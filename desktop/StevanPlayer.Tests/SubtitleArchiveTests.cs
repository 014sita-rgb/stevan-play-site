using System.IO.Compression;
using System.Text;

namespace StevanPlayer.Tests;

public sealed class SubtitleArchiveTests
{
    [Theory]
    [InlineData("Show.S1E1.srt", "Show.S1E2.srt")]
    [InlineData("Show.1x1.srt", "Show.1x2.srt")]
    [InlineData("Show.S01E001.srt", "Show.S01E002.srt")]
    [InlineData("Show.01x001.srt", "Show.01x002.srt")]
    [InlineData("Show.(S01E01).srt", "Show.(S01E02).srt")]
    public void SelectsRequestedEpisodeRegardlessOfZeroPadding(string otherEpisode, string requestedEpisode)
    {
        byte[] archive = CreateArchive((otherEpisode, "Wrong episode"),
            ("nested/" + requestedEpisode, "Requested episode"));

        string subtitle = Core.SubtitleArchive.Normalize(new("season.zip", archive),
            Core.MediaIdentifier.Parse("Show.S01E02.mkv"));

        Assert.Contains("Requested episode", subtitle);
        Assert.DoesNotContain("Wrong episode", subtitle);
    }

    [Theory]
    [InlineData("Show.S1E20.srt")]
    [InlineData("Show.1x20.srt")]
    [InlineData("Show.S2E2.srt")]
    [InlineData("Show.2x2.srt")]
    public void DoesNotSelectAnotherEpisodeOrSeasonWithSimilarNumber(string otherEpisode)
    {
        byte[] archive = CreateArchive((otherEpisode, "Wrong episode"), ("unlabelled.srt", "Unknown"));

        Assert.Throws<InvalidDataException>(() => Core.SubtitleArchive.Normalize(new("season.zip", archive),
            Core.MediaIdentifier.Parse("Show.S01E02.mkv")));
    }

    [Theory]
    [InlineData("sr", "sr")]
    [InlineData("sr", "SRP")]
    [InlineData("sr", "Serbian")]
    [InlineData("sr", "srpski")]
    [InlineData("sr", "Cirilica")]
    [InlineData("sr", "sr-cyrl")]
    [InlineData("sr", "sr-latn")]
    [InlineData("hr", "hrv")]
    [InlineData("hr", "Croatian")]
    [InlineData("hr", "Hrvatski")]
    [InlineData("bs", "bos")]
    [InlineData("bs", "Bosnian")]
    [InlineData("bs", "Bosanski")]
    public void SelectsTheRequestedLanguageAndRecognizedAliases(string language, string alias)
    {
        byte[] archive = CreateArchive(("Film.2024.en.srt", "Other language"),
            ("Film.2024.srt", "Unlabelled"), ("Film.2024." + alias + ".srt", "Requested language"));

        string subtitle = Core.SubtitleArchive.Normalize(new("languages.zip", archive), language: language);

        Assert.Contains("Requested language", subtitle);
        Assert.DoesNotContain("Other language", subtitle);
        Assert.DoesNotContain("Unlabelled", subtitle);
    }

    [Fact]
    public void SelectsEpisodeBeforeApplyingLanguagePreference()
    {
        byte[] archive = CreateArchive(("Show.S1E1.sr.srt", "Wrong episode"),
            ("Show.S1E2.hr.srt", "Wrong language"), ("Show.S1E2.srp.srt", "Requested episode and language"));

        string subtitle = Core.SubtitleArchive.Normalize(new("season.zip", archive),
            Core.MediaIdentifier.Parse("Show.S01E02.mkv"), "sr");

        Assert.Contains("Requested episode and language", subtitle);
        Assert.DoesNotContain("Wrong", subtitle);
    }

    [Fact]
    public void RecognizesLanguageFoldersAndFilenameTagsOverrideTheFolder()
    {
        byte[] archive = CreateArchive(("English/Film.2024.srt", "English"),
            ("Serbian/Film.2024.hr.srt", "Croatian"), ("Serbian/Film.2024.srt", "Serbian"));

        string subtitle = Core.SubtitleArchive.Normalize(new("languages.zip", archive), language: "sr");

        Assert.Contains("\nSerbian\n", subtitle);
        Assert.DoesNotContain("English", subtitle);
        Assert.DoesNotContain("Croatian", subtitle);
    }

    [Theory]
    [InlineData("Film.2024.hr.srt")]
    [InlineData("Film.2024.bosnian.srt")]
    [InlineData("Film.2024.en.srt")]
    [InlineData("Film.2024.English.SDH.CC.srt")]
    [InlineData("Croatian/Film.2024.srt")]
    [InlineData("English/Film.2024.srt")]
    public void RejectsAnArchiveContainingOnlyAnotherExplicitLanguage(string otherLanguage)
    {
        byte[] archive = CreateArchive((otherLanguage, "Wrong language"));

        Assert.Throws<InvalidDataException>(() =>
            Core.SubtitleArchive.Normalize(new("languages.zip", archive), language: "sr"));
    }

