# СТЕВАНplejer — izvorni ZIP

[Preuzmi ZIP sa kodom i uputstvom](https://github.com/014sita-rgb/stevan-play-site/raw/refs/heads/stevanplejer-windows-source-20261009/downloads/STEVANplejer_Windows_Universal_Source.zip)

Plejer sam prepoznaje film, traži dostupne srpske/hrvatske/bosanske titlove
i prikazuje spisak. Označavanje jednog prevoda pokreće njegovo preuzimanje
i uključivanje u plejeru. Nema unosa API ključeva ili ručnog traženja na
sajtovima.

Raspakuj ZIP u posebnu fasciklu i pročitaj desktop/TEST-ON-YOUR-PC.md.
Na tvom Windows 11 x64 sa PowerShell-om 7, nakon dodavanja razvojnih alata
iz uputstva, pokreni iz raspakovane fascikle:

~~~powershell
./desktop/scripts/test-local-windows.ps1 -TestBothArchitectures
~~~

Univerzalni instalater bira x64 ili x86. Glavni cilj sa lokalnim prevodom
je Windows 10/11 x64; x86 ima reprodukciju/pretragu titlova, bez lokalnog AI.
Windows 11 x86 ne postoji. Instalirani plejer ne traži Python, PowerShell 7
ili razvojne alate. x64 lokalni AI traži Visual C++ runtime; instalater ga
po potrebi preuzima i proverava, uz moguću UAC potvrdu/restart. Modeli
(~3 GB) preuzimaju se jednom iz podešavanja.

**Ovo je izvorni paket sa proverama; završno Windows izdanje nije potvrđeno.**
295 Core testova i WPF kompajliranje prošli su u Linux cloud okruženju.
Stvarna javna Titlovi.com pretraga za Charade (1963) dala je šest rezultata;
izabrani srpski i hrvatski ZIP dali su ispravne vremenski označene titlove.
Windows prikaz, instalacija i stvarna obrada lokalnim modelima još čekaju
provere. OpenSubtitles javni mrežni put i preostala tri tražena izvora
imaju ograničenja opisana u desktop/VALIDATION.md.

ZIP sadrži kod, licence i test uputstva. Ne sadrži gotov EXE, modele,
lična podešavanja ili GitHub Actions workflow. Isporuka je u grani
stevanplejer-windows-source-20261009; main i javni release nisu menjani.
