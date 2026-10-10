using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace StevanPlayer.Core.Generation;

/// <summary>
/// Downloads public models from their official repositories, pins each download to the
/// repository revision and verifies its LFS SHA-256. Supply an HttpClient with automatic
/// redirects disabled; no account credentials are used. Verification before inference is
/// independent of the network and checks the installed files against the saved receipt.
/// </summary>
public sealed class LocalModelManager
{
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private const int MaximumReceiptBytes = 16 * 1024;
    private const long MaximumModelBytes = 6L * 1024 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private static readonly ModelDefinition Speech = new("ggerganov/whisper.cpp", "ggml-small.bin", "Whisper small");
    private static readonly ModelDefinition Translation = new("Qwen/Qwen3-4B-GGUF", "Qwen3-4B-Q4_K_M.gguf", "Qwen3 4B");
    private static readonly ModelDefinition[] Definitions = [Speech, Translation];
    private readonly HttpClient httpClient;
    private readonly string directory;
    private readonly SemaphoreSlim operation = new(1, 1);

    public LocalModelManager(HttpClient httpClient, string directory)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.httpClient = httpClient;
        this.directory = Path.GetFullPath(directory);
        EnsureNoCredentials();
    }

    public string SpeechModelPath => Path.Combine(directory, Speech.FileName);
    public string TranslationModelPath => Path.Combine(directory, Translation.FileName);
    private string ReceiptPath => Path.Combine(directory, "local-models.json");

    /// <summary>Fast availability check. Call VerifyAsync before using the models.</summary>
    public bool IsInstalled
    {
        get
        {
            try
            {
                var receipt = ReadReceipt();
                return Definitions.All(definition =>
                {
                    var model = FindReceipt(receipt, definition);
                    var file = new FileInfo(Path.Combine(directory, definition.FileName));
                    return model is not null && file.Exists && file.LinkTarget is null && file.Length == model.Size;
                });
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
        }
    }

    public async Task DownloadAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            ModelReceiptFile receipt;
            try { receipt = ReadReceipt(); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
                receipt = new ModelReceiptFile(1, []);
            }
            foreach (var definition in Definitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var existing = FindReceipt(receipt, definition);
                if (existing is not null && await FileMatchesAsync(definition, existing, cancellationToken).ConfigureAwait(false))
                {
                    progress?.Report($"Model {definition.DisplayName} je već proveren.");
                    continue;
                }
                progress?.Report($"Provera zvaničnih podataka modela {definition.DisplayName}…");
                var model = await GetModelMetadataAsync(definition, cancellationToken).ConfigureAwait(false);
                CheckFreeSpace(model.Size);
                await DownloadModelAsync(definition, model, progress, cancellationToken).ConfigureAwait(false);
                receipt.Models.RemoveAll(item => string.Equals(item.Repository, definition.Repository, StringComparison.OrdinalIgnoreCase));
                receipt.Models.Add(model);
                await SaveReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
            }
            progress?.Report("Lokalni modeli su preuzeti i provereni.");
        }
        finally { operation.Release(); }
    }

    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModelReceiptFile receipt;
            try { receipt = ReadReceipt(); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                throw new InvalidOperationException("Nedostaje ispravna potvrda lokalnih modela. Ponovo preuzmite modele.", exception);
            }
            foreach (var definition in Definitions)
            {
                var model = FindReceipt(receipt, definition);
                if (model is null || !await FileMatchesAsync(definition, model, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException($"Model {definition.DisplayName} nedostaje ili njegova provera nije prošla. Ponovo preuzmite modele.");
            }
        }
        finally { operation.Release(); }
    }

    private async Task<ModelReceipt> GetModelMetadataAsync(ModelDefinition definition, CancellationToken cancellationToken)
    {
        var endpoint = new Uri($"https://huggingface.co/api/models/{definition.Repository}?blobs=true");
        using var response = await GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response, MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("sha", out var sha) || sha.ValueKind != JsonValueKind.String || !IsHex(sha.GetString(), 40) ||
            !root.TryGetProperty("siblings", out var siblings) || siblings.ValueKind != JsonValueKind.Array)
            throw MetadataError(definition);
        if (root.TryGetProperty("id", out var id) &&
            (id.ValueKind != JsonValueKind.String || !string.Equals(id.GetString(), definition.Repository, StringComparison.OrdinalIgnoreCase)))
            throw MetadataError(definition);

        // Model repositories have used different filename casing. Accept only one exact
        // basename match, never a path guessed from a webpage or supplied by a user.
        var matches = siblings.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("rfilename", out var filename) && filename.ValueKind == JsonValueKind.String &&
            string.Equals(filename.GetString(), definition.FileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || !matches[0].TryGetProperty("lfs", out var lfs) || lfs.ValueKind != JsonValueKind.Object ||
            !lfs.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number ||
            !size.TryGetInt64(out var length) || length is <= 0 or > MaximumModelBytes)
            throw MetadataError(definition);
        string? hash = null;
        if (lfs.TryGetProperty("sha256", out var hashElement) && hashElement.ValueKind == JsonValueKind.String)
            hash = hashElement.GetString();
        else if (lfs.TryGetProperty("oid", out hashElement) && hashElement.ValueKind == JsonValueKind.String)
            hash = hashElement.GetString();
        if (hash?.StartsWith("sha256:", StringComparison.Ordinal) == true)
            hash = hash[7..];
        if (!IsHex(hash, 64))
            throw MetadataError(definition);
        return new ModelReceipt(definition.Repository, sha.GetString()!.ToLowerInvariant(),
            matches[0].GetProperty("rfilename").GetString()!, length, hash!.ToLowerInvariant());
    }

    private async Task DownloadModelAsync(ModelDefinition definition, ModelReceipt model, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri($"https://huggingface.co/{model.Repository}/resolve/{model.Revision}/{Uri.EscapeDataString(model.FileName)}");
        var partial = Path.Combine(directory, ".model-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using var response = await GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != model.Size)
                throw new InvalidDataException($"Veličina modela {definition.DisplayName} se ne slaže sa zvaničnim podacima.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                long total = 0;
                var lastPercent = -1;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > model.Size || total > MaximumModelBytes)
                        throw new InvalidDataException($"Preuzeti model {definition.DisplayName} prelazi dozvoljenu veličinu.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    var percent = (int)(total * 100 / model.Size);
                    if (percent != lastPercent)
                    {
                        progress?.Report($"Preuzimanje modela {definition.DisplayName}: {percent}% ({total / (1024 * 1024)} / {model.Size / (1024 * 1024)} MiB)…");
                        lastPercent = percent;
                    }
                }
                if (total != model.Size || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), model.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SHA-256 provera modela {definition.DisplayName} nije prošla. Model nije instaliran.");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, Path.Combine(directory, definition.FileName), overwrite: true);
        }
        finally { DeletePartial(partial); }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects <= MaximumRedirects; redirects++)
        {
            EnsureNoCredentials();
            ValidateEndpoint(endpoint);
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.UserAgent.ParseAdd("STEVANplejer/1.0");
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            try
            {
                // Also detect unsafe final URLs when an incorrectly configured injected
                // transport followed redirects itself. Production disables those redirects.
                if (response.RequestMessage?.RequestUri is Uri finalUri)
                    ValidateEndpoint(finalUri);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location;
                    if (location is null || redirects == MaximumRedirects)
                        throw new InvalidDataException("Servis modela je vratio neispravno ili previše preusmerenja.");
                    endpoint = location.IsAbsoluteUri ? location : new Uri(endpoint, location);
                    ValidateEndpoint(endpoint);
                    response.Dispose();
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Preuzimanje javnih modela nije uspelo (HTTP {(int)response.StatusCode}). Modeli ne zahtevaju API ključ.", null, response.StatusCode);
                if (response.Content.Headers.ContentEncoding.Any(value => !string.Equals(value, "identity", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Servis modela je vratio nepodržano kodiranje sadržaja.");
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        throw new InvalidDataException("Servis modela je vratio previše preusmerenja.");
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        var host = endpoint.IdnHost;
        if (endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Port != 443 || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            !(string.Equals(host, "huggingface.co", StringComparison.OrdinalIgnoreCase) ||
              host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) ||
              host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Preuzimanje modela je pokušalo pristup neodobrenoj adresi.");
    }

    private void EnsureNoCredentials()
    {
        if (httpClient.DefaultRequestHeaders.Authorization is not null ||
            new[] { "Cookie", "Api-Key", "X-Api-Key", "token", "Proxy-Authorization" }.Any(httpClient.DefaultRequestHeaders.Contains))
            throw new InvalidOperationException("Preuzimanje javnih modela koristi zasebnu vezu bez naloga i ključeva.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximum)
            throw new InvalidDataException("Podaci javnog modela prelaze dozvoljenu veličinu.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > maximum)
                throw new InvalidDataException("Podaci javnog modela prelaze dozvoljenu veličinu.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private ModelReceiptFile ReadReceipt()
    {
        var file = new FileInfo(ReceiptPath);
        if (!file.Exists)
            return new ModelReceiptFile(1, []);
        if (file.LinkTarget is not null || file.Length is <= 0 or > MaximumReceiptBytes)
            throw new InvalidDataException("Potvrda lokalnih modela nije ispravna.");
        using var input = new FileStream(ReceiptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var receipt = JsonSerializer.Deserialize<ModelReceiptFile>(input, new JsonSerializerOptions { MaxDepth = 16 });
        if (receipt is null || receipt.Version != 1 || receipt.Models is null || receipt.Models.Count > Definitions.Length ||
            receipt.Models.Any(model => model is null || !Definitions.Any(definition => ValidReceipt(model, definition))) ||
            receipt.Models.Select(model => model.Repository).Distinct(StringComparer.OrdinalIgnoreCase).Count() != receipt.Models.Count)
            throw new InvalidDataException("Potvrda lokalnih modela nije ispravna.");
        return receipt;
    }

    private async Task SaveReceiptAsync(ModelReceiptFile receipt, CancellationToken cancellationToken)
    {
        var partial = Path.Combine(directory, ".receipt-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, receipt, cancellationToken: cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, ReceiptPath, overwrite: true);
        }
        finally { DeletePartial(partial); }
    }

    private async Task<bool> FileMatchesAsync(ModelDefinition definition, ModelReceipt receipt, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, definition.FileName);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null || file.Length != receipt.Size)
                return false;
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
            return input.Length == receipt.Size && string.Equals(Convert.ToHexString(hash), receipt.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void CheckFreeSpace(long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(directory);
            if (root is not null && new DriveInfo(root).AvailableFreeSpace < requiredBytes + 64L * 1024 * 1024)
                throw new InvalidOperationException("Nema dovoljno slobodnog prostora za lokalni model i njegovu proveru.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Some virtual filesystems cannot report space. Streaming and size limits remain
            // enforced, and disk-full errors leave no partially installed model.
        }
    }

    private static ModelReceipt? FindReceipt(ModelReceiptFile receipt, ModelDefinition definition) =>
        receipt.Models.SingleOrDefault(model => ValidReceipt(model, definition));

    private static bool ValidReceipt(ModelReceipt model, ModelDefinition definition) =>
        string.Equals(model.Repository, definition.Repository, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(model.FileName, definition.FileName, StringComparison.OrdinalIgnoreCase) &&
        IsHex(model.Revision, 40) && IsHex(model.Sha256, 64) && model.Size is > 0 and <= MaximumModelBytes;

    private static bool IsHex(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);
    private static InvalidDataException MetadataError(ModelDefinition definition) =>
        new($"Zvanični podaci modela {definition.DisplayName} nemaju jedinstvenu datoteku, fiksnu reviziju ili važeću LFS SHA-256 potvrdu. Neproveren model nije preuzet.");
    private static void DeletePartial(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record ModelDefinition(string Repository, string FileName, string DisplayName);
    private sealed record ModelReceipt(string Repository, string Revision, string FileName, long Size, string Sha256);
    private sealed record ModelReceiptFile(int Version, List<ModelReceipt> Models);
}
