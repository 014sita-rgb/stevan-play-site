# Release validation record

**Release status: BLOCKED. Do not publish or label the application finished.**
This record separates checks executed in the current Linux cloud machine from
checks that require an actual Windows machine, working provider credentials,
licensed media, or a distribution decision. A compiled candidate is internal
build evidence, not a trial version or an approved release.

## Evidence from this implementation session

- The user-supplied 11.6 KB ZIP was inspected as source, including its readme
  and existing Windows workflow. It contained a .NET 8 WinForms starter, not
  an already-tested Windows executable.
- Repository development now lives in `desktop/`; existing Android files and
  website are not the desktop application source.
- Linux core test execution passed **123 tests, 0 failed, 0 skipped**. Locked
  restore and the reusable cloud setup script also succeeded. These are logic
  tests against local fixtures/mock provider responses, not live provider calls.
- The Windows-target WPF application compiled and cross-published for `win-x64`
  with **0 compiler warnings and 0 errors**. The application output is an actual
  PE32+ Windows x64 executable and includes the .NET/WPF/native libVLC runtimes.
  Cross-compilation does not demonstrate execution on Windows.
- PowerShell 7.5.4 parsed all five build/validation `.ps1` scripts with 0
  syntax errors; the new local runner actually rejected Linux execution.
  The fixture-generation PowerShell script was executed on Linux and FFprobe
  confirmed a 20.000 second MPEG-4 640x360 video + AAC 48 kHz audio file.
- The complete PowerShell build (`scripts/build.ps1 -BuildInstaller`) actually
  ran on Linux: locked restores, 123 passing tests, self-contained publish,
  license/ownership manifest, ZIP and NSIS installer completed with exit 0.
  Initial registry-string quoting warnings were corrected; the final NSIS
  compilation emitted no warnings. Evidence is in `artifacts/cross-build.log`
  and `artifacts/test-results/core-tests.trx`. This is not an installation test.
- License texts were retrieved from the official VideoLAN source tag over
  verified HTTPS; provenance/hash is in `licenses/SOURCES.md`.
- Windows automation has been authored as local test scripts but has not run
  on an actual Windows machine. GitHub Actions is excluded from the source
  delivery; no hosted runner or release is authorized by this delivery.
- The user authorized GitHub delivery of the source in a separate branch and
  chose their own Windows PC for actual tests. `TEST-ON-YOUR-PC.md` and
  `scripts/test-local-windows.ps1` are the local evidence handoff; the runner
  excludes unattended installer mutations on the user's personal account.
- No real provider search/download, cloud transcription, paid translation,
  film-specific automatic synchronization, or UI playback pass is recorded.
- Read-only public API probes reached OpenSubtitles' language catalogue (HTTP
  200 with Serbian/Croatian/Bosnian) and SubDL (HTTP 403 `not_authorized` without
  a key). Official OpenSubtitles/SubDL documentation was retrieved. This proves
  reachability, not an authenticated subtitle search or download. Titlovi's
  official Kodi/API page was retrieved: a confirmed account e-mail and enabled
  API access at https://titlovi.com/donacije/ are prerequisites. Protocol fields
  are additionally corroborated by public client source, not a live credential test.
- Read-only Prijevodi-Titlovi.org inspection reached its homepage, robots,
  terms, public search and a movie detail page. The page displayed downloads
  as members-only; robots excludes `/subtitles/*/download`, download-review,
  `/prevod/` and `/search`. No permitted automated download API was confirmed.
  These observed restrictions justify manual fallback, not a general claim
  that all public indexing is prohibited.
- The user confirmed Prijevodi Online as https://www.prijevodi-online.org/.
  Homepage and robots probes failed at the cloud proxy with CONNECT 403,
  including approved retries; the origin was not reached. Its terms, robots,
  API, authentication and subtitle availability remain unverified. Both hostnames
  were added to the saved environment draft; saving does not apply runtime access.
  The app now offers the confirmed homepage as a manual browser link.