    [Fact]
    public void AllowsAnUnlabelledMemberUsingTheProviderLanguageMetadata()
    {
        byte[] archive = CreateArchive(("Film.2024.sr.srt", "Serbian"), ("Film.2024.srt", "Croatian"));

        string subtitle = Core.SubtitleArchive.Normalize(new("languages.zip", archive), language: "hr");

        Assert.Contains("Croatian", subtitle);
        Assert.DoesNotContain("Serbian", subtitle);
    }

    [Theory]
    [InlineData("English.Patient.1996.srt")]
    [InlineData("It.srt")]
    public void DoesNotTreatAnUnlabelledMovieTitleAsALanguageTag(string filename)
    {
        byte[] archive = CreateArchive((filename, "Provider identified Serbian"));

        Assert.Contains("Provider identified Serbian",
            Core.SubtitleArchive.Normalize(new("languages.zip", archive), language: "sr"));
    }

    [Fact]
    public void ChoosesRequestedLanguageBeforePreferringSrt()
    {
        byte[] archive = CreateArchive(("Film.2024.srt", "Unknown language"),
            ("Film.2024.sr.ass", "Serbian ASS"));

        Assert.Contains("Serbian ASS", Core.SubtitleArchive.Normalize(new("languages.zip", archive), language: "sr"));
    }

    [Fact]
    public void ChoosesSrtThenFilenameRegardlessOfZipMemberOrder()
    {
        (string Name, string Text)[] members = [
            ("Show.S1E2.A.sr.vtt", "VTT"),
            ("Show.S1E2.Z.sr.srt", "Later SRT"),
            ("Show.S1E2.B.sr.srt", "Selected SRT")];
        var media = Core.MediaIdentifier.Parse("Show.S01E02.mkv");

        string first = Core.SubtitleArchive.Normalize(new("season.zip", CreateArchive(members)), media, "sr");
        string reversed = Core.SubtitleArchive.Normalize(new("season.zip", CreateArchive(members.Reverse().ToArray())), media, "sr");

        Assert.Equal(first, reversed);
        Assert.Contains("Selected SRT", first);
    }

    [Fact]
    public void RejectsDuplicateMatchingMemberNames()
    {
        byte[] archive = CreateArchive(("Show.S1E2.sr.srt", "First"), ("Show.S1E2.sr.srt", "Second"));

        Assert.Throws<InvalidDataException>(() => Core.SubtitleArchive.Normalize(new("season.zip", archive),
            Core.MediaIdentifier.Parse("Show.S01E02.mkv"), "sr"));
    }

    [Fact]
    public void ExistingCallerWithoutLanguageCanStillReadAForeignMember()
    {
        byte[] archive = CreateArchive(("Film.2024.en.srt", "English"));

        Assert.Contains("English", Core.SubtitleArchive.Normalize(new("languages.zip", archive)));
    }

    [Fact]
    public async Task CatalogUsesTheChosenCandidateLanguageWhenPreferredLanguageDiffers()
    {
        byte[] archive = CreateArchive(("Film.2024.sr.srt", "Serbian"), ("Film.2024.hr.srt", "Croatian"));
        var catalog = new Core.SubtitleCatalog([new PayloadProvider(new("languages.zip", archive))]);
        var candidate = new Core.SubtitleCandidate("chosen", "bundle", "hr", "Film.2024", "x");
        var directory = Path.Combine(Path.GetTempPath(), "stevan-archive-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = await catalog.DownloadAsync(candidate, new() { PreferredLanguage = "sr" }, directory,
                media: Core.MediaIdentifier.Parse("Film.2024.mkv"));

            string subtitle = await File.ReadAllTextAsync(path);
            Assert.Contains("Croatian", subtitle);
            Assert.DoesNotContain("Serbian", subtitle);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CatalogDoesNotCacheAWrongLanguageArchiveAsASuccessfulDownload()
    {
        byte[] archive = CreateArchive(("Film.2024.hr.srt", "Croatian"));
        var catalog = new Core.SubtitleCatalog([new PayloadProvider(new("languages.zip", archive))]);
        var candidate = new Core.SubtitleCandidate("chosen", "bundle", "sr", "Film.2024", "x");
        var directory = Path.Combine(Path.GetTempPath(), "stevan-archive-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => catalog.DownloadAsync(candidate, new(), directory));
            Assert.False(Directory.Exists(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static byte[] CreateArchive(params (string Name, string Text)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(entry.Name).Open(), new UTF8Encoding(false));
                if (entry.Name.EndsWith(".ass", StringComparison.OrdinalIgnoreCase))
                    writer.Write("[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
                        "Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,," + entry.Text + "\n");
                else
                    writer.Write("1\n00:00:01,000 --> 00:00:03,000\n" + entry.Text + "\n");
            }
        }
        return stream.ToArray();
    }

    private sealed class PayloadProvider(Core.SubtitlePayload payload) : Core.ISubtitleProvider
    {
        public string Id => "bundle";
        public bool IsConfigured(Core.PlayerSettings settings) => true;
        public Task<IReadOnlyList<Core.SubtitleCandidate>> SearchAsync(Core.MediaIdentity media,
            Core.PlayerSettings settings, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Core.SubtitleCandidate>>([]);
        public Task<Core.SubtitlePayload> DownloadAsync(Core.SubtitleCandidate candidate,
            Core.PlayerSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(payload);
    }
}
