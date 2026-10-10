# Licence zavisnosti i pripadajući izvori

Aplikacija isporučuje zasebne .NET sklopove, native VLC biblioteke/plugin-e
i lokalne AI mehanizme. Python nije uključen niti potreban. Ovaj dokument
i licenses/ direktorijum moraju pratiti svaki distribuirani paket.

| Komponenta | Licenca | Tačan izvor / dokument |
| --- | --- | --- |
| LibVLCSharp i LibVLCSharp.WPF 3.10.1 | LGPL-2.1-or-later, prema oba NuGet paketa | [VideoLAN commit 4896d0e06d19ac40b46802cf7dc167d432f3fd63](https://code.videolan.org/videolan/LibVLCSharp/-/tree/4896d0e06d19ac40b46802cf7dc167d432f3fd63); licenses/COPYING.LIB |
| VideoLAN.LibVLC.Windows 3.0.24 / libVLC | LGPL-2.1-or-later za libVLC; VLC moduli imaju zasebne LGPL/GPL i druge licence | [VLC 3.0.24 izvorna arhiva](https://download.videolan.org/pub/videolan/vlc/3.0.24/vlc-3.0.24.tar.xz); [tagovani izvor](https://code.videolan.org/videolan/vlc/-/tree/3.0.24); [NuGet build skripte](https://code.videolan.org/videolan/libvlc-nuget); licenses/COPYING.LIB i licenses/COPYING |
| Whisper.net, Runtime i Runtime.NoAvx 1.8.1 | MIT | [whisper.net commit b36b61db21ed682c19ae96102318f6c815b00d2d](https://github.com/sandrohanea/whisper.net/tree/b36b61db21ed682c19ae96102318f6c815b00d2d); licenses/Whisper.net-LICENSE.txt |
| whisper.cpp, native biblioteke iz Whisper.net paketa | MIT | [commit 6e7629b14680e6c958fef96cdea602306c0f7433](https://github.com/ggml-org/whisper.cpp/tree/6e7629b14680e6c958fef96cdea602306c0f7433); licenses/whisper.cpp-LICENSE.txt |
| LLamaSharp i LLamaSharp.Backend.Cpu 0.27.0 | MIT | [LLamaSharp commit 7cbbc45e421d55794d5050d126e0b96511007007](https://github.com/SciSharp/LLamaSharp/tree/7cbbc45e421d55794d5050d126e0b96511007007); licenses/LLamaSharp-LICENSE.txt |
| llama.cpp, native CPU biblioteke iz LLamaSharp paketa | MIT | [commit 3f7c29d318e317b63f54c558bc69803963d7d88c](https://github.com/ggml-org/llama.cpp/tree/3f7c29d318e317b63f54c558bc69803963d7d88c); licenses/llama.cpp-LICENSE.txt |
| .NET, Windows Desktop runtime i managed posredne zavisnosti | MIT i uključena zasebna obaveštenja | Tačne verzije i deklarisane licence su u licenses/bundled-package-inventory.json; dokumenti iz stvarnih NuGet paketa nalaze se uz inventar; [runtime izvor](https://github.com/dotnet/runtime), [WPF izvor](https://github.com/dotnet/wpf) |

NuGet lockfile-ovi fiksiraju verzije i content hash. Izgradnja zadržava TLS,
NuGet provere i checksum-e. scripts/prepare-publish.mjs kopira licence,
copyright i notice dokumente iz stvarno uključenih verzija, uključujući
managed posredne zavisnosti, i upisuje inventar. Poreklo dodatih punih
licenci nalazi se u [licenses/SOURCES.md](licenses/SOURCES.md).

Aplikacija ne menja libVLC ili LibVLCSharp. Korisnik može da zameni LGPL
biblioteke kompatibilnim izmenjenim verzijama i debaguje ih za svoje
potrebe. Instalater ne šifruje biblioteke, ne nameće njihov potpis i ne
spaja ih u single-file paket. Potpun aplikacioni izvor i postupak izgradnje
nalaze se u desktop/ direktorijumu.

Pre javne distribucije uz binarne pakete treba sačuvati odgovarajuće
VideoLAN izvore, contrib izvore/build skripte i obaveštenja, uključujući
izvore koje zahtevaju GPL plugin-i stvarno prisutni u paketu. Veza ka
promenljivom upstream sajtu sama ne završava taj korak. Provera pripadajućih
izvora i licencnih obaveza ostaje uslov u VALIDATION.md; interni candidate
paketi nisu javno završno izdanje.

## Lokalni modeli

Težine modela nisu uključene u izvorni ZIP ili Windows publish. Korisnik ih
jednom preuzima iz aplikacije. Model manager čuva reviziju zvaničnog
repozitorijuma, veličinu i LFS SHA-256 i proverava fajl pre upotrebe.

| Model | Zvanični izvor | Deklarisana licenca |
| --- | --- | --- |
| Whisper small, ggml-small.bin | [ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp), zasnovan na [OpenAI Whisper](https://github.com/openai/whisper) | MIT |
| Qwen3 4B Q4_K_M, Qwen3-4B-Q4_K_M.gguf | [Qwen/Qwen3-4B-GGUF](https://huggingface.co/Qwen/Qwen3-4B-GGUF) | Apache-2.0 |

Ovde modeli nisu uspešno preuzeti i nije potvrđena stvarna inferencija.
Ako se težine naknadno distribuiraju u paketu, treba sačuvati licencu,
model card i obaveštenja baš preuzete revizije i ispuniti njihove obaveze.
Licenca aplikacije ne menja licence modela niti preuzetih titlova.

## Instalater i spoljni servisi

NSIS 3.11 nije playback runtime. Njegove licence i izvori su na
[NSIS License](https://nsis.sourceforge.io/License) i
[NSIS 3.11 sources](https://sourceforge.net/projects/nsis/files/NSIS%203/3.11/).

Microsoft Visual C++ runtime je zasebna Microsoft komponenta. Instalater
po potrebi preuzima zvanični Microsoft redistributable, proverava očekivani
SHA-256 i validan Microsoft Authenticode potpis pre pokretanja. Microsoft
uslovi i UAC/restart postupak važe za tu instalaciju; runtime se ne
relicencira kao MIT i deinstalacija plejera ga ne uklanja.

Titlovi.com, OpenSubtitles, Stremio/Cinemeta, SubDL i Prijevodi sajtovi su
spoljni servisi; projekat ne tvrdi partnerstvo. Sadašnja aplikacija koristi
javne Titlovi.com stranice i javni zvanični Stremio OpenSubtitles v3
protokol, bez korisničkih ključeva. Uslovi pristupa, kvote, robots pravila
gde se čitaju stranice i prava na pojedinačne titlove i dalje važe.
Ne uključuje se zabranjeno preuzimanje ili zaobilaženje prijave/CAPTCHA.
Stremio SDK nije ugrađen u aplikaciju; javni protokol je dokumentovan u
[izvorima integracije](StevanPlayer.Core/Providers/KEYLESS-OPENSUBTITLES-SOURCES.md).

Korisnikov početni STEVANplejer_Windows_Source.zip pomogao je kao starter
projekat. Nova implementacija i provereni rezultati su odvojeni od tvrdnji
u priloženim dokumentima. Kasniji Universal ZIP nije ovde mogao da se
preuzme zbog ograničenja transfera, pa nije izvršeno binarno poređenje.

FFmpeg se koristi samo za sintetički test video i nije uključen u plejer.
