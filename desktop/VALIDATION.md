# Provere СТЕВАНplejera

**Status aplikacije: nije završena. Objavljivanje izvršnog programa ostaje zaustavljeno dok stvarne Windows provere ne prođu.**
ZIP na GitHub-u sadrži izvorni kod i uputstva; nije instalacija niti potvrđena završna verzija.

## Šta je promenjeno

Plejer prepoznaje naslov iz naziva datoteke ili dostupnih metapodataka, godinu i epizodu, računa dostupni filmski hash i sam pokreće pretragu. Rezultati se prikazuju unutar plejera. Korisnik označava jedan prevod; plejer preuzima, proverava ZIP/tekst i učitava izabrani titl u libVLC. Program ne šalje korisnika na sajtove i nema polja za API ključeve.

Aktivni izvori su javne stranice Titlovi.com, javni Stremio OpenSubtitles dodatak i lokalne datoteke. Kada nema rezultata, lokalni Whisper/Qwen postupak može napraviti titl, pod uslovom da su modeli prethodno preuzeti kroz podešavanja. Stari API i cloud adapteri postoje kao neaktivne bibliotečke klase; nisu povezani sa aplikacijom niti uslov njenog rada.

## Izvršene provere u Linux oblaku

| Provera | Rezultat i granice dokaza |
| --- | --- |
| Ponovljivo podešavanje okruženja | `bash scripts/setup-cloud.sh` završio sa exit 0; zaključani NuGet restore, potrebni alati i WPF kompilacija. SDK 10.0.401, PowerShell 7.5.4, NSIS 3.11. |
| Core testovi | **295 prošlo, 0 palo, 0 preskočeno**. Obuhvaćeni su javni izvori, lokalne datoteke, izbor jezika/epizode u ZIP-u, otkazivanje, verifikacija modela i lokalni generator. Većina su testovi lokalnih odgovora i zamenskih AI adaptera. |
| WPF kompilacija | **0 upozorenja, 0 grešaka**. Kompilacija za Windows na Linux-u nije izvršavanje na Windows-u. |
| Titlovi.com stvarna pretraga | Bez naloga i API ključa: Charade (1963) dao 6 sr/hr rezultata; Night of the Living Dead (1968) dao 10. |
| Titlovi.com izabrano preuzimanje | Srpski ZIP 43128 B → 1418 vremenski označenih titlova; hrvatski ZIP 31736 B → 887. Dijalektička slova očuvana. Ovo ne dokazuje prikaz u Windows prozoru ili sinhronizaciju sa određenom kopijom filma. |
| Pakovanje biblioteka | Stvarni native DLL-ovi provereni po PE arhitekturi: x64 ima 28 AMD64 AI biblioteka, x86 8 I386 Whisper biblioteka i nema LLama backend-a. Uklonjene pogrešne arhitekture/OS, popravljen x86 naziv Whisper DLL-a i provereno 19 inventara licenci po paketu. Neodgovarajuća arhitektura se odbija. |
| Native AI adapteri | Kompajlirani sa stvarnim Whisper.net 1.8.1 / LLamaSharp 0.27.0 zavisnostima. Linux CPU llama biblioteka i native JSON grammar parser učitani; **modeli nisu pokrenuti**. |
| VC runtime helper | PowerShell parsiranje i grane sa postojećim runtime-om/greškom proverene. Microsoft installer nije preuzet niti izvršen na Windows-u. |
| Puna izgradnja instalacionih paketa | `scripts/build.ps1 -BuildUniversalInstaller` nakon popravke izbora titla završio sa exit 0: 295 Core testova, self-contained win-x64/win-x86 publish, oba instalatera i univerzalni NSIS paket. Bez compiler/NSIS upozorenja. Ovo potvrđuje cross-kompilaciju i pakovanje, ne Windows instalaciju/reprodukciju. |

Zapis stvarne javne pretrage bez sadržaja titlova nalazi se u `validation/titlovi-public-live.json`, a sažetak završne cross-izgradnje u `validation/keyless-cross-build.json`. Lokalni detaljni logovi su u `artifacts/keyless-setup.log`, `artifacts/keyless-universal-build.log` i `artifacts/test-results/`; nisu deo izvornog ZIP-a. Lokalni Windows runner je na Linux-u pravilno odbio izvršavanje i upisao svež FAILED izveštaj; to je očekivano odbijanje pogrešnog OS-a, ne Windows test.

## Neizvršene provere i preostala ograničenja

