using System.Globalization;
using System.Net;
using System.Text.Json;

namespace StevanPlayer.Core.Providers;

internal static class ProviderHttp
{
    internal const int MaximumPayloadBytes = 16 * 1024 * 1024;

    internal static bool IsSafeCredential(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 &&
        !value.Any(c => char.IsControl(c));

    internal static string Query(IEnumerable<KeyValuePair<string, string?>> parameters, bool sortedFormEncoding = false)
    {
        var fields = parameters.Where(p => p.Value is not null);
        if (sortedFormEncoding) fields = fields.OrderBy(p => p.Key, StringComparer.Ordinal);
        string Encode(string value) => sortedFormEncoding ? Uri.EscapeDataString(value).Replace("%20", "+") : Uri.EscapeDataString(value);
        return string.Join('&', fields.Select(p => Encode(p.Key) + "=" + Encode(p.Value!)));
    }

    internal static async Task<JsonDocument> JsonAsync(HttpClient client, HttpRequestMessage request,
        string providerId, CancellationToken cancellationToken, Func<Uri, Uri, bool>? redirectPolicy = null)
    {
        var bytes = await BytesAsync(client, request, providerId, cancellationToken, redirectPolicy).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw new ProviderException(providerId, ProviderFailure.InvalidResponse,
                $"{providerId}: servis je vratio neispravan odgovor.");
        }
    }

    internal static async Task<byte[]> BytesAsync(HttpClient client, HttpRequestMessage request,
        string providerId, CancellationToken cancellationToken, Func<Uri, Uri, bool>? redirectPolicy = null)
    {
        HttpRequestMessage? redirected = null;
        try
        {
            for (var redirectCount = 0; ; redirectCount++)
            {
                var current = redirected ?? request;
                using var response = await client.SendAsync(current, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirectPolicy is null || redirectCount >= 3 || response.Headers.Location is not Uri location ||
                        !Uri.TryCreate(current.RequestUri!, location, out var target) ||
                        !redirectPolicy(request.RequestUri!, target))
                        throw new ProviderException(providerId, ProviderFailure.InvalidResponse,
                            $"{providerId}: nepodržano preusmeravanje ili previše preusmeravanja.");
                    var next = await RedirectRequestAsync(current, target, response.StatusCode, cancellationToken).ConfigureAwait(false);
                    redirected?.Dispose();
                    redirected = next;
                    continue;
                }
                CheckStatus(response, providerId);
                if (response.Content.Headers.ContentLength > MaximumPayloadBytes)
                    throw TooLarge(providerId);
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = new byte[32 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    if (output.Length + read > MaximumPayloadBytes)
                        throw TooLarge(providerId);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (output.Length == 0)
                    throw new ProviderException(providerId, ProviderFailure.InvalidResponse,
                        $"{providerId}: servis je vratio prazan odgovor.");
                return output.ToArray();
            }
        }
        catch (HttpRequestException)
        {
            // HttpRequestException may include SubDL's API key in the request URL.
            throw new ProviderException(providerId, ProviderFailure.Unavailable,
                $"{providerId}: veza sa servisom nije uspela. Proverite mrežu i pokušajte kasnije.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(providerId, ProviderFailure.Unavailable,
                $"{providerId}: servis nije odgovorio na vreme.");
        }
        catch (IOException)
        {
            throw new ProviderException(providerId, ProviderFailure.Unavailable,
                $"{providerId}: preuzimanje je prekinuto.");
        }
        finally { redirected?.Dispose(); }
    }

    private static async Task<HttpRequestMessage> RedirectRequestAsync(HttpRequestMessage original, Uri target,
        HttpStatusCode status, CancellationToken cancellationToken)
    {
        // 307/308 preserve the method and body. 303 explicitly requests a GET representation.
        var method = (int)status == 303 && original.Method != HttpMethod.Head ? HttpMethod.Get : original.Method;
        var next = new HttpRequestMessage(method, target);
        foreach (var header in original.Headers)
            next.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (method == original.Method && original.Content is not null)
        {
            next.Content = new ByteArrayContent(await original.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            foreach (var header in original.Content.Headers)
                next.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return next;
    }

    private static ProviderException TooLarge(string providerId) =>
        new(providerId, ProviderFailure.InvalidResponse, $"{providerId}: odgovor je veći od dozvoljenih 16 MB.");

    private static void CheckStatus(HttpResponseMessage response, string providerId)
    {
        if (response.IsSuccessStatusCode) return;
        var status = response.StatusCode;
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || (int)status == 406)
            throw new ProviderException(providerId, ProviderFailure.Authentication,
                $"{providerId}: pristup nije odobren. Proverite API ključ, nalog i dozvole servisa.");
        if ((int)status is 407 or 429)
        {
            var retry = response.Headers.RetryAfter?.Delta;
            var wait = retry.HasValue ? $" Pokušajte za {Math.Max(1, (int)Math.Ceiling(retry.Value.TotalSeconds))} s." : " Pokušajte kasnije.";
            throw new ProviderException(providerId, ProviderFailure.Quota,
                $"{providerId}: dostignuto je ograničenje zahteva ili preuzimanja." + wait);
        }
        throw new ProviderException(providerId,
            (int)status >= 500 ? ProviderFailure.Unavailable : ProviderFailure.InvalidResponse,
            $"{providerId}: servis je vratio HTTP {(int)status}.");
    }

    internal static string? String(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    internal static int? Integer(JsonElement element, string property) =>
        int.TryParse(String(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : null;

    internal static bool Boolean(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number != 0 ||
         value.ValueKind == JsonValueKind.String && value.GetString() is "true" or "1");

    internal static string? Language(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "sr" or "srp" or "serbian" or "srpski" or "cirilica" or "sr-cyrl" => "sr",
        "hr" or "hrv" or "croatian" or "hrvatski" => "hr",
        "bs" or "bos" or "bosnian" or "bosanski" => "bs",
        _ => null
    };

    internal static Uri TrustedDownloadUri(string? value, string providerId, params string[] domains)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort ||
            !domains.Any(domain => string.Equals(uri.IdnHost, domain, StringComparison.OrdinalIgnoreCase) ||
                uri.IdnHost.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase)))
            throw new ProviderException(providerId, ProviderFailure.InvalidResponse,
                $"{providerId}: servis je vratio nepodržanu adresu za preuzimanje.");
        return uri;
    }

    internal static string SafeFileName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var final = name.Replace('\\', '/').Split('/').Last();
        return final.Length is > 0 and <= 200 && !final.Any(char.IsControl) ? final : fallback;
    }
}
