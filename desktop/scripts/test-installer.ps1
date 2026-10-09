#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$InstallerPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/STEVANplejer-Windows-universal-candidate-setup.exe'),
    [ValidateSet('Auto', 'win-x64', 'win-x86')][string]$RuntimeIdentifier = 'Auto',
    [Parameter(Mandatory)][string]$MediaPath,
    [Parameter(Mandatory)][string]$SubtitlePath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or -not $env:CI) { throw 'Run the unattended installer check only in an isolated Windows CI account.' }
if ([Environment]::OSVersion.Version.Major -lt 10) { throw 'Windows 10 or newer is required; older Windows versions are outside this validation scope.' }
$osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
if ($osArchitecture -notin @('X64', 'X86')) { throw "This installer validation supports Windows x64/x86, not $osArchitecture." }
if ($RuntimeIdentifier -eq 'Auto') { $RuntimeIdentifier = if ($osArchitecture -eq 'X64') { 'win-x64' } else { 'win-x86' } }
if ($RuntimeIdentifier -eq 'win-x64' -and $osArchitecture -ne 'X64') { throw 'A 32-bit Windows installation cannot execute the x64 application.' }
$desktopRoot = Split-Path $PSScriptRoot -Parent
$reports = Join-Path $desktopRoot 'artifacts/test-results'
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$reportPath = Join-Path $reports "windows-installer-$RuntimeIdentifier.json"
if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath -Force }
$installedNativeReportPath = Join-Path $reports "windows-installed-native-decode-$RuntimeIdentifier.json"
if (Test-Path -LiteralPath $installedNativeReportPath) { Remove-Item -LiteralPath $installedNativeReportPath -Force }
$report = [ordered]@{
    Status = 'RUNNING'
    Scope = 'windows_install_native_decode_scoped_uninstall'
    StartedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    OSArchitecture = $osArchitecture
    ExpectedProcessArchitecture = if ($RuntimeIdentifier -eq 'win-x64') { 'X64' } else { 'X86' }
    RuntimeIdentifier = $RuntimeIdentifier
    Checks = [ordered]@{}
    NotTested = @('human-visible installer UI', 'live subtitle sources', 'cloud ASR/translation', 'Windows versions not used for this run')
}
$applicationRoot = Join-Path $env:LOCALAPPDATA 'Programs/StevanPlayer'
$settingsRoot = Join-Path $env:LOCALAPPDATA 'StevanPlayer'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StevanPlayer'
$settingsSentinel = Join-Path $settingsRoot 'validation-preserve.txt'
$unknownInstalledFile = Join-Path $applicationRoot 'validation-user-file.txt'
$ownsFixtureFiles = $false
try {
    if ((Test-Path -LiteralPath $applicationRoot) -or (Test-Path -LiteralPath $settingsRoot) -or (Test-Path -LiteralPath $uninstallKey)) {
        throw 'An existing installation/settings/key was found. Do not run installer automation against user data.'
    }
    if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) { throw "Missing installer: $InstallerPath" }
    $report.InstallerSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $InstallerPath).Hash
    New-Item -ItemType Directory -Path $settingsRoot -Force | Out-Null
    $ownsFixtureFiles = $true
    Set-Content -LiteralPath $settingsSentinel -Value 'Installer must preserve this independent settings directory.' -Encoding utf8
    $install = Start-Process -FilePath ([IO.Path]::GetFullPath($InstallerPath)) -ArgumentList '/S' -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Silent installation failed: $($install.ExitCode)" }
    if (-not (Test-Path -LiteralPath $uninstallKey)) { throw 'Per-user uninstall registration is absent.' }
    $startMenu = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs/СТЕВАНplejer/СТЕВАНplejer.lnk'
    if (-not (Test-Path -LiteralPath $startMenu)) { throw 'Start menu shortcut is absent.' }
    $report.Checks.InstallationRegistrationAndShortcut = $true
    & (Join-Path $PSScriptRoot 'test-windows.ps1') -RuntimeIdentifier $RuntimeIdentifier -PublishDirectory $applicationRoot -MediaPath $MediaPath -SubtitlePath $SubtitlePath -ReportPath $installedNativeReportPath
    $nativeReport = Get-Content -LiteralPath $installedNativeReportPath -Raw | ConvertFrom-Json
    $report.ProcessArchitecture = $nativeReport.ProcessArchitecture
    $report.Checks.ExpectedArchitectureAndInstalledNativeDecode = $true
    Set-Content -LiteralPath $unknownInstalledFile -Value 'An unowned file must survive uninstall.' -Encoding utf8
    # Use NSIS' normal temporary uninstaller copy: an in-place _?= invocation
    # locks Uninstall.exe and prevents checking actual self-removal. On Windows,
    # Start-Process -Wait waits the launched process and its descendants.
    $uninstall = Start-Process -FilePath (Join-Path $applicationRoot 'Uninstall.exe') -ArgumentList '/S' -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw "Silent uninstallation failed: $($uninstall.ExitCode)" }
    if (Test-Path -LiteralPath (Join-Path $applicationRoot 'STEVANplejer.exe')) { throw 'Application executable survived uninstall.' }
    if (Test-Path -LiteralPath (Join-Path $applicationRoot 'Uninstall.exe')) { throw 'Uninstaller executable survived normal uninstall.' }
    if ((Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath $startMenu)) { throw 'Registration or shortcut survived uninstall.' }
    if (-not (Test-Path -LiteralPath $settingsSentinel)) { throw 'Uninstaller deleted the user settings directory.' }
    if (-not (Test-Path -LiteralPath $unknownInstalledFile)) { throw 'Uninstaller deleted an unowned file.' }
    $report.Checks.ScopedUninstallAndSettingsPreservation = $true
    $report.Status = 'PASS'
    Write-Host "PASS: installation, installed $RuntimeIdentifier native decode, scoped uninstall, and settings preservation."
}
catch {
    $report.Status = 'FAILED'
    $report.Error = 'Installer validation failed. Inspect the current console and native decode report; no release approval is recorded.'
    throw
}
finally {
    # Only remove fixture files created by this check, never a recursively targeted user directory.
    if ($ownsFixtureFiles) {
        if (Test-Path -LiteralPath $settingsSentinel) { Remove-Item -LiteralPath $settingsSentinel -Force }
        if (Test-Path -LiteralPath $unknownInstalledFile) { Remove-Item -LiteralPath $unknownInstalledFile -Force }
        foreach ($ownedDirectory in @($settingsRoot, $applicationRoot)) {
            if (Test-Path -LiteralPath $ownedDirectory) {
                try { [IO.Directory]::Delete($ownedDirectory, $false) }
                catch [IO.IOException] { } # Preserve all unexpected files if setup/uninstall failed.
            }
        }
    }
    $report.CompletedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
}
