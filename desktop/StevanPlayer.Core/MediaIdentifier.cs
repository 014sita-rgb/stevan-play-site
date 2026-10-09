using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace StevanPlayer.Core;

public static partial class MediaIdentifier
{
    [GeneratedRegex(@"(?i)(?:^|[ ._\-])(?:S(?<s>\d{1,2})[ ._\-]*E(?<e>\d{1,3})|(?<s>\d{1,2})x(?<e>\d{1,3}))(?=$|[^0-9])")]
    private static partial Regex EpisodePattern();
    [GeneratedRegex(@"(?:^|[ ._\-(])(?<y>19\d{2}|20\d{2})(?=$|[ ._\-)])")]
    private static partial Regex YearPattern();
    [GeneratedRegex(@"(?i)(?:^|[ ._\-])(?:2160p|1080p|720p|480p|WEB[ ._\-]?DL|WEBRip|BluRay|BDRip|DVDRip|HDTV|x26[45]|HEVC)(?:$|[ ._\-])")]
    private static partial Regex ReleasePattern();

    public static MediaIdentity Parse(string path, long fileSize = 0)
    {
        var source = Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last()) : Path.GetFileName(path);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var name = extension is ".mkv" or ".mp4" or ".avi" or ".mov" or ".webm" or ".m4v" or ".ts" or
            ".wmv" or ".mpg" or ".mpeg" or ".flv" or ".ogg" or ".mp3" or ".wav" or ".srt" or ".vtt" or
            ".ass" or ".ssa" or ".sub" or ".zip" ? Path.GetFileNameWithoutExtension(source) : source;
        var episode = EpisodePattern().Match(name);
        var year = YearPattern().Matches(name).LastOrDefault(x => x.Index > 0);
        var release = ReleasePattern().Match(name);
        var cut = name.Length;
        if (episode.Success) cut = Math.Min(cut, episode.Index);
        if (year is not null) cut = Math.Min(cut, year.Index);
        if (release.Success && release.Index > 0) cut = Math.Min(cut, release.Index);
        var title = Regex.Replace(name[..cut], @"[._]+", " ").Trim(' ', '-', '(', ')', '[', ']');
        if (title.Length == 0) title = name;
        return new(path, title, year is not null ? int.Parse(year.Groups["y"].Value) : null,
            episode.Success ? int.Parse(episode.Groups["s"].Value) : null,
            episode.Success ? int.Parse(episode.Groups["e"].Value) : null, fileSize);
    }

    public static async Task<MediaIdentity> IdentifyAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            return Parse(path);
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Видео датотека није пронађена.", path);
        var identity = Parse(file.FullName, file.Length);
        return identity with { OpenSubtitlesHash = await ComputeHashAsync(file.FullName, cancellationToken) };
    }

    public static async Task<string?> ComputeHashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (input.Length < 131072) return null;
        var buffer = new byte[65536];
        var hash = (ulong)input.Length;
        await input.ReadExactlyAsync(buffer, cancellationToken);
        for (var i = 0; i < buffer.Length; i += 8)
            hash = unchecked(hash + BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(i, 8)));
        input.Seek(-65536, SeekOrigin.End);
        await input.ReadExactlyAsync(buffer, cancellationToken);
        for (var i = 0; i < buffer.Length; i += 8)
            hash = unchecked(hash + BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(i, 8)));
        return hash.ToString("x16");
    }

    public static string? FindLocalSubtitle(string mediaPath, string preferredLanguage)
    {
        if (!File.Exists(mediaPath)) return null;
        var stem = Path.GetFileNameWithoutExtension(mediaPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(mediaPath))!;
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".srt", ".ass", ".ssa", ".vtt" };
        var candidates = Directory.EnumerateFiles(directory).Where(x => extensions.Contains(Path.GetExtension(x)));
        return candidates.OrderByDescending(x => Path.GetFileNameWithoutExtension(x).Equals(stem + "." + preferredLanguage,
                StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(x => Path.GetFileNameWithoutExtension(x).Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(Path.GetFileNameWithoutExtension(x), "^" + Regex.Escape(stem) +
                    @"[._ -](sr|srp|serbian|hr|hrv|croatian|bs|bos|bosnian)$", RegexOptions.IgnoreCase));
    }
}
