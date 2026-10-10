# СТЕВАНplejer za Windows

Plejer sam prepoznaje film ili epizodu, traži srpske, hrvatske i bosanske
titlove i prikazuje spisak u svom prozoru. Jednim označavanjem biraš prevod:
plejer ga preuzima, proverava i uključuje uz video. U podešavanjima nema API
ključeva, naloga niti upućivanja na sajt radi ručnog traženja titla.

**Završno izdanje još nije potvrđeno.** Dostupan je izvorni kod sa proverama;
gotov Windows program neće biti objavljen dok stvarna reprodukcija,
instalacija, prikaz titlova i lokalno prevođenje ne prođu prihvatne testove.
Rezultati i preostale provere vode se u [VALIDATION.md](VALIDATION.md).

## Kako radi izbor titla

1. Otvoriš lokalni film ili direktnu HTTP(S) adresu video sadržaja.
2. Plejer koristi naziv datoteke, godinu, sezonu/epizodu, dostupni naslov iz
   metapodataka i, za lokalnu datoteku, hash. Sam pokreće pretragu.
3. U spisku označiš jedan ponuđeni prevod. Plejer preuzima baš taj prevod i
   bira njegov stvarni libVLC subtitle zapis. Drugi prevod biraš istim postupkom.

Hash dopunjuje pretragu zasnovanu na poznatom nazivu/metapodacima. Ne
prepoznaje samostalno nepoznat film iz proizvoljnog imena kao `video.mp4`.
Skidanje oznake isključuje titl i otkazuje njegovo učitavanje ako još traje.

Postoje i lokalni izvori: fascikla filma, Downloads i cache plejera. Lokalni
titl mora odgovarati naslovu, godini/epizodi i jeziku. Preuzeti titlovi čuvaju
se u `%LOCALAPPDATA%\StevanPlayer\Subtitles`; podešavanja su u istom
korisničkom direktorijumu. Spisak nije dokaz da je titl vremenski usklađen
sa svakim izdanjem filma. Dostupni su ručni pomak i lokalno usklađivanje.

Podržani su SRT, VTT, ASS i ZIP sa proverom izabrane epizode/jezika. RAR
trenutno nije podržan. Nejasan naziv filma ili serije može sprečiti pouzdano
poklapanje; pretraga ne garantuje prevod za svaki naslov.

## Izvori bez korisničkih ključeva

| Izvor | Trenutna automatska podrška i dokaz |
| --- | --- |
| Titlovi.com | Javne stranice za pretragu, izabrani detalj i objavljeni download, uz ograničenje učestalosti i robots pravila. Stvarna provera za *Charade (1963)* dala je šest rezultata; izabrani srpski i hrvatski ZIP dali su ispravne vremenski označene SRT titlove. Prikaz uz film na Windows-u još nije potvrđen. |
| OpenSubtitles | Javni zvanični Stremio OpenSubtitles v3 dodatak i Cinemeta za identifikaciju. Korisniku ne treba ključ. Protokol je testiran primerima; cloud CONNECT 403 sprečio je stvarnu mrežnu proveru tog puta. |
| Prijevodi-Titlovi.org | Automatski dozvoljen put za traženje/preuzimanje nije potvrđen; relevantne putanje su ograničene robots pravilima i članstvom. |
| Prijevodi Online | Potvrđena adresa je `https://www.prijevodi-online.org/`. Robots pravila izričito odbijaju ChatGPT-User/GPTBot; dozvoljen automatski API nije potvrđen. |
| SubDL | Javni katalog može da se pročita, ali robots pravila zabranjuju relevantna preuzimanja, a zvanični API traži ključ. Automatsko preuzimanje bez ključa nije uključeno. |

Preostala tri izvora nisu predstavljena kao završene integracije. Plejer
prijavljuje kvar ili ograničenje dostupnog izvora; ne zaobilazi prijavu,
CAPTCHA, zabrane preuzimanja ili ograničenja servisa. OpenSubtitles put je
opisan u [dokazima o javnom dodatku](StevanPlayer.Core/Providers/KEYLESS-OPENSUBTITLES-SOURCES.md).
Starije klase za API servise ostaju u Core biblioteci i njenim testovima,
ali ih sadašnja Windows aplikacija ne aktivira i ne učitava njihove ključeve.

## Kada prevod nije dostupan

Na x64 računaru plejer može lokalno izdvojiti zvuk, prepoznati govor pomoću
Whisper small modela i prevesti vremenski označene replike pomoću Qwen3 4B
Q4_K_M modela. Podešavanja nude jednokratno preuzimanje modela, približno
3 GB. Preuzimanje koristi zvanične javne Hugging Face repozitorijume,
određenu reviziju i proveru LFS SHA-256; fajlovi se ponovo proveravaju pre
obrade. Posle toga obrada radi lokalno bez slanja zvuka/teksta na servis.

