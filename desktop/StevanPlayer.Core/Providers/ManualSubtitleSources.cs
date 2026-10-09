namespace StevanPlayer.Core.Providers;

public sealed record ManualSubtitleSource(string Name, Uri? Website, string AccessNote);

/// <summary>Browser fallback sources. Unknown service addresses stay unset, and no HTML scraping occurs.</summary>
public static class ManualSubtitleSources
{
    public static IReadOnlyList<ManualSubtitleSource> All { get; } =
    [
        new("Titlovi.com", new Uri("https://titlovi.com/"),
            "Ručna pretraga je dostupna i bez integracije. Automatski Kodi API zahteva nalog sa odobrenim pristupom; živa integracija još nije proverena."),
        new("Prijevodi-Titlovi.org", new Uri("https://prijevodi-titlovi.org/"),
            "Ručni pristup: preuzimanje je za članove, robots pravila isključuju download putanje; odobren API nije potvrđen."),
        new("Prijevodi Online", new Uri("https://www.prijevodi-online.org/"),
            "Adresa je potvrđena od korisnika. Ručni pristup u pregledaču; pravila i dozvoljen API još nisu provereni, automatska integracija nije aktivirana.")
    ];
}
