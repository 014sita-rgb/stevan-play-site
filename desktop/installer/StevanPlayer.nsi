Unicode true
RequestExecutionLevel user
SetCompressor /SOLID zlib
ManifestDPIAware true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

!ifdef UNIVERSAL
  !ifndef PUBLISH_DIR_X64
    !define PUBLISH_DIR_X64 "${__FILEDIR__}/../artifacts/win-x64"
  !endif
  !ifndef PUBLISH_DIR_X86
    !define PUBLISH_DIR_X86 "${__FILEDIR__}/../artifacts/win-x86"
  !endif
  !ifndef PAYLOAD_MANIFEST_X64
    !define PAYLOAD_MANIFEST_X64 "${__FILEDIR__}/../artifacts/installer-payload-win-x64.nsh"
  !endif
  !ifndef PAYLOAD_MANIFEST_X86
    !define PAYLOAD_MANIFEST_X86 "${__FILEDIR__}/../artifacts/installer-payload-win-x86.nsh"
  !endif
  !ifndef OUTFILE
    !define OUTFILE "${__FILEDIR__}/../artifacts/STEVANplejer-Windows-universal-candidate-setup.exe"
  !endif
  !include "${PAYLOAD_MANIFEST_X64}"
  !include "${PAYLOAD_MANIFEST_X86}"
!else
  !ifndef RID
    !define RID "win-x64"
  !endif
  !if "${RID}" == "win-x64"
    !define PAYLOAD_SUFFIX "win_x64"
    !define ARCHITECTURE "x64"
  !else if "${RID}" == "win-x86"
    !define PAYLOAD_SUFFIX "win_x86"
    !define ARCHITECTURE "x86"
  !else
    !error "RID must be win-x64 or win-x86."
  !endif
  !ifndef PUBLISH_DIR
    !define PUBLISH_DIR "${__FILEDIR__}/../artifacts/${RID}"
  !endif
  !ifndef PAYLOAD_MANIFEST
    !define PAYLOAD_MANIFEST "${__FILEDIR__}/../artifacts/installer-payload-${RID}.nsh"
  !endif
  !ifndef OUTFILE
    !define OUTFILE "${__FILEDIR__}/../artifacts/STEVANplejer-Windows-${ARCHITECTURE}-candidate-setup.exe"
  !endif
  !include "${PAYLOAD_MANIFEST}"
!endif

Var SelectedRuntime
Var PrerequisitePowerShell
Var PrerequisiteResult

Name "СТЕВАНplejer"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\StevanPlayer"
ShowInstDetails show
ShowUninstDetails show
BrandingText "СТЕВАНplejer"
!define MUI_ICON "${__FILEDIR__}/../StevanPlayer.App/Assets/stevan.ico"
!define MUI_UNICON "${__FILEDIR__}/../StevanPlayer.App/Assets/stevan.ico"
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${__FILEDIR__}/../LICENSE"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "Serbian"
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  SetShellVarContext current
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "СТЕВАНplejer захтева Windows 10 или Windows 11."
    Abort
  ${EndIf}
  ; RunningX64 also matches ARM64. Detect the native machine instead of
  ; silently installing an untested emulated payload on ARM devices.
  ${If} ${IsNativeAMD64}
    StrCpy $SelectedRuntime "win-x64"
  ${ElseIf} ${IsNativeIA32}
    StrCpy $SelectedRuntime "win-x86"
  ${Else}
    MessageBox MB_ICONSTOP "Овај пакет подржава Intel/AMD x64 и x86 рачунаре. ARM64 није подржан."
    Abort
  ${EndIf}
  !ifndef UNIVERSAL
    !if "${RID}" == "win-x64"
      ${If} $SelectedRuntime != "win-x64"
        MessageBox MB_ICONSTOP "Овај пакет захтева Windows 10/11 x64. За Windows 10 32-bit употребите x86 или универзални пакет."
        Abort
      ${EndIf}
    !else
      ; A specific x86 installer can also run on a native x64 computer.
      StrCpy $SelectedRuntime "win-x86"
    !endif
  !endif
FunctionEnd