- **Windows reprodukcija i vidljiv titl:** nisu izvršeni novi native/WPF testovi, instalacija ili deinstalacija na Windows 10 x64, Windows 11 x64 ili Windows 10 x86. Korisnikov raniji izveštaj da je video radio odnosi se na raniji paket i ne potvrđuje ovu izgradnju. Windows 11 nema x86 izdanje.
- **Jedan izbor u plejeru:** stvarno proveriti da izbor preuzima i prikazuje taj prevod; brzu promenu A→B, skidanje oznake, otkazivanje i otvaranje drugog filma. Zamenski odgovori i kompilacija ne potvrđuju libVLC asinhrono ponašanje na Windows-u.
- **OpenSubtitles javni dodatak:** integracija i 24 testova protokola postoje. Ovaj oblak za Stremio vraća CONNECT 403 pre dolaska do izvora; stvarna pretraga/preuzimanje nisu potvrđeni. Dodatak može menjati raspoloživost i kvote.
- **Tri dodatna tražena izvora:** nema potvrđenog dozvoljenog automatskog puta za Prijevodi-Titlovi.org (pretraga/download ograničeni robots pravilima i članstvom), Prijevodi Online (robots zabranjuje ChatGPT-User/GPTBot) i SubDL javni download/CDN (robots zabrana; zvanični API traži ključ). Ne zaobilazimo te zabrane. Zahtev za svih pet automatskih izvora još nije ispunjen.
- **Lokalno prepoznavanje i prevod:** Whisper small i Qwen3-4B-Q4_K_M su implementirani, ali Hugging Face/model CDN i Microsoft CDN nisu bili dostupni ovom oblaku. Stvarni modeli, sr/hr/bs kvalitet, brzina i potrošnja memorije nisu provereni. Preuzimanje koristi zvanični LFS SHA-256 i immutable revision; verifikacija se ne isključuje. AI radi samo na lokalnoj video datoteci i u x64 procesu; najmanje 8 GB RAM, preporučeno 16 GB. x86 ima reprodukciju i gotove titlove, bez ovog AI postupka.
- **Sinhronizacija:** logika pomaka/tempa prolazi testove; poravnanje uz stvaran govor na početku, sredini i kraju filma i dalje treba proveriti. Različita filmska izdanja mogu tražiti drugi prevod.
- **Trajanje AI obrade i cache:** obrađuje se ceo film; rezultat može stići nakon kraja reprodukcije. Generisani SRT ostaje u cache-u, ali njegova automatska veza sa filmom pri ponovnom otvaranju još nije implementirana. Ovo je preostali funkcionalni nedostatak, ne prošla provera automatskog prevoda.
- **Instalacija:** x64 AI biblioteke zahtevaju Microsoft VC runtime ≥14.44, uključujući OpenMP. Installer ga proverava i po potrebi preuzima zvanični installer uz SHA-256 i Microsoft potpis; mogu biti potrebni Internet, UAC i restart. Stvarno Windows preuzimanje/instalacija runtime-a nisu izvršeni. Python, PowerShell 7, .NET SDK i zasebni VLC nisu zavisnosti instaliranog plejera; razvojni testovi koriste PowerShell 7 i alate opisane u uputstvu.
- **Prepoznavanje filma:** nejasan naziv poput `video.mp4`, odsutni/pogrešni metapodaci i nepoznat hash ne garantuju prepoznat naslov. Direktni HTTP(S) video izvori su podržani; HTML plejeri i DRM nisu. ZIP/SRT/VTT/ASS se obrađuju; RAR se prijavljuje kao nepodržan.
- **Priloženi poslednji ZIP:** servis prenosa ga je odbio jer prelazi 32 MiB. Zato nije pregledan njegov sadržaj niti upoređen njegov binary sa repozitorijumom. Ove izmene popravljaju kod u repozitorijumu; ne predstavljaju potvrdu popravke tog konkretnog binary-ja.
- **Distribucija:** ostaju provere licenci/kodova tačno upakovanih libVLC plugina i izbor licence aplikacije/potpisa pre objavljivanja instalacije.

## Preostala Windows potvrda

Pokrenuti `scripts/test-local-windows.ps1` po `TEST-ON-YOUR-PC.md`. Izveštaji moraju biti sveži, sa stvarnom OS/proces arhitekturom i svim očekivanim proverama. Test generiše kratku reprodukciju, native dekodiranje/audio, WPF kontrole, izbor lokalnog titla i segmentaciju preko 600 sekundi. Izveštaj sa PASS ne zamenjuje ljudsku potvrdu vidljivih titlova, čujnog zvuka, stvarnih javnih izvora i lokalnog prevoda.

Za Titlovi koristiti dozvoljen lokalni film sa jasnim nazivom/godinom ili epizodom, videti spisak, izabrati sr/hr/bs, proveriti tekst i govor na više mesta. Sa promenom izbora i skidanjem oznake poslednja korisnička odluka mora ostati važeća. Za AI jednom preuzeti modele kroz podešavanja na x64, zatim proveriti generisanje i poravnanje na licenciranom uzorku, uključujući otkazivanje i prelazak na drugi film.

GitHub isporuka je odobrena samo kao izvorni kod u grani `stevanplejer-windows-source-20261009`. Hosted workflow-i, release i kandidat instalacije ne objavljuju se. Podešavanje cloud okruženja može biti sačuvano kao nacrt; to ne objavljuje okruženje niti dokazuje buduće Windows izvršavanje.
