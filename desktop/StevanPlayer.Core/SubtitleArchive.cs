using System.IO.Compression;
using System.Text.RegularExpressions;

namespace StevanPlayer.Core;

public static class SubtitleArchive
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".vtt", ".ass", ".ssa" };

    public static string Normalize(SubtitlePayload payload, MediaIdentity? media = null, string? language = null)
    {
        if (language is not null)
            language = Providers.ProviderHttp.Language(language) ??
                throw new ArgumentException("Подржани језици титла су српски, хрватски и босански.", nameof(language));
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
            var eligible = entries;
            if (media?.Season is int season && media.Episode is int episode)
            {
                var matches = entries.Where(x => MatchesEpisode(x.Name, season, episode)).ToArray();
                if (matches.Length == 0 && entries.Length > 1)
                    throw new InvalidDataException("Пакет титлова не садржи јасно означену тражену епизоду.");
                if (matches.Length > 0) eligible = matches;
            }
            var entry = SelectMember(eligible, language);
            if (media?.Season is int expectedSeason && media.Episode is int expectedEpisode)
            {
                var parsed = MediaIdentifier.Parse(entry.Name);
                if (parsed.Episode is int other && (other != expectedEpisode || parsed.Season != expectedSeason))
                    throw new InvalidDataException("Титл у архиви припада другој епизоди.");
            }
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

    private static bool MatchesEpisode(string name, int season, int episode)
    {
        // Match the same digit widths as MediaIdentifier rather than requiring zero padding.
        // Preserve archive separators such as parentheses as well as the usual dot/space forms.
        var match = Regex.Match(name,
            @"(?i)(?:^|[^a-z0-9])(?:S(?<s>\d{1,2})[ ._\-]*E(?<e>\d{1,3})|(?<s>\d{1,2})x(?<e>\d{1,3}))(?=$|[^0-9])");
        return match.Success && int.Parse(match.Groups["s"].Value) == season &&
            int.Parse(match.Groups["e"].Value) == episode;
    }

    private static ZipArchiveEntry SelectMember(ZipArchiveEntry[] entries, string? language)
    {
        if (language is not null)
        {
            var labelled = entries.Select(entry => (Entry: entry, Language: MemberLanguage(entry.FullName))).ToArray();
            var requested = labelled.Where(item => item.Language == language).Select(item => item.Entry).ToArray();
            // Prefer an explicit language label to an unlabelled member. If the provider's
            // language is not marked in the ZIP, its metadata can still identify an unlabelled file.
            entries = requested.Length > 0 ? requested :
                labelled.Where(item => item.Language is null).Select(item => item.Entry).ToArray();
            if (entries.Length == 0)
                throw new InvalidDataException("Архива не садржи титл на језику изабраног резултата.");
        }
        if (entries.GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("Архива садржи више различитих титлова са истим именом.");
        return entries.OrderByDescending(entry => Path.GetExtension(entry.Name).Equals(".srt", StringComparison.OrdinalIgnoreCase))
            .ThenBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.FullName, StringComparer.Ordinal).First();
    }

    private static string? MemberLanguage(string fullName)
    {
        var components = fullName.Replace('\\', '/').Split('/');
        var stem = Path.GetFileNameWithoutExtension(components[^1]);
        // Language suffixes and language-only folders are labels. Words in the movie title
        // (for example "English.Patient.1996") must not be mistaken for a language label.
        var language = LanguageSuffix(stem);
        if (language is not null) return language;
        for (int i = components.Length - 2; i >= 0; i--)
        {
            language = KnownLanguage(components[i].Trim(' ', '[', ']', '(', ')'));
            if (language is not null) return language;
        }
        return null;
    }

    private static string? LanguageSuffix(string stem)
    {
        while (true)
        {
            var stripped = Regex.Replace(stem, @"(?i)(?:[ ._\-\[(])(?:sdh|cc|forced)(?:[\])\s]*)$", "");
            if (stripped == stem) break;
            stem = stripped.TrimEnd(' ', '.', '_', '-', '[', ']', '(', ')');
        }
        var match = Regex.Match(stem, @"(?i)(?:^|[ ._\-\[(])(?<language>sr-cyrl|sr-latn|[\p{L}]+)(?:[\])\s]*)$");
        if (!match.Success) return null;
        var tag = match.Groups["language"].Value;
        var language = KnownLanguage(tag);
        // A short foreign code used as the complete movie title (e.g. "It.srt") is ambiguous.
        return match.Index == 0 && tag.Length == 2 && language is not ("sr" or "hr" or "bs") ? null : language;
    }

    private static string? KnownLanguage(string tag) => Providers.ProviderHttp.Language(tag) ?? (tag.ToLowerInvariant() switch
    {
        "sr-latn" or "ћирилица" or "ćirilica" => "sr",
        "en" or "eng" or "english" => "en",
        "de" or "deu" or "ger" or "german" => "de",
        "fr" or "fra" or "fre" or "french" => "fr",
        "es" or "spa" or "spanish" => "es",
        "it" or "ita" or "italian" => "it",
        "pt" or "por" or "portuguese" => "pt",
        "ru" or "rus" or "russian" => "ru",
        "bg" or "bul" or "bulgarian" => "bg",
        "mk" or "mkd" or "macedonian" => "mk",
        "sl" or "slv" or "slovenian" => "sl",
        "sq" or "sqi" or "alb" or "albanian" => "sq",
        "tr" or "tur" or "turkish" => "tr",
        "hu" or "hun" or "hungarian" => "hu",
        "ro" or "ron" or "rum" or "romanian" => "ro",
        "el" or "ell" or "gre" or "greek" => "el",
        "pl" or "pol" or "polish" => "pl",
        "cs" or "ces" or "cze" or "czech" => "cs",
        "sk" or "slk" or "slo" or "slovak" => "sk",
        "nl" or "nld" or "dut" or "dutch" => "nl",
        "sv" or "swe" or "swedish" => "sv",
        "no" or "nor" or "norwegian" => "no",
        "da" or "dan" or "danish" => "da",
        "fi" or "fin" or "finnish" => "fi",
        "uk" or "ukr" or "ukrainian" => "uk",
        "ar" or "ara" or "arabic" => "ar",
        "he" or "heb" or "hebrew" => "he",
        "zh" or "zho" or "chi" or "chinese" => "zh",
        "ja" or "jpn" or "japanese" => "ja",
        "ko" or "kor" or "korean" => "ko",
        _ => null
    });
}