## Expanded architecture scope (verification pending)

The new request adds Windows 10 x86 alongside Windows 10/11 Intel/AMD x64.
There is no 32-bit Windows 11 edition. One universal installer contains two
self-contained payloads, selects native AMD64/x86, and rejects ARM64.
PowerShell is a development/testing tool, not an installed player dependency.
Each native/UI report must prove its actual ProcessArchitecture (X86/X64).
An x86 pass on x64 Windows (WOW64) is not proof of a 32-bit operating system.
The consumer Windows 10 acceptance target is updated 22H2; ordinary Home/Pro
regular support has ended. Official .NET10 supported OS matrices separately
list in-lifecycle LTSC/Enterprise releases. Do not claim every Windows10 build
or current Microsoft support for ordinary consumer editions.

The complete `build.ps1 -BuildUniversalInstaller` now exited 0 on Linux:
locked restores, 123 passing core tests, win-x64 and win-x86 self-contained
publishes, both architecture installers and the universal NSIS installer.
There were no compiler/NSIS warnings; the TRX overwrite notice is informational.
Before packaging, PE headers verified the native apphost, CLR, WPF, libVLC and
libvlccore match AMD64 for x64 and I386 for x86. Evidence is in
`artifacts/universal-build.log`, per-RID payload/hash manifests and core TRX.
All five PowerShell scripts parse without errors; the both-architecture local
runner actually rejects Linux with fresh FAILED evidence rather than a stale pass.
Actual Windows10x64, Windows10x86 and Windows11x64 playback/installation/live
subtitle/ASR acceptance remains NOT RUN.

Source delivery is authorized through the separate GitHub branch
`stevanplejer-windows-source-20261009` in `014sita-rgb/stevan-play-site`.
The ZIP at `downloads/STEVANplejer_Windows_Universal_Source.zip` contains
source, documentation and local test scripts, excluding compiled binaries,
local settings, build caches and hosted workflows. Verification must retrieve
the public download and compare its SHA256 with the prepared archive.
Source availability does not change the blocked application release status.

## Required automated checks

| Check | Passing evidence | Current status |
| --- | --- | --- |
| Locked restore, core tests | Successful process exit and fresh test reports; malformed/foreign URL, archive, sync and provider failure cases included | PASS on Linux: 123 passed, 0 failed/skipped |
| Self-contained Windows x64 compilation | Published `.exe`, WPF/.NET and native VLC runtime/plugin folders, no compiler warnings | PASS for cross-compilation: 0 warnings/errors; execution untested |
| Self-contained Windows x86 compilation | PE32/I386 apphost/CLR/WPF/VLC, locked dependencies, correct x86 license inventory | PASS for cross-compilation; execution on Windows10x86 untested |
| NSIS compilation | Per-user installer generated using exact owned-file manifest | PASS on Linux: installer built, no warnings; installation untested |
| Universal NSIS compilation | One installer contains exact x64/x86 manifests and chooses native CPU, rejects ARM64 | PASS on Linux; actual architecture selection/installation untested |
| Native Windows decode | Fresh JSON `Scope=native_windows_decode_and_audio_extraction`, `Status=PASS`, all checks true, real frame and audio sample counters | NOT RUN on Windows |
| WPF controls/fullscreen | Fresh JSON `Scope=windows_wpf_controls`, all checks true, actual window/fullscreen desktop screenshots | NOT RUN on Windows |
| Audio extraction segmentation | Long fixture (>600 seconds), valid non-silent PCM and accurate durations, at least two segments; `AudioSegmentationBoundaryExercised=true` | NOT RUN on Windows |
| Installed app and uninstall | Isolated Windows account install, Start menu entry, installed native decode, removal of owned files/registry, preservation of settings and unknown file | NOT RUN on Windows |

