using System.Text;
using System.Text.Json;
using StevanPlayer.Core;

namespace StevanPlayer.App;

/// <summary>Playback preferences only: the player never loads or saves service credentials.</summary>
public sealed class SettingsStore
{
    internal static string? TestDataDirectory { get; set; }
    public static string DataDirectory => TestDataDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StevanPlayer");
    public static string CacheDirectory => Path.Combine(DataDirectory, "Subtitles");
    public static string ModelsDirectory => Path.Combine(DataDirectory, "Models");
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string? Warning { get; private set; }

    public PlayerSettings Load()
    {
        Warning = null;
        try
        {
            if (!File.Exists(SettingsPath)) return new();
            var preferences = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SettingsPath))?.Preferences ?? new();
            return new PlayerSettings
            {
                PreferredLanguage = preferences.PreferredLanguage is "hr" or "bs" ? preferences.PreferredLanguage : "sr",
                AutoSubtitles = preferences.AutoSubtitles,
                UseLocalAi = preferences.UseLocalAi,
                AutoSynchronize = preferences.AutoSynchronize,
                SubtitleOffsetMilliseconds = Math.Clamp(preferences.SubtitleOffsetMilliseconds, -600_000, 600_000)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Warning = "Подешавања нису учитана. Користим подразумеване поставке.";
            return new();
        }
    }

    public void Save(PlayerSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        var stored = new StoredSettings
        {
            Preferences = new Preferences
            {
                PreferredLanguage = settings.PreferredLanguage,
                AutoSubtitles = settings.AutoSubtitles,
                UseLocalAi = settings.UseLocalAi,
                AutoSynchronize = settings.AutoSynchronize,
                SubtitleOffsetMilliseconds = settings.SubtitleOffsetMilliseconds
            }
        };
        string temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temporary, SettingsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class StoredSettings
    {
        public int Version { get; set; } = 2;
        public Preferences? Preferences { get; set; }
    }

    private sealed class Preferences
    {
        public string PreferredLanguage { get; set; } = "sr";
        public bool AutoSubtitles { get; set; } = true;
        public bool UseLocalAi { get; set; } = true;
        public bool AutoSynchronize { get; set; }
        public int SubtitleOffsetMilliseconds { get; set; }
    }
}
