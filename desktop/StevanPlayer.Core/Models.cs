namespace StevanPlayer.Core;

public sealed record MediaIdentity(string FilePath, string Title, int? Year, int? Season, int? Episode,
    long FileSize, string? OpenSubtitlesHash = null);

public sealed record SubtitleCandidate(string Id, string ProviderId, string Language, string Release,
    string DownloadReference, bool HashMatched = false, int? Season = null, int? Episode = null,
    int Downloads = 0, int Score = 0);

public sealed record SubtitlePayload(string FileName, byte[] Content);
public sealed record ProviderNotice(string ProviderId, string Message);
public sealed record SubtitleSearchResult(IReadOnlyList<SubtitleCandidate> Candidates,
    IReadOnlyList<ProviderNotice> Notices);

public sealed class PlayerSettings
{
    public string PreferredLanguage { get; set; } = "sr";
    public bool AutoSubtitles { get; set; } = true;
    public bool UseLocalAi { get; set; } = true;
    public string OpenSubtitlesApiKey { get; set; } = "";
    public string OpenSubtitlesToken { get; set; } = "";
    public string SubDlApiKey { get; set; } = "";
    public string TitloviUsername { get; set; } = "";
    public string TitloviPassword { get; set; } = "";
    public string AiApiKey { get; set; } = "";
    public string TranslationModel { get; set; } = "gpt-4.1-mini";
    public bool AllowCloudTranscription { get; set; }
    public bool AutoSynchronize { get; set; }
    public int SubtitleOffsetMilliseconds { get; set; }
}

public interface ISubtitleProvider
{
    string Id { get; }
    bool IsConfigured(PlayerSettings settings);
    Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(MediaIdentity media, PlayerSettings settings,
        CancellationToken cancellationToken);
    Task<SubtitlePayload> DownloadAsync(SubtitleCandidate candidate, PlayerSettings settings,
        CancellationToken cancellationToken);
}

public interface IAudioExtractor
{
    Task<IReadOnlyList<AudioSegment>> ExtractAsync(string mediaPath, string outputDirectory,
        IProgress<string>? progress, CancellationToken cancellationToken);
}

public sealed record AudioSegment(string FilePath, TimeSpan Start);
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

public interface ISubtitleGenerator
{
    Task<IReadOnlyList<SubtitleCue>> GenerateAsync(string mediaPath, string language, PlayerSettings settings,
        IProgress<string>? progress, CancellationToken cancellationToken);
}
