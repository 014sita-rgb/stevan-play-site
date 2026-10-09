using System.IO.Compression;
using System.Text.RegularExpressions;

namespace StevanPlayer.Core;

public static class SubtitleArchive
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".vtt", ".ass", ".ssa" };

    public static string Normalize(SubtitlePayload payload, MediaIdentity? media = null)
    {
        if (payload.Content.Length > 8_000_000) throw new InvalidDataException("Преузети титл је превелик.");
        var name = payload.FileName;
        var bytes = payload.Content;
        if (bytes.Length > 3 && bytes[0] == 'P' && bytes[1] == 'K')
        {
            using var input = new MemoryStream(bytes);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            if (archive.Entries.Count > 500) throw new InvalidDataException("Архива има превише датотека.");
            var entries = archive.Entries.Where(x => Supported.Contains(Path.GetExtension(x.Name))).ToArray();
            if (entries.Length == 0) throw new InvalidDataException("Архива не садржи подржани титл (SRT/VTT/ASS/SSA).");
            ZipArchiveEntry entry;
            if (media?.Season is int season && media.Episode is int episode)
            {
                var matches = entries.Where(x => Regex.IsMatch(x.Name,
                    $@"(?i)(?:^|[^a-z0-9])(?:S{season:00}[ ._\-]*E{episode:00}|{season}x{episode:00})(?=$|[^0-9])")).ToArray();
                if (matches.Length == 0 && entries.Length > 1)
                    throw new InvalidDataException("Пакет титлова не садржи јасно означену тражену епизоду.");
                entry = matches.FirstOrDefault() ?? entries[0];
                var parsed = MediaIdentifier.Parse(entry.Name);
                if (parsed.Episode is int other && (other != episode || parsed.Season != season))
                    throw new InvalidDataException("Титл у архиви припада другој епизоди.");
            }
            else entry = entries.OrderByDescending(x => Path.GetExtension(x.Name).Equals(".srt", StringComparison.OrdinalIgnoreCase)).First();
            if (entry.Length > 8_000_000) throw new InvalidDataException("Распаковани титл је превелик.");
            using var stream = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                if (output.Length + count > 8_000_000) throw new InvalidDataException("Распаковани титл је превелик.");
                output.Write(buffer, 0, count);
            }
            bytes = output.ToArray();
            name = entry.Name;
        }
        var extension = Path.GetExtension(name);
        if (!Supported.Contains(extension)) throw new InvalidDataException("Неподржан формат титла.");
        return SubtitleFormat.ToSrt(SubtitleFormat.Parse(SubtitleFormat.Decode(bytes), extension));
    }
}
