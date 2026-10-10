using System.IO.Compression;
using System.Text;
using StevanPlayer.Core;
using StevanPlayer.Core.Providers;

namespace StevanPlayer.Tests.Providers;

public sealed class LocalSubtitleProviderTests
{
    [Fact]
    public async Task SidecarsAreFoundWithoutAnyKeyAndResultsRemainStable()
    {
        using var files = new SubtitleFiles();
        var media = files.Media("Movie.2020.1080p.mkv");
        files.Subtitle(files.Video, "Movie.2020.1080p.srpski.srt", "Srpski čćšžđ");
        files.Subtitle(files.Video, "Movie.2020.1080p.hrvatski.vtt", "Hrvatski", vtt: true);
        files.Subtitle(files.Video, "Movie.2020.1080p.bosanski.srt", "Bosanski");
        var provider = new LocalSubtitleProvider();
        Assert.True(provider.IsConfigured(new PlayerSettings()));
        var found = await provider.SearchAsync(media, new PlayerSettings(), default);
        Assert.Equal(new[] { "bs", "hr", "sr" }, found.Select(candidate => candidate.Language).Order());
        Assert.All(found, candidate => Assert.Equal("Local", candidate.ProviderId));
        var again = await provider.SearchAsync(media, new PlayerSettings(), default);
        Assert.Equal(found.Select(candidate => candidate.Id).Order(), again.Select(candidate => candidate.Id).Order());
        var payload = await provider.DownloadAsync(found.Single(candidate => candidate.Language == "sr"), new PlayerSettings(), default);
        Assert.Contains("Srpski čćšžđ", SubtitleArchive.Normalize(payload));
        Assert.Empty(provider.LastNotices);
    }

    [Fact]
    public async Task DownloadsScanRequiresAnExplicitRootAndDoesNotRecurse()
    {
        using var files = new SubtitleFiles();
        var media = files.Media("Movie.2020.mkv");
        files.Subtitle(files.Downloads, "Movie.2020.sr.srt", "Requested");
        files.Subtitle(files.Downloads, "Another.Movie.2020.sr.srt", "Wrong title");
        files.Subtitle(files.Downloads, "Movie.1999.sr.srt", "Wrong year");
        var nested = Directory.CreateDirectory(Path.Combine(files.Downloads, "nested")).FullName;
        files.Subtitle(nested, "Movie.2020.hr.srt", "Do not recursively scan");
        Assert.Empty(await new LocalSubtitleProvider().SearchAsync(media, new PlayerSettings(), default));
        var provider = new LocalSubtitleProvider([files.Downloads]);
        var result = Assert.Single(await provider.SearchAsync(media, new PlayerSettings(), default));
        Assert.Equal("sr", result.Language);
        Assert.Equal(Path.Combine(files.Downloads, "Movie.2020.sr.srt"), result.DownloadReference);
    }

    [Fact]
    public async Task WrongMissingYearsForeignLanguagesAndPartialFilesAreIgnored()
    {
        using var files = new SubtitleFiles();
        var media = files.Media("Movie.2020.mkv");
        foreach (var name in new[]
        {
            "Movie.2020.en.srt", "Movie.2020.English.srt", "Movie.2020.part.sr.srt",
            "Movie.2020.sr.srt.part", "Movie.sr.srt", "Movie.1999.sr.srt", "Movie.2020.sr.srt.tmp"
        }) files.Subtitle(files.Video, name, "Must not select");
        files.Subtitle(files.Video, "Movie.2020.[SR].srt", "Correct");
        var result = Assert.Single(await new LocalSubtitleProvider().SearchAsync(media, new PlayerSettings(), default));
        Assert.Equal("sr", result.Language);
        Assert.EndsWith("Movie.2020.[SR].srt", result.DownloadReference);
    }

    [Fact]
    public async Task UnlabelledFilesUsePreferredLanguageOnlyForTheSameRelease()
    {
        using var files = new SubtitleFiles();
        var media = files.Media("Movie.2020.1080p.WEB-DL.mkv");
        files.Subtitle(files.Downloads, "Movie 2020 1080p WEB-DL.srt", "Same release");
        files.Subtitle(files.Downloads, "Movie.2020.BluRay.srt", "Unlabelled different release");
        files.Subtitle(files.Downloads, "Movie.2020.srt", "Unlabelled generic title");
        var result = Assert.Single(await new LocalSubtitleProvider([files.Downloads]).SearchAsync(media,
            new PlayerSettings { PreferredLanguage = "hr" }, default));
        Assert.Equal("hr", result.Language);
        Assert.EndsWith("Movie 2020 1080p WEB-DL.srt", result.DownloadReference);
    }

    [Theory]
    [InlineData("Srpski", "sr")]
    [InlineData("HRV", "hr")]
    [InlineData("bosanski", "bs")]
    public async Task AnExplicitLanguageFolderLabelsARelease(string folder, string expected)
    {
        using var files = new SubtitleFiles();
        var root = Directory.CreateDirectory(Path.Combine(files.Downloads, folder)).FullName;
        files.Subtitle(root, "Movie.2020.BluRay.srt", "Language folder");
        var result = Assert.Single(await new LocalSubtitleProvider([root]).SearchAsync(
            files.Media("Movie.2020.WEB-DL.mkv"), new PlayerSettings(), default));
        Assert.Equal(expected, result.Language);
    }

