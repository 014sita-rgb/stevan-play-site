# СТЕВАНplejer за Windows

Изворни код самосталног плејера за Windows 10 x86/x64 и Windows 11 x64,
заснованог на .NET/WPF и libVLC. Један универзални инсталер бира одговарајући
runtime: кориснику нису потребни Python, PowerShell, .NET SDK нити засебан VLC.
PowerShell се користи само за развојне/тест скрипте. Постојећи Android сајт је одвојен од
овог пројекта. Развој је започет уз корисников приложени
`STEVANplejer_Windows_Source.zip`.

**Статус: завршно издање није потврђено.** Linux компајлирање и тестови логике
нису доказ Windows репродукције. `artifacts/*candidate*` су интерни резултати
компајлирања за проверу, без јавне објаве или представљања као готов производ.
Услови за завршетак и стварни резултати воде се у [VALIDATION.md](VALIDATION.md).

## Архитектуре и Windows верзије

| Рачунар / ОС | Пакет који универзални инсталер бира |
| --- | --- |
| Windows 10 64-bit, Intel/AMD | `win-x64` |
| Windows 10 32-bit, Intel/AMD | `win-x86` |
| Windows 11 64-bit, Intel/AMD | `win-x64` |
| Windows 11 32-bit | Не постоји такво издање Windows-а |
| ARM64 Windows | Није покривен овим пакетом; инсталер га изричито одбија |

Ово су циљеви кода и паковања, не потврђени резултати репродукције.
За обичан Windows 10 Home/Pro циљ прихватних тестова је ажуриран 22H2;
његова редовна Microsoft подршка је завршена. .NET 10 званична OS матрица
посебно наводи LTSC/Enterprise издања у животном циклусу. Нема гаранције за
сваку стару Windows 10 верзију. x86 процес тестиран на Windows 11 x64
није доказ рада на стварном Windows 10 32-bit ОС-у.

## Шта код омогућава

- Сопствени интерфејс и иконицу, отварање локалног видеа и директних HTTP(S)
  видео/stream адреса, превлачење датотека и листу репродукције.
- Репродукцију, паузу, заустављање, кретање по филму, јачину/утишавање звука,
  пун екран, избор аудио/титл записа и ручни временски помак титла.
- Претрагу/преузимање српских, хрватских и босанских титлова преко
  OpenSubtitles, SubDL и Titlovi.com провајдера, уз потребан налог/кључ и
  услове сервиса. Препознавање назива, године, серије/епизоде и hash-а филма;
  провера локалног титла пре интернет претраге и чување преузетих титлова
  у корисниковом cache-у. Мрежни одговори су тестирани синтетичким примерима;
  успешан live download и тачност поклапања нису потврђени без налога и филма.
- Када титл није нађен: локално издвајање звука, OpenAI препознавање говора
  и превођење на изабрани језик. Иста обрада може проценити временски помак
  и промену брзине постојећег титла. Обрада тражи API кључ, изричиту
  сагласност за слање звука/текста и може направити трошак. Квалитет превода
  и аутоматске синхронизације мора се проверити на стварном филму.
- Windows DPAPI заштиту API кључева и лозинке везану за Windows корисника.