`scripts/test-windows.ps1` rejects a stale/missing report or non-Windows host.
Native track selection is not proof the subtitle text was visibly rendered.
Automated video callbacks bypass the WPF surface and real speakers. The report
lists these exclusions, and successful automation does not waive the checks
below. Wine cannot certify Windows playback here.

## Required actual Windows and live service acceptance

Record Windows version/architecture, app hash, time, fixture identifiers,
observed results and fresh logs without secret values. A failure remains a
blocker until corrected and retested.

- Windows 10 x64, Windows 10 x86 and Windows 11 x64 separately: launch the
  universal installer in a user account without Python, PowerShell, .NET SDK or
  separate VLC; verify its selected architecture and choose/cancel an optional desktop shortcut;
  upgrade behavior and scoped uninstall. Verify normal SmartScreen/signature
  expectations on the intended distribution channel.
- WPF video visibly plays and actual audio is audible; exercise pause/stop,
  seeking both directions, resume, volume/mute, track selection, playlist next,
  drag/drop, minimization and fullscreen enter/exit (including Escape) on real
  hardware. Check scaling and video overlays at 100% and 150% DPI.
- Serbian Cyrillic/Latin, Croatian and Bosnian SRT appears visibly and changes
  at the right time. Confirm embedded subtitles and manual offset; select a
  subtitle of a different release and ensure the user is informed rather than
  treating a search ranking as a guaranteed film match.
- OpenSubtitles, SubDL and Titlovi.com: for each usable credential, search a
  known legally available foreign film/episode with available sr/hr/bs subtitle;
  observe automatic selection/download and actual rendering. Test wrong/missing
  keys, expired login, quota/429, offline state, cancel/open another film during
  a search, season/episode mismatch, archives and wrong language. Provider
  fixture tests check protocol handling, not live account/API compatibility.
- Prijevodi-Titlovi.org and Prijevodi Online: obtain a confirmed permitted API
  or operator-approved automated access, implement it and run the same live
  checks before claiming all five requested sources are automatic. Both now
  have confirmed manual homepage links; no automated integration is verified.
  First resolve cloud reachability for Online and inspect its access rules.
  The five-source requirement is not yet met.
- AI fallback: with explicitly consented licensed audio and an active OpenAI
  account, confirm the selected model is available, requests succeed within
  quota, no results replace another film after cancellation, segment boundaries
  are preserved and sr/hr/bs output is accurate enough. Confirm disabled consent
  causes no upload; identify actual cost to the user. Cloud privacy/cost consent
  is required in the app; credentials are never requested in chat.
- Automatic alignment: actual film/subtitle with a known shift and tempo drift;
  compare beginning/middle/end against spoken dialogue and visually verify
  lip-sync after correction. Unit tests on synthesized cue timing do not prove
  speech recognition/translation quality on film audio.
- DPAPI settings: save/reopen credentials under the same Windows account,
  inspect settings for plaintext secret absence, cancel settings without saving,
  and handle an unreadable/copied credential blob without crashing or leaking it.
- Distribution: archive exact corresponding libVLC, wrapper and bundled plugin/
  contrib sources/notices as required by their licenses, decide the application's
  release license and obtain any desired Authenticode signing certificate. No
  public distribution is authorized by a passing build alone.

## Known scope limits

Direct HTTP(S) media streams are supported; arbitrary web player HTML, DRM,
subscription circumvention and guaranteed subtitles for every title are not.
Subtitle payloads support validated SRT/VTT/ASS and bounded ZIP archives;
RAR-only provider downloads are not decoded and report an explicit error.
Provider outages, wrong release translations, recognition mistakes and different
edits of a film can defeat automation. AI operates on local media; remote URL
streams do not automatically become uploadable local film files.

The immediate external prerequisites are an actual Windows test machine/runner,
usable provider accounts/API keys with permitted access, a known licensed sample
film/episode, and explicit in-app permission for any paid/cloud audio processing.
The new project can be inspected and rebuilt before those prerequisites arrive.