    [Fact]
    public async Task SeriesRequireExactTitleSeasonAndEpisode()
    {
        using var files = new SubtitleFiles();
        var media = files.Media("Show.S02E007.WEB-DL.mkv");
        foreach (var name in new[] { "Show.S02E008.sr.srt", "Show.S01E007.sr.srt", "Other.Show.S02E007.sr.srt", "Show.sr.srt" })
            files.Subtitle(files.Downloads, name, "Wrong episode");
        files.Subtitle(files.Downloads, "Show.2x7.BluRay.hrv.srt", "Exact episode");
        var result = Assert.Single(await new LocalSubtitleProvider([files.Downloads]).SearchAsync(media, new PlayerSettings(), default));
        Assert.Equal((2, 7, "hr"), (result.Season, result.Episode, result.Language));
    }

    [Fact]
    public async Task MovieZipSelectsTheMatchingFilmAndLanguageAndImportsThroughCatalog()
    {
        using var files = new SubtitleFiles();
        var media = files.Media("Movie.2020.mkv");
        files.Archive("Movie.2020.zip", new()
        {
            ["sr/A.Different.Movie.2020.srt"] = "Never select unrelated film",
            ["sr/Movie.2020.srt"] = "Srpski ispravno",
            ["hr/Movie.2020.srt"] = "Hrvatski ispravno",
            ["en/Movie.2020.srt"] = "English ignored",
            ["../Movie.2020.bs.srt"] = "Traversal ignored"
        });
        var provider = new LocalSubtitleProvider([files.Downloads]);
        var catalog = new SubtitleCatalog([provider]);
        var search = await catalog.SearchAsync(media, new PlayerSettings());
        Assert.Equal(new[] { "hr", "sr" }, search.Candidates.Select(candidate => candidate.Language).Order());
        foreach (var candidate in search.Candidates)
        {
            var cached = await catalog.DownloadAsync(candidate, new PlayerSettings(), files.Cache, media: media);
            var content = await File.ReadAllTextAsync(cached);
            Assert.Contains(candidate.Language == "sr" ? "Srpski ispravno" : "Hrvatski ispravno", content);
            Assert.DoesNotContain("unrelated", content);
        }
        Assert.False(Directory.Exists(Path.Combine(files.Downloads, "sr")));
    }

    [Fact]
    public async Task ArchiveCannotSupplyWrongEpisodeOrWrongFilm()
    {
        using var files = new SubtitleFiles();
        files.Archive("Show.S01E02.sr.zip", new()
        {
            ["Show.S01E01.sr.srt"] = "Wrong episode",
            ["Show.S01E02.sr.srt"] = "Right episode"
        });
        files.Archive("Show.S01E02.bs.zip", new() { ["Other.Show.S01E02.bs.srt"] = "Wrong film" });
        var provider = new LocalSubtitleProvider([files.Downloads]);
        var result = Assert.Single(await provider.SearchAsync(files.Media("Show.S01E02.mkv"), new PlayerSettings(), default));
        var payload = await provider.DownloadAsync(result, new PlayerSettings(), default);
        Assert.Equal("Show.S01E02.sr.srt", payload.FileName);
        Assert.Contains("Right episode", SubtitleArchive.Normalize(payload));
    }

    [Fact]
    public async Task DownloadRejectsOutsidePathsUnidentifiedFilesAndChangedCandidateMetadata()
    {
        using var files = new SubtitleFiles();
        var media = files.Media("Movie.2020.mkv");
        files.Subtitle(files.Video, "Movie.2020.sr.srt", "Selected");
        var privatePath = files.Subtitle(files.Root, "private.srt", "Private unrelated data");
        var unrelated = files.Subtitle(files.Video, "Other.Movie.2020.sr.srt", "Unrelated within searched root");
        var provider = new LocalSubtitleProvider();
        var result = Assert.Single(await provider.SearchAsync(media, new PlayerSettings(), default));
        foreach (var forged in new[]
        {
            result with { DownloadReference = privatePath }, result with { DownloadReference = unrelated },
            result with { ProviderId = "other" }, result with { Id = "unknown" }, result with { Language = "bs" }
        }) await Assert.ThrowsAsync<ProviderException>(() => provider.DownloadAsync(forged, new PlayerSettings(), default));
    }

