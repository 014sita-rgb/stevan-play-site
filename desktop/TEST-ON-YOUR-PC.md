# Provera na tvom Windows računaru

Ovo uputstvo prati izvorni kod i prihvatne provere. Završno izdanje još nije
potvrđeno; lokalno napravljeni paketi služe proveri. Plejer sam traži titlove:
ne traži ključeve ili odlazak na sajtove za preuzimanje.

## 1. Priprema za izgradnju iz izvora

Tvoj Windows 11 x64 i već instalirani PowerShell 7 odgovaraju ovom postupku.
Raspakuj izvorni ZIP u posebnu fasciklu, na primer C:\StevanValidation.
Za ove razvojne provere potrebni su:

- [.NET SDK 10.0.401](https://dotnet.microsoft.com/download/dotnet/10.0), kao u desktop/global.json.
- [Node.js](https://nodejs.org/) 22 ili noviji, za pripremu paketa.
- [NSIS 3.11](https://nsis.sourceforge.io/Download), za instalater.
- [FFmpeg](https://ffmpeg.org/download.html), dostupan kao ffmpeg u PATH,
  za sintetički video bez tuđeg filma.

Ovo su razvojni alati. Za rad instaliranog plejera nisu potrebni Python,
PowerShell 7, SDK, Node.js, FFmpeg ili zaseban VLC. Lokalni AI na x64 traži
Visual C++ runtime 14.44 ili noviji. Instalater proverava i po potrebi
preuzima zvanični, potpisani Microsoft paket; tada su potrebni internet i
moguća UAC potvrda ili restart. Ako za proveru pokrećeš direktno publish
fasciklu bez instalatera, odgovarajući runtime mora već biti instaliran.

Preuzimaj alate iz zvaničnih izvora uz ponuđene potpise/checksum-e. Ne
isključuj Defender/SmartScreen, TLS proveru ili execution policy. Ako
organizacija blokira skriptu, koristi njen odobren postupak. Otvori nov
PowerShell 7 posle instalacije alata.

## 2. Automatske Windows provere

U PowerShell-u 7 pređi u fasciklu u kojoj je raspakovan desktop i pokreni:

~~~powershell
./desktop/scripts/test-local-windows.ps1
~~~

Za x64 i x86 procese na tvom Windows 11 x64 i univerzalni instalater:

~~~powershell
./desktop/scripts/test-local-windows.ps1 -TestBothArchitectures
~~~

Za x86 proces koristi -RuntimeIdentifier win-x86. x86 pod WOW64 na x64
Windows-u ne dokazuje rad na Windows 10 32-bit. Na stvarnom x86 OS-u treba
obezbediti odgovarajuće razvojne alate; lokalni AI tamo nije dostupan.
Windows 11 nema x86 izdanje, a ARM64 nije deo paketa.

Ako je NSIS na drugom mestu, dodaj -MakeNsis 'C:\putanja\makensis.exe'.
Skripta koristi zaključane zavisnosti, pokreće Core testove, pravi pakete i
kompajlira instalater, zatim pokreće stvarni libVLC decode, WPF kontrole i
puni ekran, izbor titla sa spiska u plejeru i izdvajanje zvuka preko granice
od 600 sekundi. Skripta ne pokreće instalater na tvom nalogu.

Tokom provere otvaraju se prozori. Ne koristi miš/tastaturu u test prozoru
i ne zaključavaj ekran. Provera staje na prvoj grešci. JSON/TRX izveštaji,
log i normalni/fullscreen snimci nalaze se u desktop/artifacts/test-results/.
Sažetak je local-windows-validation-win-x64.json, odgovarajući win-x86 ili
both-architectures fajl. Status AUTOMATED_CHECKS_PASSED_NOT_A_RELEASE znači
da su prošle samo navedene automatske provere.

Pre deljenja pregledaj logove i slike jer mogu sadržati lične putanje i
delove radne površine. Za dijagnostiku su dovoljni pregledani izveštaji;
settings i Models fascikle nisu potrebne.

## 3. Provera filma i automatskog izbora prevoda

Pokreni desktop/artifacts/win-x64/STEVANplejer.exe za proveru. Cela publish
fascikla mora ostati uz EXE. Za x86 koristi odgovarajuću win-x86 fasciklu.

Najpre otvori desktop/artifacts/test-fixture/Stevan.Validation.2026.mp4.
Proveri sliku, ton na stvarnim zvučnicima, vidljiv srpski titl, pauzu,
seek, mute, F11/Escape i promenu veličine. Proveri 100%/150% DPI i izbor
ugrađenih audio/titl zapisa. Automatski izabran libVLC zapis i snimak
ekrana sami po sebi ne dokazuju čitljiv i pravilno usklađen titl.

Za stvarnu internet proveru koristi zakonito dostupan strani film sa
jasnim naslovom i godinom u nazivu datoteke, na primer svoju kopiju
Charade.1963.mp4. Na Titlovi.com je ovde potvrđena pretraga za taj naslov,
sa šest rezultata i ispravnim srpskim/hrvatskim preuzimanjem. Nije potvrđeno
poklapanje sa svakom kopijom filma.

1. U podešavanjima ostavi automatsku pretragu uključenu. Otvori film i
   sačekaj da plejer sam prikaže spisak prevoda.
2. Označi jedan ponuđeni prevod. Potvrdi da se preuzima i vidi preko filma,
   bez pregledača ili dodatnog ručnog preuzimanja.
3. Označi drugi prevod. Potvrdi da prikaz pređe na njega i da je aktivan
   samo izabrani zapis. Proveri srpski/hrvatski/bosanski kada postoje.
   Skini oznaku: titl treba da se isključi. Skidanje oznake tokom
   preuzimanja treba da otkaže učitavanje bez naknadnog uključivanja.
4. Ponovi sa jednom serijom: naslov, sezona i epizoda moraju odgovarati
   titlu, uključujući ZIP koji sadrži više epizoda.
5. Proveri vreme na početku, sredini i kraju. Za drugo izdanje filma
   probaj ručni pomak; zabeleži stvarnu grešku ili nepoklapanje.

Zabeleži Windows verziju, izvor prikazan na stavci, naziv/izdanje filma,
jezik i ishod. Odvojeno proveri javni OpenSubtitles dodatak ako vrati
rezultate: taj mrežni put nije mogao da se proveri iz cloud okruženja.
Prijevodi-Titlovi.org, Prijevodi Online i SubDL još nemaju potvrđen dozvoljen
automatski put bez ključeva; ne računaj ih kao prošlu integraciju.
Hash je dopuna poznatom naslovu, ne nezavisno prepoznavanje nepoznatog
filma nazvanog video.mp4 bez upotrebljivih metapodataka.

## 4. Lokalno prepoznavanje, prevod i sinhronizacija

Ovaj test zahteva x64 plejer, najmanje 8 GB RAM-a (preporučeno 16 GB),
prostor za modele i vreme za CPU obradu. Ovde nije izvršen stvarni test
modela; prolaz testova logike ne dokazuje kvalitet generisanog titla.

U Podešavanjima izaberi Preuzmi lokalne modele (~3 GB). Potvrdi uspešno
preuzimanje i proveru oba modela. Omogući lokalno prepoznavanje/prevod i
sačuvaj. Zatim otvori lokalni test snimak sa jasnim stranim govorom za koji
automatska pretraga nema titl. Plejer treba da sam započne lokalnu obradu.
Možeš koristiti i dugme za lokalnu obradu u plejeru.

Proveri da rezultat ima razumljiv prevod na izabrani jezik i odgovarajuća
vremena; probaj srpski, hrvatski i bosanski. Nakon prvog preuzimanja
ponovi lokalnu obradu bez interneta. Proveri otkazivanje, promenu filma
tokom obrade i odsustvo titla iz prethodnog filma na novom videu.
Obrada celog filma može završiti tek nakon kraja reprodukcije: vrati
film na početak i proveri generisani titl. To nije trenutni prevod govora.
Generisani SRT se čuva u cache-u, ali njegovo automatsko povezivanje pri
ponovnom otvaranju istog filma još nije implementirano.

Za sinhronizaciju prvo izaberi spoljašnji titl, zatim pokreni lokalno
usklađivanje ili uključi automatsko usklađivanje u podešavanjima. Proveri
početak, sredinu i kraj, sa poznatim pomakom i promenom brzine. Loš prevod,
pogrešna epizoda, nevidljiv titl ili loše vreme znače neuspeh provere.

## 5. Instalacija i uklanjanje

Na novom zasebnom Windows test nalogu ili VM proveri napravljeni
universal-candidate-setup.exe. Proveri izbor x64/x86, Start menu i opcionu
Desktop prečicu i reprodukciju bez razvojnih alata. Na x64 proveri oba
slučaja: već instaliran odgovarajući Visual C++ runtime i njegov nedostatak.
U drugom slučaju očekuju se proveren Microsoft download i normalna UAC
potvrda; odbijanje ili neuspeh ne sme ostaviti delimično instaliran plejer.
Proveri i traženi restart ako ga Microsoft instalater prijavi.

Proveri normalnu deinstalaciju: korisnička podešavanja, modeli, cache,
nepoznate datoteke i zajednički Visual C++ runtime moraju ostati. Ne
testiraj uninstall nad vrednim postojećim podacima. Ne postavljaj CI=true
na svom nalogu da bi zaobišao zaštitu u test-installer.ps1; taj automatski
test je namenjen samo izolovanom test nalogu.

Svi preostali uslovi su u [VALIDATION.md](VALIDATION.md). Ove skripte ništa
ne objavljuju i ne potvrđuju završno izdanje.

## Isporuka izvora

Izvorni ZIP sa ovim uputstvom nalazi se u odobrenoj GitHub grani
stevanplejer-windows-source-20261009 repozitorijuma 014sita-rgb/stevan-play-site:
[preuzmi ZIP](https://github.com/014sita-rgb/stevan-play-site/raw/refs/heads/stevanplejer-windows-source-20261009/downloads/STEVANplejer_Windows_Universal_Source.zip).
Sadrži kod i test skripte, bez javnog gotovog EXE izdanja. Glavna grana,
GitHub Actions i javni release nisu deo ove isporuke.