Prijevodi-Titlovi.org тренутно има везу за ручно отварање у прегледачу и
додавање преузетог титла: download путање су искључене у robots правилима,
а сајт приказује преузимање само за чланове. Корисник је потврдио адресу
[Prijevodi Online](https://www.prijevodi-online.org/), која је сада ручна веза
у плејеру. Cloud proxy блокира проверу тог сајта пре приступа серверу;
његова правила и дозвољени API нису проверени. То није доказ забране сајта.
Потпуна аутоматизација оба извора остаје неиспуњен захтев.
Нема заобилажења CAPTCHA, пријаве, DRM-а
или ограничења сервиса. HTML адреса странице није директна видео адреса.
Подржани су SRT/VTT/ASS и ZIP архиве; RAR-only титлови тренутно пријављују
неподржан формат уместо лажног успеха.

## Компајлирање и инсталација за проверу

Развој тражи Windows x64, PowerShell 7, SDK верзију из `global.json`, Node.js
22 или новији (само за припрему package-а), и NSIS 3.11 за инсталатер.
Не мењајте lockfile ради успешног restore-а. Из корена репозиторијума:

```powershell
./desktop/scripts/build.ps1 -BuildUniversalInstaller -MakeNsis 'C:\Program Files (x86)\NSIS\makensis.exe'
```

Скрипта прво покреће тестове, затим self-contained publish за `win-x64` и
`win-x86`, копира лиценце и генерише посебне манифесте власништва
инсталатера. Не прави single-file пакет јер су native VLC библиотеке,
plugin-и и лиценце неопходни поред `.exe` датотеке. Резултати су
`desktop/artifacts/win-x64/`, `desktop/artifacts/win-x86/`, засебни интерни
`*x64-candidate.zip` / `*x86-candidate.zip` и инсталери, те један
`STEVANplejer-Windows-universal-candidate-setup.exe` са оба payload-а.
За једну архитектуру: `build.ps1 -RuntimeIdentifier win-x86 -BuildInstaller`
(подразумевана архитектура build скрипте је x64).
Једна издвојена `.exe` датотека није цео плејер.

NSIS инсталира за тренутног корисника у
`%LOCALAPPDATA%\Programs\StevanPlayer`, без администратора, и додаје Start
menu пречицу. Desktop пречица се бира током инсталације. Деинсталација
брише само познате инсталиране датотеке и пречице; подешавања, заштићени
кључеви и титлови у `%LOCALAPPDATA%\StevanPlayer` остају сачувани. Пакет још
није Authenticode потписан; SmartScreen упозорење на непознатог издавача
може се појавити. Потписивање за јавну дистрибуцију тражи сертификат издавача.

Cloud окружење користи постојећи checkout: сваки задатак је већ изолован,
па додатни Git worktree није потребан без изричитог захтева. Linux може
покренути core тестове и cross-publish, али не замењује Windows тестове.
`scripts/setup-cloud.sh` припрема проверене pinned алате за тај workflow.

## Стварни Windows тестови

За проверу на свом рачунару користите [TEST-ON-YOUR-PC.md](TEST-ON-YOUR-PC.md)
и `scripts/test-local-windows.ps1` из PowerShell-а 7. Та скрипта не шаље код
и не покреће аутоматску деинсталацију на вашем корисничком налогу.
На твом Windows 11 x64 може се проверити и x86 процес:
`scripts/test-local-windows.ps1 -TestBothArchitectures`. Извештаји проверавају
стварни `ProcessArchitecture`, не прихватају x64 резултат као x86 PASS.
Коначна подршка за Windows 10 x86 захтева додатно тестирање на том ОС-у.

FFmpeg се користи само за прављење синтетичког тест филма, не у инсталацији.

```powershell
./desktop/scripts/create-test-fixture.ps1
./desktop/scripts/test-windows.ps1 `
  -MediaPath desktop/artifacts/test-fixture/Stevan.Validation.2026.mp4 `
  -SubtitlePath desktop/artifacts/test-fixture/Stevan.Validation.2026.sr.srt
./desktop/scripts/test-windows.ps1 -Mode Ui `
  -MediaPath desktop/artifacts/test-fixture/Stevan.Validation.2026.mp4 `
  -SubtitlePath desktop/artifacts/test-fixture/Stevan.Validation.2026.sr.srt `
  -ReportPath desktop/artifacts/test-results/windows-wpf-controls.json
./desktop/scripts/create-test-fixture.ps1 -DurationSeconds 610 -FrameRate 1 -Stem Stevan.Segmentation.2026
./desktop/scripts/test-windows.ps1 `
  -MediaPath desktop/artifacts/test-fixture/Stevan.Segmentation.2026.mp4 `
  -SubtitlePath desktop/artifacts/test-fixture/Stevan.Segmentation.2026.sr.srt `
  -RequireSegmentationBoundary `
  -ReportPath desktop/artifacts/test-results/windows-native-segmentation.json
```

Плејер прихвата `--smoke-test MEDIA SUBTITLE REPORT.json`. Тест стварно
декодира видео и звук native VLC-ом, проверава додат и одабран спољни титл,
помак, паузу, seek и наставак декодирања. Такође издваја звук и проверава
стварни непразан/non-silent mono PCM WAV 16 kHz/16 bit и трајање. Дужи
fixture проверава границу сегмената од 600 секунди. JSON именује појединачне
assertion-е и оно што није тестирано у native тесту: WPF приказ, стварне звучнике, fullscreen,
исцртан текст титла, липсинк и live услуге. Не своди та ограничења на PASS.

Посебан `--ui-smoke-test` режим стварно отвара WPF прозор, репродукује
fixture у native видео-host-у, проверава локални ауто-титл, контроле,
fullscreen и Escape, промену видеа и отказивање операције. Чува JSON и
снимке стварних desktop пиксела у нормалном и fullscreen стању. Потребна
је Windows радна површина; headless/session окружење које не може да
потврди приказ мора пасти на том тесту. Човек и даље проверава изглед,
исцртан текст, стварне звучнике и синхронизацију — снимање слике само по
себи не доказује да је титл читљив или филм правилно усклађен.

GitHub испорука у засебној грани садржи само изворе и изворни ZIP у
`downloads/`. Workflow није укључен и GitHub тестови нису покренути.
Windows провере обављају се локалним скриптама на корисниковом рачунару.
Није објављен release нити је пакет представљен као готов производ.

У плејеру отворите Подешавања за своје API кључеве/налог и језик. Не
постављајте кључеве у Git, разговор, снимке екрана или тест извештаје.
Titlovi.com налог мора имати потврђену e-mail адресу и активиран API приступ
на [страници за донације/API](https://titlovi.com/donacije/), према
[званичним упутствима за API/Kodi](https://titlovi.com/kodi-plugin/).
Погледајте [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) пре дистрибуције.
