using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace StevanPlayer.Tests;

public sealed class CoreTests
{
    [Theory]
    [InlineData("Film.Title.2024.1080p.WEB-DL.mkv", "Film Title", 2024, null, null)]
    [InlineData("Show_Name_S02E007_1080p.mkv", "Show Name", null, 2, 7)]
    [InlineData("Show.Name.2x03.HDTV.mkv", "Show Name", null, 2, 3)]
    [InlineData("2001.A.Space.Odyssey.1968.BluRay.mkv", "2001 A Space Odyssey", 1968, null, null)]
    [InlineData("https://example.com/Some%20Film.2025.mp4?token=secret", "Some Film", 2025, null, null)]
    public void ParsesMovieAndEpisodeIdentifiers(string name, string title, int? year, int? season, int? episode)
    {
        var identity = Core.MediaIdentifier.Parse(name);
        Assert.Equal(title, identity.Title);
        Assert.Equal(year, identity.Year);
        Assert.Equal(season, identity.Season);
        Assert.Equal(episode, identity.Episode);
    }

    [Fact]
    public async Task MovieHashUsesBothBlocksAndLittleEndianWrapping()
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = new byte[131080];
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0, 8), ulong.MaxValue);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(data.Length - 8, 8), 7);
            await File.WriteAllBytesAsync(path, data);
            Assert.Equal(((ulong)data.Length + 6).ToString("x16"), await Core.MediaIdentifier.ComputeHashAsync(path));
            await File.WriteAllBytesAsync(path, new byte[100]);
            Assert.Null(await Core.MediaIdentifier.ComputeHashAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SrtRoundtripPreservesCyrillicAndMultilineTimes()
    {
        const string srt = "1\r\n00:00:01,230 --> 00:00:03,567\r\nЗдраво, свете!\r\nДруги ред.\r\n\r\n2\r\n00:01:00,000 --> 00:01:02,000\r\nDobar dan.\r\n";
        var cues = Core.SubtitleFormat.Parse(srt);
        Assert.Equal(2, cues.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1230), cues[0].Start);
        Assert.Equal("Здраво, свете!\nДруги ред.", cues[0].Text);
        Assert.Equal(cues, Core.SubtitleFormat.Parse(Core.SubtitleFormat.ToSrt(cues)));
    }

    [Fact]
    public void VttAndAssBecomeTimedSrt()
    {
        var vtt = Core.SubtitleFormat.Parse("WEBVTT\n\n00:01.000 --> 00:03.000 align:start\nPozdrav!\n", ".vtt");
        Assert.Single(vtt);
        var ass = Core.SubtitleFormat.Parse("[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:03.50,Default,,0,0,0,,{\\i1}Zdravo, svete!\\NDrugi red\n", ".ass");
        Assert.Equal("Zdravo, svete!\nDrugi red", Assert.Single(ass).Text);
        Assert.Contains("00:00:03,500", Core.SubtitleFormat.ToSrt(ass));
    }

    [Fact]
    public void DecodesLegacyBalkanEncodingWithoutLosingAccents()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1250).GetBytes("Čujem žubor, šapat i đake.");
        Assert.Equal("Čujem žubor, šapat i đake.", Core.SubtitleFormat.Decode(bytes));
    }

    [Fact]
    public void RejectsHtmlMasqueradingAsSubtitleAndInvalidTime()
    {
        Assert.Throws<InvalidDataException>(() => Core.SubtitleFormat.Parse("<html>Rate limit</html>"));
        Assert.Throws<InvalidDataException>(() => Core.SubtitleFormat.Parse("1\n00:99:01,000 --> 00:99:03,000\nCaption\n"));
    }

    [Fact]
    public void ArchiveSelectsCorrectEpisodeAndRejectsOtherEpisode()
    {
        var identity = Core.MediaIdentifier.Parse("Show.S01E02.mkv");
        var archive = CreateArchive(("Show.S01E01.srt", "First"), ("folder/Show.S01E02.srt", "Second"));
        Assert.Contains("Second", Core.SubtitleArchive.Normalize(new("pack.zip", archive), identity));
        Assert.Throws<InvalidDataException>(() => Core.SubtitleArchive.Normalize(new("pack.zip",
            CreateArchive(("Show.S01E01.srt", "Wrong"))), identity));
        Assert.Throws<InvalidDataException>(() => Core.SubtitleArchive.Normalize(new("pack.zip",
            CreateArchive(("a.srt", "Unknown"), ("b.srt", "Unknown"))), identity));
    }

    [Fact]
    public async Task ProviderFailureDoesNotHideAnotherProvidersResultsOrWrongEpisode()
    {
        var good = new StubProvider("good", [new("right", "good", "sr", "Show.S01E02", "x"),
            new("wrong", "good", "sr", "Show.S01E03", "x"), new("english", "good", "en", "Show.S01E02", "x")]);
        var bad = new StubProvider("bad", [], true);
        var result = await new Core.SubtitleCatalog([good, bad]).SearchAsync(Core.MediaIdentifier.Parse("Show.S01E02.mkv"), new());
        Assert.Equal("right", Assert.Single(result.Candidates).Id);
        Assert.Equal("bad", Assert.Single(result.Notices).ProviderId);
    }

    [Fact]
    public async Task HashMatchOutranksPreferredLanguageAndDownloadWritesValidatedUtf8()
    {
        var provider = new StubProvider("good", [new("preferred", "good", "sr", "Film.2024", "x"),
            new("exact", "good", "hr", "Film.2024", "x", HashMatched: true)]);
        var catalog = new Core.SubtitleCatalog([provider]);
        var media = Core.MediaIdentifier.Parse("Film.2024.mkv");
        var result = await catalog.SearchAsync(media, new());
        Assert.Equal("exact", result.Candidates[0].Id);
        var directory = Path.Combine(Path.GetTempPath(), "stevan-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = await catalog.DownloadAsync(result.Candidates[0], new(), directory, media: media);
            var second = await catalog.DownloadAsync(result.Candidates[0], new(), directory, media: media);
            Assert.Equal(first, second);
            Assert.Single(Directory.GetFiles(directory));
            Assert.Contains("Здраво", await File.ReadAllTextAsync(first));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void SynchronizationCorrectsMeasuredOffsetAndFrameRateDrift()
    {
        var original = Enumerable.Range(0, 8).Select(i => new Core.SubtitleCue(TimeSpan.FromSeconds(i * 100 + 10),
            TimeSpan.FromSeconds(i * 100 + 13), $"Posebna rečenica broj{i} naslov{i} pozdrav{i}")).ToArray();
        var transcript = original.Select(x => x with { Start = TimeSpan.FromSeconds(x.Start.TotalSeconds * 1.04 + 2.5),
            End = TimeSpan.FromSeconds(x.End.TotalSeconds * 1.04 + 2.5) }).ToArray();
        var result = Core.SubtitleSynchronizer.Synchronize(original, transcript);
        Assert.Equal(1.04, result.TimeScale, 5);
        Assert.Equal(2.5, result.OffsetSeconds, 5);
        Assert.Equal(8, result.AnchorCount);
        Assert.Equal(transcript[^1].Start.TotalSeconds, result.Cues[^1].Start.TotalSeconds, 5);
    }

    [Fact]
    public void SynchronizationRefusesUnconfirmedAlignment()
    {
        var original = new[] { new Core.SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "Nema dovoljno uzoraka.") };
        Assert.Throws<InvalidDataException>(() => Core.SubtitleSynchronizer.Synchronize(original, original));
    }

    [Fact]
    public void SynchronizationMatchesSerbianCyrillicAndLatinTranscripts()
    {
        string[] cyrillic = ["Живим далеко овде", "Његови ученици говоре", "Љубав траје увек", "Ђаци уче школу", "Џинови гледају човека"];
        string[] latin = ["Živim daleko ovde", "Njegovi učenici govore", "Ljubav traje uvek", "Đaci uče školu", "Džinovi gledaju čoveka"];
        var original = cyrillic.Select((text, i) => new Core.SubtitleCue(TimeSpan.FromSeconds(i * 20 + 10), TimeSpan.FromSeconds(i * 20 + 13), text)).ToArray();
        var transcript = original.Select((cue, i) => cue with { Start = cue.Start + TimeSpan.FromSeconds(3), Text = latin[i] }).ToArray();
        Assert.Equal(3, Core.SubtitleSynchronizer.Synchronize(original, transcript).OffsetSeconds, 4);
    }

    [Fact]
    public void SynchronizationRejectsRepeatedAmbiguousDialogue()
    {
        var original = Enumerable.Range(0, 6).Select(i => new Core.SubtitleCue(TimeSpan.FromSeconds(i * 10 + 1),
            TimeSpan.FromSeconds(i * 10 + 3), "Ne znam odgovor.")).ToArray();
        Assert.Throws<InvalidDataException>(() => Core.SubtitleSynchronizer.Synchronize(original, original));
    }

    private static byte[] CreateArchive(params (string Name, string Text)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var item in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(item.Name).Open(), new UTF8Encoding(false));
                writer.Write("1\n00:00:01,000 --> 00:00:03,000\n" + item.Text + "\n");
            }
        return stream.ToArray();
    }

    private sealed class StubProvider(string id, IReadOnlyList<Core.SubtitleCandidate> results, bool fail = false) : Core.ISubtitleProvider
    {
        public string Id => id;
        public bool IsConfigured(Core.PlayerSettings settings) => true;
        public Task<IReadOnlyList<Core.SubtitleCandidate>> SearchAsync(Core.MediaIdentity media, Core.PlayerSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fail) throw new Core.Providers.ProviderException(id, Core.Providers.ProviderFailure.Unavailable, "Недоступан извор.");
            return Task.FromResult(results);
        }
        public Task<Core.SubtitlePayload> DownloadAsync(Core.SubtitleCandidate candidate, Core.PlayerSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(new Core.SubtitlePayload("subtitle.srt", Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:03,000\nЗдраво\n")));
    }
}
