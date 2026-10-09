using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StevanPlayer.Core;

public static partial class SubtitleFormat
{
    [GeneratedRegex(@"(?m)^\s*(?<start>(?:\d{1,3}:)?\d{2}:\d{2}[,.]\d{1,3})\s*-->\s*(?<end>(?:\d{1,3}:)?\d{2}:\d{2}[,.]\d{1,3})[^\r\n]*\r?\n(?<text>.*?)(?=\r?\n\s*\r?\n|\z)", RegexOptions.Singleline)]
    private static partial Regex CuePattern();

    public static IReadOnlyList<SubtitleCue> Parse(string text, string extension = ".srt")
    {
        if (text.Length > 8_000_000) throw new InvalidDataException("Титл је превелик.");
        if (extension.Equals(".ass", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase))
            return ParseAss(text);
        var cues = new List<SubtitleCue>();
        foreach (Match match in CuePattern().Matches(text.Replace("\r\n", "\n").Replace('\r', '\n')))
        {
            var start = ParseTime(match.Groups["start"].Value);
            var end = ParseTime(match.Groups["end"].Value);
            var caption = match.Groups["text"].Value.Trim();
            if (end > start && caption.Length > 0) cues.Add(new(start, end, caption));
        }
        if (cues.Count == 0) throw new InvalidDataException("Датотека нема исправне временски означене титлове.");
        return cues.OrderBy(x => x.Start).ToArray();
    }

    private static IReadOnlyList<SubtitleCue> ParseAss(string text)
    {
        var cues = new List<SubtitleCue>();
        string[]? fields = null;
        var inEvents = false;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
            if (!inEvents) continue;
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                fields = line[7..].Split(',').Select(x => x.Trim()).ToArray();
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) || fields is null) continue;
            var values = line[9..].TrimStart().Split(',', fields.Length);
            if (values.Length != fields.Length) continue;
            var startIndex = Array.FindIndex(fields, x => x.Equals("Start", StringComparison.OrdinalIgnoreCase));
            var endIndex = Array.FindIndex(fields, x => x.Equals("End", StringComparison.OrdinalIgnoreCase));
            var textIndex = Array.FindIndex(fields, x => x.Equals("Text", StringComparison.OrdinalIgnoreCase));
            if (startIndex < 0 || endIndex < 0 || textIndex < 0) continue;
            var start = ParseTime(values[startIndex]);
            var end = ParseTime(values[endIndex]);
            var caption = Regex.Replace(values[textIndex], @"\{[^}]*\}", "").Replace(@"\N", "\n").Replace(@"\n", "\n");
            if (end > start && !string.IsNullOrWhiteSpace(caption)) cues.Add(new(start, end, caption));
        }
        if (cues.Count == 0) throw new InvalidDataException("ASS/SSA датотека нема исправне титлове.");
        return cues.OrderBy(x => x.Start).ToArray();
    }

    private static TimeSpan ParseTime(string value)
    {
        var pieces = value.Trim().Replace(',', '.').Split(':');
        if (pieces.Length is not (2 or 3)) throw new InvalidDataException("Неисправно време титла.");
        var hours = pieces.Length == 3 ? int.Parse(pieces[0], CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(pieces[^2], CultureInfo.InvariantCulture);
        var seconds = double.Parse(pieces[^1], CultureInfo.InvariantCulture);
        if (hours < 0 || minutes is < 0 or >= 60 || seconds is < 0 or >= 60)
            throw new InvalidDataException("Неисправно време титла.");
        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
    }

    public static string ToSrt(IEnumerable<SubtitleCue> cues)
    {
        var builder = new StringBuilder();
        var index = 1;
        foreach (var cue in cues.OrderBy(x => x.Start))
        {
            if (cue.Start < TimeSpan.Zero || cue.End <= cue.Start || string.IsNullOrWhiteSpace(cue.Text))
                throw new InvalidDataException("Неисправан временски интервал титла.");
            builder.Append(index++).Append('\n').Append(FormatTime(cue.Start)).Append(" --> ").Append(FormatTime(cue.End))
                .Append('\n').Append(cue.Text.Replace("\r", "").Trim()).Append("\n\n");
        }
        if (index == 1) throw new InvalidDataException("Нема титлова за чување.");
        return builder.ToString();
    }

    private static string FormatTime(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length > 8_000_000) throw new InvalidDataException("Титл је превелик.");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        try { return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1250).GetString(bytes);
        }
    }
}
