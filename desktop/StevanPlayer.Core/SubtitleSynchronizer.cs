using System.Text.RegularExpressions;

namespace StevanPlayer.Core;

public sealed record SynchronizationResult(IReadOnlyList<SubtitleCue> Cues, double OffsetSeconds,
    double TimeScale, int AnchorCount);

public static class SubtitleSynchronizer
{
    // Match subtitles against the timestamped, translated transcript. Never infer confidence
    // from silence or assume a release-name match establishes actual audio synchronization.
    public static SynchronizationResult Synchronize(IReadOnlyList<SubtitleCue> original,
        IReadOnlyList<SubtitleCue> transcript)
    {
        var anchors = new List<(double Source, double Target)>();
        var used = new HashSet<int>();
        var transcriptWords = transcript.Select(x => Words(x.Text)).ToArray();
        foreach (var cue in original)
        {
            var words = Words(cue.Text);
            if (words.Count < 3) continue;
            var bestIndex = -1;
            var best = 0.0;
            var runnerUp = 0.0;
            for (var i = 0; i < transcript.Count; i++)
            {
                if (used.Contains(i)) continue;
                var other = transcriptWords[i];
                var union = words.Union(other).Count();
                var similarity = union == 0 ? 0 : (double)words.Intersect(other).Count() / union;
                if (similarity > best) { runnerUp = best; best = similarity; bestIndex = i; }
                else runnerUp = Math.Max(runnerUp, similarity);
            }
            if (bestIndex >= 0 && best >= 0.6 && best - runnerUp >= 0.1)
            {
                used.Add(bestIndex);
                anchors.Add((cue.Start.TotalSeconds, transcript[bestIndex].Start.TotalSeconds));
            }
        }
        if (anchors.Count < 5)
            throw new InvalidDataException("Нема довољно поузданих говорних поклапања за аутоматско усклађивање; користите ручни помак.");
        var offsets = anchors.Select(x => x.Target - x.Source).Order().ToArray();
        var median = offsets[offsets.Length / 2];
        // A robust fit supports both a fixed offset and common 23.976/25fps drift.
        var slopes = new List<double>();
        for (var i = 0; i < anchors.Count; i++)
            for (var j = i + 1; j < anchors.Count; j++)
            {
                var delta = anchors[j].Source - anchors[i].Source;
                if (Math.Abs(delta) > 60) slopes.Add((anchors[j].Target - anchors[i].Target) / delta);
            }
        var scale = slopes.Count == 0 ? 1 : slopes.Order().ElementAt(slopes.Count / 2);
        if (scale is < 0.94 or > 1.06)
            throw new InvalidDataException("Титл се сувише разликује од звучног записа за безбедно усклађивање.");
        var intercepts = anchors.Select(x => x.Target - scale * x.Source).Order().ToArray();
        var intercept = intercepts[intercepts.Length / 2];
        var good = anchors.Where(x => Math.Abs(x.Target - (scale * x.Source + intercept)) <= 1.5).ToArray();
        if (good.Length < 5 || good.Length < anchors.Count * 0.7)
            throw new InvalidDataException("Говорна поклапања не потврђују стабилно усклађивање титла.");
        if (slopes.Count == 0) intercept = median;
        var corrected = original.Select(x => new SubtitleCue(
            TimeSpan.FromSeconds(Math.Max(0, scale * x.Start.TotalSeconds + intercept)),
            TimeSpan.FromSeconds(Math.Max(0, scale * x.End.TotalSeconds + intercept)), x.Text))
            .Where(x => x.End > x.Start).ToArray();
        if (corrected.Length == 0) throw new InvalidDataException("Усклађивање би уклонило све титлове.");
        return new(corrected, intercept, scale, good.Length);
    }

    private static HashSet<string> Words(string text)
    {
        const string cyrillic = "абвгдђежзијклмнопрстћуфхцчш";
        const string latin = "abvgdđežzijklmnoprstćufhcčš";
        var normalized = new System.Text.StringBuilder();
        foreach (var character in text.ToLowerInvariant())
        {
            if (character == 'љ') { normalized.Append("lj"); continue; }
            if (character == 'њ') { normalized.Append("nj"); continue; }
            if (character == 'џ') { normalized.Append("dž"); continue; }
            var index = cyrillic.IndexOf(character);
            normalized.Append(index >= 0 ? latin[index] : character);
        }
        return Regex.Split(Regex.Replace(normalized.ToString(), @"<[^>]*>|\{[^}]*\}", ""), @"[^\p{L}\p{N}]+")
            .Where(x => x.Length > 2).ToHashSet();
    }
}