Function EnsureNativeRuntime
  ; Only x64 payloads run the local translator. x86 playback/subtitle selection
  ; does not invoke the unsupported x86 LLama backend.
  ${If} $SelectedRuntime != "win-x64"
    Return
  ${EndIf}
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File "/oname=ensure-vc-runtime.ps1" "${__FILEDIR__}/ensure-vc-runtime.ps1"
  ; NSIS is a 32-bit executable; Sysnative avoids WOW64 registry/DLL confusion.
  StrCpy $PrerequisitePowerShell "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
  IfFileExists "$PrerequisitePowerShell" prerequisite_powershell_found 0
  StrCpy $PrerequisitePowerShell "$WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
  IfFileExists "$PrerequisitePowerShell" prerequisite_powershell_found 0
  MessageBox MB_ICONSTOP "Није пронађен Windows PowerShell за проверу Microsoft Visual C++ библиотека. Инсталација није завршена."
  SetErrorLevel 1
  Abort
  prerequisite_powershell_found:
  ; Pass the installer-owned helper path as data. Execute only the bundled local
  ; source inline: no execution-policy changes or downloaded PowerShell scripts.
  System::Call 'kernel32::SetEnvironmentVariable(t "STEVAN_INSTALL_PREREQUISITE", t "$PLUGINSDIR\ensure-vc-runtime.ps1") i .r0'
  ${If} $0 == 0
    MessageBox MB_ICONSTOP "Неуспешна припрема провере Microsoft Visual C++ библиотека. Инсталација није завршена."
    SetErrorLevel 1
    Abort
  ${EndIf}
  DetailPrint "Провера Microsoft Visual C++ x64 библиотека за локалне титлове..."
  nsExec::ExecToLog '"$PrerequisitePowerShell" -NoProfile -NonInteractive -Command "& ([scriptblock]::Create([IO.File]::ReadAllText([Environment]::GetEnvironmentVariable($\'STEVAN_INSTALL_PREREQUISITE$\'))))"'
  Pop $PrerequisiteResult
  System::Call 'kernel32::SetEnvironmentVariable(t "STEVAN_INSTALL_PREREQUISITE", p 0) i .r0'
  ${If} $PrerequisiteResult == 3010
    SetRebootFlag true
    DetailPrint "Microsoft библиотеке су инсталиране; поново покрените Windows пре употребе плејера."
  ${ElseIf} $PrerequisiteResult != 0
    MessageBox MB_ICONSTOP "Microsoft Visual C++ библиотеке нису успешно проверене или инсталиране. Погледајте детаље инсталације. Инсталација СТЕВАНplejera није завршена."
    SetErrorLevel 1
    Abort
  ${EndIf}
FunctionEnd

Section "СТЕВАНplejer (обавезно)" MainSection
  SectionIn RO
  SetShellVarContext current
  ; Complete the prerequisite before writing application files or uninstall keys.
  Call EnsureNativeRuntime
  !ifdef UNIVERSAL
    ${If} $SelectedRuntime == "win-x64"
      !insertmacro InstallPayload_win_x64 "${PUBLISH_DIR_X64}"
    ${Else}
      !insertmacro InstallPayload_win_x86 "${PUBLISH_DIR_X86}"
    ${EndIf}
  !else
    !insertmacro InstallPayload_${PAYLOAD_SUFFIX} "${PUBLISH_DIR}"
  !endif
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\СТЕВАНplejer"
  CreateShortcut "$SMPROGRAMS\СТЕВАНplejer\СТЕВАНplejer.lnk" "$INSTDIR\STEVANplejer.exe"
  CreateShortcut "$SMPROGRAMS\СТЕВАНplejer\Уклони СТЕВАНplejer.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StevanPlayer" "DisplayName" "СТЕВАНplejer"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StevanPlayer" "DisplayVersion" "1.0.0"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StevanPlayer" "RuntimeIdentifier" "$SelectedRuntime"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StevanPlayer" "Publisher" "СТЕВАНplejer"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StevanPlayer" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StevanPlayer" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StevanPlayer" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StevanPlayer" "DisplayIcon" "$INSTDIR\STEVANplejer.exe,0"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StevanPlayer" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StevanPlayer" "NoRepair" 1
SectionEnd

Section /o "Пречица на радној површини" DesktopSection
  CreateShortcut "$DESKTOP\СТЕВАНplejer.lnk" "$INSTDIR\STEVANplejer.exe"
SectionEnd

Function un.onInit
  SetShellVarContext current
FunctionEnd

Section "Uninstall"
  !ifdef UNIVERSAL
    ; Remove the union of the two exact ownership manifests. Never recurse:
    ; unrelated files and the separate per-user settings remain untouched.
    !insertmacro UninstallPayload_win_x64
    !insertmacro UninstallPayload_win_x86
  !else
    !insertmacro UninstallPayload_${PAYLOAD_SUFFIX}
  !endif
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\СТЕВАНplejer\СТЕВАНplejer.lnk"
  Delete "$SMPROGRAMS\СТЕВАНplejer\Уклони СТЕВАНplejer.lnk"
  RMDir "$SMPROGRAMS\СТЕВАНplejer"
  Delete "$DESKTOP\СТЕВАНplejer.lnk"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StevanPlayer"
  ; Deliberately preserve $LOCALAPPDATA\StevanPlayer (credentials and subtitles).
SectionEnd
