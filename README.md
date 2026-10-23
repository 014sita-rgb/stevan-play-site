# STEVAN Play sajt

Javni sajt za preuzimanje STEVAN Play aplikacije i prikaz STEVAN kataloga.

Predviđena GitHub Pages adresa:
https://014sita-rgb.github.io/stevan-play-site/

Glavni Android paket treba da se nalazi u korenu repozitorijuma pod tačnim nazivom:
`STEVAN_Play.apk`

Sajt automatski proverava da li taj fajl postoji i tek tada aktivira dugme „PREUZMI STEVAN PLAY ZA ANDROID“.

Aktuelni testni APK: STEVAN Play 1.2.2 AndroidX Fix, package `rs.stevan.play`.

## Web aplikacije (23.10.2026.)

Katalog `katalog/catalog.json` zadržava originalni `apps` niz za Android APK-ove i uvodi **`webApps`** niz, kako postojeći native APK-klijenti ne bi slučajno pokušali da instaliraju veb-link kao APK. GitHub Pages prikazuje oba niza. Tip `web` zahteva `webUrl` sa HTTPS adresom; STEVAN pretraživač otvara postojeći Cloudflare PWA, bez kopiranja baze ili Worker-a na GitHub Pages.

**Važno:** postojeći preuzimljivi `playstevan.apk` nije menjan. Njegova Android UI podrška za `webApps` zahteva originalni Android Studio izvorni projekat i isti release signing sertifikat. Dok se to ne ugradi, nova WEB stavka dostupna je na javnom STEVAN Play sajtu, ne nužno u interfejsu ranije instaliranog APK-a.
