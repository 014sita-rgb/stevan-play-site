# Poreklo punih tekstova licenci

Tekstovi u ovom direktorijumu preuzeti su preko proverenog HTTPS-a iz
zvaničnih upstream repozitorijuma. Verzije za native AI biblioteke prate
stvarne Whisper.net 1.8.1 i LLamaSharp 0.27.0 pakete.

| Fajl | Tačan upstream dokument |
| --- | --- |
| COPYING.LIB | [COPYING.LIB](https://raw.githubusercontent.com/videolan/vlc/3.0.24/COPYING.LIB) |
| COPYING | [COPYING](https://raw.githubusercontent.com/videolan/vlc/3.0.24/COPYING) |
| Whisper.net-LICENSE.txt | [Whisper.net-LICENSE.txt](https://raw.githubusercontent.com/sandrohanea/whisper.net/b36b61db21ed682c19ae96102318f6c815b00d2d/LICENSE) |
| whisper.cpp-LICENSE.txt | [whisper.cpp-LICENSE.txt](https://raw.githubusercontent.com/ggml-org/whisper.cpp/6e7629b14680e6c958fef96cdea602306c0f7433/LICENSE) |
| LLamaSharp-LICENSE.txt | [LLamaSharp-LICENSE.txt](https://raw.githubusercontent.com/SciSharp/LLamaSharp/7cbbc45e421d55794d5050d126e0b96511007007/LICENSE) |
| llama.cpp-LICENSE.txt | [llama.cpp-LICENSE.txt](https://raw.githubusercontent.com/ggml-org/llama.cpp/3f7c29d318e317b63f54c558bc69803963d7d88c/LICENSE) |

SHA-256 sačuvanih dokumenata:

```
dc626520dcd53a22f727af3ee42c770e56c97a64fe3adb063799d8ab032fe551  COPYING.LIB
8177f97513213526df2cf6184d8ff986c675afb514d4e68a404010521b880643  COPYING
778628a2167e54fcb03f28957004a45f339386d38dc11243b5a8687efcf82937  Whisper.net-LICENSE.txt
e562a2ddfaf8280537795ac5ecd34e3012b6582a147ef69ba6a6a5c08c84757d  whisper.cpp-LICENSE.txt
86d814671934e4f3caa669c825e4dad4a8d7aaf829d9b380f05dc8555e7cf5ed  LLamaSharp-LICENSE.txt
94f29bbed6a22c35b992c5c6ebf0e7c92f13b836b90f36f461c9cf2f0f1d010d  llama.cpp-LICENSE.txt
```

Ovo su licence, ne arhive pripadajućeg izvornog koda. Obaveze pre javne
binarne distribucije navedene su u [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)
i [VALIDATION.md](../VALIDATION.md).

Modeli nisu ovde uključeni. Njihov downloader bira zvaničnu reviziju i
LFS SHA-256 i čuva potvrdu uz modele. Ako se težine modela kasnije uključe
u paket, uz njih moraju biti licenca i obaveštenja baš te revizije.

`scripts/prepare-publish.mjs` dodatno kopira licencne dokumente stvarno
isporučenih NuGet paketa i beleži njihove verzije/licence u
`licenses/bundled-package-inventory.json` u Windows publish direktorijumu.
