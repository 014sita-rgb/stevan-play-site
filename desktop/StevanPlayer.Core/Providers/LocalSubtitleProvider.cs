using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace StevanPlayer.Core.Providers;

/// <summary>
/// Searches the video's directory and explicitly supplied directories, without accounts or a network.
/// It never recursively scans a user's files. Only an identified search result can subsequently be read.
/// </summary>
public sealed class LocalSubtitleProvider : ISubtitleProvider
{
    private const int MaximumBytes = 8_000_000;
    private const int MaximumRoots = 16;
    private const int MaximumDirectoryEntries = 2_000;
    private const int MaximumArchiveEntries = 500;
    private const int MaximumArchives = 32;
    private const int MaximumCandidates = 120;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase)
        { ".srt", ".vtt", ".ass", ".ssa" };
    private static readonly Dictionary<string, string> LanguageLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sr"] = "sr", ["srp"] = "sr", ["serbian"] = "sr", ["srpski"] = "sr",
        ["sr-latn"] = "sr", ["sr-cyrl"] = "sr", ["cirilica"] = "sr", ["ćirilica"] = "sr", ["ћирилица"] = "sr",
        ["hr"] = "hr", ["hrv"] = "hr", ["croatian"] = "hr", ["hrvatski"] = "hr",
        ["bs"] = "bs", ["bos"] = "bs", ["bosnian"] = "bs", ["bosanski"] = "bs",
        ["en"] = "other", ["eng"] = "other", ["english"] = "other",
        ["de"] = "other", ["deu"] = "other", ["ger"] = "other", ["german"] = "other",
        ["fr"] = "other", ["fra"] = "other", ["fre"] = "other", ["french"] = "other",
        ["es"] = "other", ["spa"] = "other", ["spanish"] = "other",
        ["it"] = "other", ["ita"] = "other", ["italian"] = "other",
        ["pt"] = "other", ["por"] = "other", ["portuguese"] = "other",
        ["ru"] = "other", ["rus"] = "other", ["russian"] = "other",
        ["sl"] = "other", ["slv"] = "other", ["slovenian"] = "other",
        ["mk"] = "other", ["mkd"] = "other", ["macedonian"] = "other",
        ["bg"] = "other", ["bul"] = "other", ["bulgarian"] = "other",
        ["tr"] = "other", ["tur"] = "other", ["turkish"] = "other",
        ["hu"] = "other", ["hun"] = "other", ["hungarian"] = "other",
        ["ro"] = "other", ["ron"] = "other", ["romanian"] = "other",
        ["pl"] = "other", ["pol"] = "other", ["polish"] = "other",
        ["ar"] = "other", ["ara"] = "other", ["arabic"] = "other",
        ["zh"] = "other", ["chi"] = "other", ["chinese"] = "other",
        ["ja"] = "other", ["jpn"] = "other", ["japanese"] = "other"
    };
    private static readonly string[] OrderedLabels = LanguageLabels.Keys.OrderByDescending(label => label.Length).ToArray();
    private readonly string[] _roots;
    private Dictionary<string, TrustedFile> _identified = new(StringComparer.Ordinal);
    private ProviderNotice[] _notices = [];

    public LocalSubtitleProvider(IEnumerable<string>? roots = null)
    {
        _roots = (roots ?? []).Take(MaximumRoots).Select(FullPath).OfType<string>()
            .Distinct(PathComparer).ToArray();
    }

    public string Id => "Local";
    public bool IsConfigured(PlayerSettings settings) => true;
    public IReadOnlyList<ProviderNotice> LastNotices => Volatile.Read(ref _notices);

    public Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(MediaIdentity media, PlayerSettings settings,
        CancellationToken cancellationToken) => Task.Run(() => Search(media, settings, cancellationToken), cancellationToken);

    private IReadOnlyList<SubtitleCandidate> Search(MediaIdentity media, PlayerSettings settings,
        CancellationToken cancellationToken)
    {
        var candidates = new List<SubtitleCandidate>();
        var identified = new Dictionary<string, TrustedFile>(StringComparer.Ordinal);
        var notices = new List<ProviderNotice>();
        var mediaPath = IsWebAddress(media.FilePath) ? null : FullPath(media.FilePath);
        var mediaRoot = mediaPath is null ? null : Path.GetDirectoryName(mediaPath);
        var roots = (mediaRoot is null ? _roots : new[] { mediaRoot }.Concat(_roots))
            .Distinct(PathComparer).Take(MaximumRoots + 1);
        var mediaStem = Path.GetFileNameWithoutExtension(mediaPath ?? media.FilePath);
        var preferredLanguage = ProviderHttp.Language(settings.PreferredLanguage) ?? "sr";
        var archives = 0;
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(root))
                {
                    if (File.Exists(root)) notices.Add(Notice("Jedna lokacija za titlove nije direktorijum i preskočena je."));
                    continue;
                }
                if (HasLinkedDirectory(root)) continue;
                var scanned = 0;
                foreach (var path in Directory.EnumerateFileSystemEntries(root).Take(MaximumDirectoryEntries + 1))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (scanned++ >= MaximumDirectoryEntries)
                    {
                        notices.Add(Notice("Pretraga jednog direktorijuma ograničena je na 2.000 stavki. Premestite titl pored video datoteke."));
                        break;
                    }
                    if (candidates.Count >= MaximumCandidates) break;
                    try
                    {
                        var extension = Path.GetExtension(path);
                        var isArchive = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
                        if (!Formats.Contains(extension) && !isArchive || IsLink(path)) continue;
                        var file = new FileInfo(path);
                        if (!file.Exists || file.Length is <= 0 or > MaximumBytes) continue;
                        var stem = Path.GetFileNameWithoutExtension(path);
                        var suffixLanguage = SplitLanguage(ref stem);
                        if (IsPartial(stem) || suffixLanguage == "other") continue;
                        var folderLanguage = FolderLanguage(root);
                        var sameRelease = SameRelease(stem, mediaStem);
                        if (!Matches(stem, media, sameRelease)) continue;
                        var language = suffixLanguage ?? folderLanguage;
                        if (language == "other") continue;
                        if (isArchive)
                        {
                            if (archives++ >= MaximumArchives) continue;
                            foreach (var member in ArchiveMembers(path, media, mediaStem, language, preferredLanguage, cancellationToken))
                            {
                                Add(path, root, file, member.Language, member.Stem, member.FullName);
                                if (candidates.Count >= MaximumCandidates) break;
                            }
                        }
                        else if (language is not null || sameRelease)
                            Add(path, root, file, language ?? preferredLanguage, stem, null);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        notices.Add(Notice("Jedan lokalni titl nije dostupan ili njegova arhiva nije ispravna; preskočen je."));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notices.Add(Notice("Jedan direktorijum za titlove nije dostupan i preskočen je."));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _identified, identified);
        Volatile.Write(ref _notices, notices.Distinct().Take(MaximumRoots + 1).ToArray());
        return candidates;

        void Add(string path, string root, FileInfo file, string language, string release, string? member)
        {
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + "\n" + member + "\n" + language))).ToLowerInvariant();
            var candidate = new SubtitleCandidate(id, Id, language, release, path,
                false, media.Season, media.Episode);
            if (!identified.TryAdd(id, new(candidate, root, file.Length, file.LastWriteTimeUtc, member))) return;
            candidates.Add(candidate);
        }
    }

    public async Task<SubtitlePayload> DownloadAsync(SubtitleCandidate candidate, PlayerSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.ProviderId != Id || !Volatile.Read(ref _identified).TryGetValue(candidate.Id, out var trusted) ||
            !PathComparer.Equals(candidate.DownloadReference, trusted.Candidate.DownloadReference) ||
            candidate.Language != trusted.Candidate.Language || candidate.Season != trusted.Candidate.Season ||
            candidate.Episode != trusted.Candidate.Episode)
            throw InvalidCandidate();
        var path = FullPath(candidate.DownloadReference);
        if (path is null || !PathComparer.Equals(Path.GetDirectoryName(path), trusted.Root) ||
            !Formats.Contains(Path.GetExtension(path)) && !Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw InvalidCandidate();
        try
        {
            var info = new FileInfo(path);
            if (IsLink(path) || HasLinkedDirectory(trusted.Root) || !info.Exists || info.Length != trusted.Length ||
                info.LastWriteTimeUtc != trusted.LastWriteTimeUtc || info.Length is <= 0 or > MaximumBytes)
                throw InvalidCandidate();
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            if (input.Length != trusted.Length) throw InvalidCandidate();
            var bytes = new byte[(int)input.Length];
            await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (trusted.Member is null) return new(Path.GetFileName(path), bytes);
            using var memory = new MemoryStream(bytes);
            using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
            if (archive.Entries.Count > MaximumArchiveEntries) throw InvalidCandidate();
            var members = archive.Entries.Where(entry => entry.FullName.Equals(trusted.Member, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (members.Length != 1 || members[0].Length is <= 0 or > MaximumBytes) throw InvalidCandidate();
            var entry = members[0];
            using var source = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > MaximumBytes) throw InvalidCandidate();
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(entry.Name, output.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new ProviderException(Id, ProviderFailure.Unavailable,
                "Lokalni titl nije dostupan ili je promenjen. Ponovite pretragu titlova.");
        }
    }

    private static IEnumerable<ArchiveMember> ArchiveMembers(string path, MediaIdentity media, string mediaStem,
        string? containerLanguage, string preferredLanguage, CancellationToken cancellationToken)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaximumBytes) throw new InvalidDataException();
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaximumArchiveEntries) throw new InvalidDataException();
        var results = new List<ArchiveMember>();
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Formats.Contains(Path.GetExtension(entry.Name)) || entry.Length is <= 0 or > MaximumBytes || !SafeMember(entry.FullName)) continue;
            var stem = Path.GetFileNameWithoutExtension(entry.Name);
            var language = SplitLanguage(ref stem) ?? ArchiveFolderLanguage(entry.FullName) ?? containerLanguage;
            var sameRelease = SameRelease(stem, mediaStem);
            if (language == "other" || IsPartial(stem) || !Matches(stem, media, sameRelease) || language is null && !sameRelease) continue;
            results.Add(new(entry.FullName, stem, language ?? preferredLanguage));
        }
        if (archive.Entries.GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException();
        return results.OrderByDescending(entry => Path.GetExtension(entry.FullName).Equals(".srt", StringComparison.OrdinalIgnoreCase))
            .ThenBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).DistinctBy(entry => entry.Language).ToArray();
    }

    private static bool Matches(string stem, MediaIdentity media, bool sameRelease)
    {
        var parsed = MediaIdentifier.Parse(stem + ".srt");
        if (Normalize(parsed.Title) != Normalize(media.Title)) return false;
        if (media.Episode.HasValue)
            return parsed.Season == media.Season && parsed.Episode == media.Episode &&
                (!media.Year.HasValue || !parsed.Year.HasValue || media.Year == parsed.Year);
        if (parsed.Season.HasValue || parsed.Episode.HasValue) return false;
        return sameRelease || media.Year == parsed.Year;
    }

    private static string? SplitLanguage(ref string stem)
    {
        stem = Regex.Replace(stem, @"(?i)(?:[ ._\-\[(])(?:sdh|cc|forced)(?:[\])\s]*)$", "").TrimEnd(' ', '.', '_', '-', '[', ']', '(', ')');
        foreach (var label in OrderedLabels)
        {
            if (stem.Length <= label.Length || !stem.EndsWith(label, StringComparison.OrdinalIgnoreCase)) continue;
            var boundary = stem.Length - label.Length - 1;
            if (!" ._-[(".Contains(stem[boundary])) continue;
            stem = stem[..(boundary + 1)].TrimEnd(' ', '.', '_', '-', '[', '(', ']');
            return LanguageLabels[label];
        }
        return null;
    }

    private static string? FolderLanguage(string root) => KnownLanguage(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)));
    private static string? ArchiveFolderLanguage(string name) => name.Replace('\\', '/').Split('/').SkipLast(1)
        .Reverse().Select(KnownLanguage).FirstOrDefault(language => language is not null);
    private static string? KnownLanguage(string value) => LanguageLabels.GetValueOrDefault(value.Trim(' ', '[', ']', '(', ')'));
    private static bool SafeMember(string name) => !name.StartsWith('/') && !name.StartsWith('\\') &&
        !name.Contains(':') && !name.Replace('\\', '/').Split('/').Any(part => part is "." or "..");
    private static bool IsPartial(string stem) => stem.StartsWith('~') || stem.StartsWith('.') ||
        Regex.IsMatch(stem, @"(?i)(?:^|[ ._\-\[(])(?:tmp|temp|part|partial|crdownload|download)(?:[\])\s]*)$");
    private static bool SameRelease(string first, string second) => Normalize(first) == Normalize(second);
    private static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormD)
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool HasLinkedDirectory(string path)
    {
        for (string? directory = path; directory is not null; directory = Path.GetDirectoryName(directory))
            if (IsLink(directory)) return true;
        return false;
    }
    private static bool IsWebAddress(string path) => Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    private static string? FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsWebAddress(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private ProviderNotice Notice(string message) => new(Id, message);
    private ProviderException InvalidCandidate() => new(Id, ProviderFailure.InvalidResponse,
        "Lokalni titl nije potvrđen prethodnom pretragom. Ponovite pretragu titlova.");
    private sealed record TrustedFile(SubtitleCandidate Candidate, string Root, long Length, DateTime LastWriteTimeUtc, string? Member);
    private sealed record ArchiveMember(string FullName, string Stem, string Language);
}