Potrebni su x64 proces i najmanje 8 GB RAM-a; preporučeno je 16 GB. Obrada
na CPU-u može trajati duže od samog filma; generisani titl nije trenutni
prevod tokom gledanja. Kada obrada završi, vrati film na početak da ga
gledaš sa tim titlom. SRT ostaje u cache-u; automatsko prepoznavanje ranije
generisanog cache fajla pri ponovnom otvaranju filma još nije rešeno.
Kada je omogućena i modeli su preuzeti, lokalna
obrada počinje ako automatska pretraga ne vrati titl. Dostupno je i lokalno
usklađivanje izabranog titla. Trenutno se obrađuje lokalni video, ne mrežni
stream. Kvalitet srpskog/hrvatskog/bosanskog prevoda i usklađivanja još nije
potvrđen stvarnom obradom: cloud pristup modelima nije uspeo i modeli nisu
ovde preuzeti. Rezultat se mora proveriti na početku, sredini i kraju filma.

## Windows i arhitekture

| Računar / OS | Cilj paketa | Lokalno prepoznavanje i prevod |
| --- | --- | --- |
| Windows 10 x64, Intel/AMD | `win-x64` | Predviđeno; stvarni testovi čekaju |
| Windows 11 x64, Intel/AMD | `win-x64` | Predviđeno; stvarni testovi čekaju |
| Windows 10 x86, Intel/AMD | `win-x86`, reprodukcija i pretraga titlova | Nije dostupno |
| Windows 11 x86 | Takvo izdanje Windows-a ne postoji | — |
| ARM64 Windows | Instalater odbija nepokrivenu arhitekturu | — |

Univerzalni instalater bira odgovarajući x64/x86 paket. To još nije dokaz
rada na svim ovim sistemima; x86 test pod WOW64 na Windows 11 x64 ne
potvrđuje rad na Windows 10 32-bit. Glavni cilj sa svim funkcijama ostaje
Windows 10/11 x64. Za običan Windows 10 Home/Pro cilj testiranja je ažuriran
22H2; njegova redovna Microsoft podrška je završena. .NET 10 zvanična OS
matrica posebno navodi podržana LTSC/Enterprise izdanja. Ne garantuje se
rad na svakoj staroj Windows 10 verziji.

## Interfejs i instalacija

WPF interfejs ima sopstvenu ikonicu, listu reprodukcije, prevlačenje videa,
reprodukciju/pauzu/stop, seek, jačinu i utišavanje zvuka, puni ekran,
izbor audio/titl zapisa i vremenski pomak. Video i zvuk obrađuje libVLC.
HTML stranica nije direktna adresa video stream-a.

Paket sadrži .NET/WPF i libVLC; za rad aplikacije nisu potrebni Python,
PowerShell 7, razvojni SDK, Node.js, FFmpeg ili zaseban VLC. x64 lokalni AI
koristi Microsoft Visual C++ runtime 14.44 ili noviji. Ako odgovarajući
runtime nedostaje, instalater preuzima zvanični Microsoft paket, proverava
SHA-256 i Microsoft Authenticode potpis, pa pokreće njegovu instalaciju.
Taj korak traži internet i može tražiti administratorsku UAC potvrdu ili
restart. Pomoćna skripta koristi Windows-ov ugrađeni PowerShell; to nije
zavisnost samog plejera.

Plejer se instalira za trenutnog korisnika u
`%LOCALAPPDATA%\Programs\StevanPlayer`, sa Start menu i opcionom Desktop
prečicom. Deinstalacija uklanja poznate datoteke aplikacije, čuva korisnička
podešavanja, modele, titlove i nepoznate datoteke i ne uklanja zajednički
Visual C++ runtime. Instalater još nije Authenticode potpisan; potpisivanje
i pripadajući izvori/licence su uslovi pre javne distribucije.

## Izgradnja i stvarni testovi

Razvoj traži SDK iz `global.json` (10.0.401), PowerShell 7, Node.js 22 ili
noviji i NSIS 3.11. Iz korena repozitorijuma:

```powershell
./desktop/scripts/build.ps1 -BuildUniversalInstaller -MakeNsis 'C:\Program Files (x86)\NSIS\makensis.exe'
```

Skripta koristi zaključane NuGet zavisnosti, pokreće Core testove i pravi
self-contained `win-x64`/`win-x86` pakete, licence, manifest vlasništva i
instalatere u `desktop/artifacts/`. Native biblioteke ostaju uz aplikaciju;
jedan izdvojeni `STEVANplejer.exe` nije ceo paket. `*candidate*` datoteke su
isključivo interni rezultati izgradnje, ne javno probno izdanje.

Za Windows provere koristi [TEST-ON-YOUR-PC.md](TEST-ON-YOUR-PC.md) i
`scripts/test-local-windows.ps1`. Native test proverava stvarno dekodiranje,
odabran spoljašnji titl, pomak, pauzu/seek i izdvojeni PCM zvuk. WPF test
otvara prozor, bira titl iz spiska u plejeru, proverava kontrole, puni ekran,
promenu videa i čuva stvarne snimke radne površine. To ne zamenjuje proveru
vidljivog teksta, zvučnika, pravog filma, internet izvora i kvaliteta prevoda.
FFmpeg se koristi samo za sintetički test video i ne isporučuje se u plejeru.

Linux cloud može pokrenuti Core testove i cross-publish, ali nema Windows
reprodukciju. `scripts/setup-cloud.sh` priprema proverene razvojne alate.
GitHub grana `stevanplejer-windows-source-20261009` sadrži samo izvorni ZIP,
kod i uputstva; GitHub testovi i javni release nisu pokrenuti. Pogledaj i
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
