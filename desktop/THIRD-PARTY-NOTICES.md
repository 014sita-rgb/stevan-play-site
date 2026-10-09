# Dependency licenses and corresponding sources

The published application includes independently replaceable .NET assemblies
and native VLC libraries/plugins; it does not merge them into a single binary.
Python is neither bundled nor required. This document and the `licenses/`
directory must travel with every distributed package.

| Component | License | Exact source / notices |
| --- | --- | --- |
| LibVLCSharp and LibVLCSharp.WPF 3.10.1 | LGPL-2.1-or-later, as declared by both NuGet packages | [VideoLAN source at package commit 4896d0e06d19ac40b46802cf7dc167d432f3fd63](https://code.videolan.org/videolan/LibVLCSharp/-/tree/4896d0e06d19ac40b46802cf7dc167d432f3fd63); full `licenses/COPYING.LIB` |
| VideoLAN.LibVLC.Windows 3.0.24 / libVLC | LGPL-2.1-or-later for libVLC; VLC modules have their own LGPL/GPL and third-party licenses | [VLC 3.0.24 source archive](https://download.videolan.org/pub/videolan/vlc/3.0.24/vlc-3.0.24.tar.xz); [tagged source](https://code.videolan.org/videolan/vlc/-/tree/3.0.24); [official package/build scripts](https://code.videolan.org/videolan/libvlc-nuget); full `licenses/COPYING.LIB` and `licenses/COPYING` |
| .NET and Windows Desktop runtimes | MIT plus included third-party notices | The exact installed version is recorded in `licenses/bundled-package-inventory.json`; included NuGet `LICENSE` / `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT`; [runtime source](https://github.com/dotnet/runtime), [WPF source](https://github.com/dotnet/wpf) |

Package content hashes are pinned by NuGet lockfiles, and installation retains
TLS, NuGet signatures and checksums. `scripts/prepare-publish.mjs` copies every
license/copyright/notice document found in the actual shipped package versions
and records that inventory. Where VideoLAN packages omit COPYING, the full
license documents vendored from the official VLC 3.0.24 source are supplied.

The application has not modified libVLC or LibVLCSharp. Users may replace the
LGPL libraries with compatible modified versions for their own use and may
debug those modifications. No installer encryption, library signing check, or
single-file packing blocks replacement. Rebuild instructions and the complete
application source are in this repository's `desktop/` directory.

Before public distribution, archive the exact corresponding VideoLAN source,
contrib dependency sources/build scripts and source notices alongside binaries,
including the source required by any GPL plugins actually bundled. Merely
linking to a moving upstream website does not complete that distribution step.
This source-package/legal distribution check remains an explicit release gate
in VALIDATION.md; candidate artifacts are internal build evidence.

The NSIS installer uses NSIS 3.11. NSIS is not a playback runtime dependency.
See [NSIS license and bundled component notices](https://nsis.sourceforge.io/License)
and [NSIS 3.11 sources](https://sourceforge.net/projects/nsis/files/NSIS%203/3.11/).

The supplied `STEVANplejer_Windows_Source.zip` is acknowledged as the user's
starter project. Its project/readme and subtitle/playback architecture informed
this implementation. The supplied readme already disclosed unverified Windows
execution, missing AI transcription and missing approved APIs for several
subtitle sites; those caveats were not treated as tested functionality.

OpenSubtitles, SubDL, Titlovi.com and the Prijevodi websites are external services,
not bundled software or affiliations. Accounts, API keys, download quotas,
provider conditions and the licenses of individual subtitles still apply.
FFmpeg is used only to create a synthetic CI test fixture and is not shipped.
