using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StevanPlayer.Core;

namespace StevanPlayer.App;

/// <summary>Secrets are bound to the current Windows user with DPAPI; only preferences remain readable.</summary>
internal sealed class SettingsStore
{
    internal static string? TestDataDirectory { get; set; }
    public static string DataDirectory => TestDataDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StevanPlayer");
    public static string CacheDirectory => Path.Combine(DataDirectory, "Subtitles");
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    private readonly Dictionary<string, string> _injectedCredentials = new(StringComparer.Ordinal);
    public string? Warning { get; private set; }

    public PlayerSettings Load()
    {
        if (!File.Exists(SettingsPath)) return ApplyEnvironmentFallback(new PlayerSettings());
        try
        {
            StoredSettings? stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SettingsPath));
            if (stored is null) return ApplyEnvironmentFallback(new PlayerSettings());
            var settings = stored.Preferences ?? new PlayerSettings();
            if (settings.PreferredLanguage is not ("sr" or "hr" or "bs")) settings.PreferredLanguage = "sr";
            settings.SubtitleOffsetMilliseconds = Math.Clamp(settings.SubtitleOffsetMilliseconds, -600_000, 600_000);
            // Ignore any plaintext secret fields: this file only accepts protected credentials.
            settings.OpenSubtitlesApiKey = settings.OpenSubtitlesToken = settings.SubDlApiKey = settings.AiApiKey = "";
            settings.TitloviUsername = settings.TitloviPassword = "";
            if (!string.IsNullOrWhiteSpace(stored.ProtectedCredentials))
            {
                byte[] clear = Unprotect(Convert.FromBase64String(stored.ProtectedCredentials));
                try
                {
                    Credentials? credentials = JsonSerializer.Deserialize<Credentials>(clear);
                    if (credentials is not null)
                    {
                        settings.OpenSubtitlesApiKey = credentials.OpenSubtitlesApiKey;
                        settings.OpenSubtitlesToken = credentials.OpenSubtitlesToken;
                        settings.SubDlApiKey = credentials.SubDlApiKey;
                        settings.AiApiKey = credentials.AiApiKey;
                        settings.TitloviUsername = credentials.TitloviUsername;
                        settings.TitloviPassword = credentials.TitloviPassword;
                    }
                }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }
            return ApplyEnvironmentFallback(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException)
        {
            Warning = "Подешавања нису учитана. Заштићени кључеви важе само за Windows корисника који их је сачувао. Унесите их поново у Подешавањима.";
            return ApplyEnvironmentFallback(new PlayerSettings());
        }
    }

    public void Save(PlayerSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(new Credentials
        {
            OpenSubtitlesApiKey = PersistentSecret("OPENSUBTITLES_API_KEY", settings.OpenSubtitlesApiKey),
            OpenSubtitlesToken = PersistentSecret("OPENSUBTITLES_TOKEN", settings.OpenSubtitlesToken),
            SubDlApiKey = PersistentSecret("SUBDL_API_KEY", settings.SubDlApiKey),
            AiApiKey = PersistentSecret("STEVAN_AI_API_KEY", settings.AiApiKey),
            TitloviUsername = PersistentSecret("TITLOVI_USERNAME", settings.TitloviUsername),
            TitloviPassword = PersistentSecret("TITLOVI_PASSWORD", settings.TitloviPassword)
        });
        string encrypted;
        try { encrypted = Convert.ToBase64String(Protect(clear)); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        var stored = new StoredSettings
        {
            Preferences = new PlayerSettings
            {
                PreferredLanguage = settings.PreferredLanguage,
                AutoSubtitles = settings.AutoSubtitles,
                AllowCloudTranscription = settings.AllowCloudTranscription,
                AutoSynchronize = settings.AutoSynchronize,
                SubtitleOffsetMilliseconds = settings.SubtitleOffsetMilliseconds,
                TranslationModel = settings.TranslationModel
            },
            ProtectedCredentials = encrypted
        };
        string temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temporaryPath, SettingsPath, true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static byte[] Protect(byte[] data) => Transform(data, protect: true);
    private static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private PlayerSettings ApplyEnvironmentFallback(PlayerSettings settings)
    {
        settings.OpenSubtitlesApiKey = EnvironmentFallback("OPENSUBTITLES_API_KEY", settings.OpenSubtitlesApiKey);
        settings.OpenSubtitlesToken = EnvironmentFallback("OPENSUBTITLES_TOKEN", settings.OpenSubtitlesToken);
        settings.SubDlApiKey = EnvironmentFallback("SUBDL_API_KEY", settings.SubDlApiKey);
        settings.AiApiKey = EnvironmentFallback("STEVAN_AI_API_KEY", settings.AiApiKey);
        settings.TitloviUsername = EnvironmentFallback("TITLOVI_USERNAME", settings.TitloviUsername);
        settings.TitloviPassword = EnvironmentFallback("TITLOVI_PASSWORD", settings.TitloviPassword);
        return settings;
    }
    private string EnvironmentFallback(string name, string stored)
    {
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        string? injected = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(injected)) return "";
        _injectedCredentials[name] = injected;
        return injected;
    }
    private string PersistentSecret(string name, string value) =>
        _injectedCredentials.TryGetValue(name, out string? injected) && value == injected ? "" : value;

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        DataBlob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool success = protect
                ? CryptProtectData(ref input, "StevanPlayer credentials", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("Windows не може да обради заштићена подешавања.");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                for (int i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
                Marshal.FreeHGlobal(input.Data);
            }
            if (output.Data != IntPtr.Zero)
            {
                if (!protect)
                    for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr pointer);

    private sealed class StoredSettings
    {
        public int Version { get; set; } = 1;
        public PlayerSettings? Preferences { get; set; }
        public string ProtectedCredentials { get; set; } = "";
    }

    private sealed class Credentials
    {
        public string OpenSubtitlesApiKey { get; set; } = "";
        public string OpenSubtitlesToken { get; set; } = "";
        public string SubDlApiKey { get; set; } = "";
        public string AiApiKey { get; set; } = "";
        public string TitloviUsername { get; set; } = "";
        public string TitloviPassword { get; set; } = "";
    }
}