    [Fact]
    public async Task ExistingLocalPathsCannotBeReadBeforeMatchingDiscovery()
    {
        using var files = new SubtitleFiles();
        var media = files.Media("Movie.2020.mkv");
        var path = files.Subtitle(files.Downloads, "Movie.2020.sr.srt", "Matching subtitle");
        var provider = new LocalSubtitleProvider([files.Downloads]);
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(path + "\n\nsr"))).ToLowerInvariant();
        var fabricated = new SubtitleCandidate(id, provider.Id, "sr", "Movie.2020", path);
        await Assert.ThrowsAsync<ProviderException>(() => provider.DownloadAsync(fabricated, new PlayerSettings(), default));
        var discovered = Assert.Single(await provider.SearchAsync(media, new PlayerSettings(), default));
        Assert.Equal(id, discovered.Id);
        Assert.Contains("Matching subtitle", SubtitleArchive.Normalize(await provider.DownloadAsync(discovered, new PlayerSettings(), default)));
    }

    [Fact]
    public async Task ChangedOrRemovedFilesRequireANewSearch()
    {
        using var files = new SubtitleFiles();
        var media = files.Media("Movie.2020.mkv");
        var path = files.Subtitle(files.Video, "Movie.2020.sr.srt", "Original");
        var provider = new LocalSubtitleProvider();
        var original = Assert.Single(await provider.SearchAsync(media, new PlayerSettings(), default));
        await File.AppendAllTextAsync(path, "changed content");
        await Assert.ThrowsAsync<ProviderException>(() => provider.DownloadAsync(original, new PlayerSettings(), default));
        var refreshed = Assert.Single(await provider.SearchAsync(media, new PlayerSettings(), default));
        File.Delete(path);
        await Assert.ThrowsAsync<ProviderException>(() => provider.DownloadAsync(refreshed, new PlayerSettings(), default));
    }

    [Fact]
    public async Task OversizedAndMalformedArchivesAreIgnoredWithoutBlockingValidSubtitles()
    {
        using var files = new SubtitleFiles();
        var media = files.Media("Movie.2020.mkv");
        await using (var large = File.Create(Path.Combine(files.Downloads, "Movie.2020.sr.zip"))) large.SetLength(8_000_001);
        files.Subtitle(files.Downloads, "Movie.2020.hr.zip", "This is not a ZIP");
        files.Subtitle(files.Downloads, "Movie.2020.bs.srt", "Valid subtitle");
        var provider = new LocalSubtitleProvider([files.Downloads]);
        var result = Assert.Single(await provider.SearchAsync(media, new PlayerSettings(), default));
        Assert.Equal("bs", result.Language);
        Assert.NotEmpty(provider.LastNotices);
    }

    [Fact]
    public async Task UnusableRootsReturnSafeNoticesAndKeepOtherResults()
    {
        using var files = new SubtitleFiles();
        var notADirectory = files.Subtitle(files.Root, "not-a-directory", "Not a directory");
        var missingDirectory = Path.Combine(files.Root, "missing");
        files.Subtitle(files.Video, "Movie.2020.sr.srt", "Usable");
        var provider = new LocalSubtitleProvider([notADirectory, missingDirectory, "https://example.test/"]);
        Assert.Single(await provider.SearchAsync(files.Media("Movie.2020.mkv"), new PlayerSettings(), default));
        var notice = Assert.Single(provider.LastNotices);
        Assert.Equal(provider.Id, notice.ProviderId);
        Assert.DoesNotContain(files.Root, notice.Message);
    }

    [Fact]
    public async Task SearchAndDownloadRespectCancellation()
    {
        using var files = new SubtitleFiles();
        var media = files.Media("Movie.2020.mkv");
        files.Subtitle(files.Video, "Movie.2020.sr.srt", "Subtitle");
        var provider = new LocalSubtitleProvider();
        var result = Assert.Single(await provider.SearchAsync(media, new PlayerSettings(), default));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchAsync(media, new PlayerSettings(), cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DownloadAsync(result, new PlayerSettings(), cancel.Token));
    }

    private sealed class SubtitleFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "stevan-local-subtitle-tests-" + Guid.NewGuid().ToString("N"));
        public string Video { get; }
        public string Downloads { get; }
        public string Cache { get; }
        public SubtitleFiles()
        {
            Video = Directory.CreateDirectory(Path.Combine(Root, "video")).FullName;
            Downloads = Directory.CreateDirectory(Path.Combine(Root, "downloads")).FullName;
            Cache = Path.Combine(Root, "cache");
        }
        public MediaIdentity Media(string name)
        {
            var path = Path.Combine(Video, name);
            File.WriteAllText(path, "media identity fixture, not an actual video");
            return MediaIdentifier.Parse(path);
        }
        public string Subtitle(string directory, string name, string text, bool vtt = false)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, vtt ? "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\n" + text + "\n" : Srt(text), new UTF8Encoding(false));
            return path;
        }
        public void Archive(string name, Dictionary<string, string> members)
        {
            using var output = File.Create(Path.Combine(Downloads, name));
            using var archive = new ZipArchive(output, ZipArchiveMode.Create);
            foreach (var member in members)
            {
                using var stream = archive.CreateEntry(member.Key).Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(Srt(member.Value));
            }
        }
        private static string Srt(string text) => "1\n00:00:01,000 --> 00:00:03,000\n" + text + "\n";
        public void Dispose() => Directory.Delete(Root, true);
    }
}
