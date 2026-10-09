#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$MakeNsis,
    [switch]$SkipSegmentation,
    [ValidateSet('Auto', 'win-x64', 'win-x86')][string]$RuntimeIdentifier = 'Auto',
    [switch]$TestBothArchitectures
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$desktopRoot = Split-Path $PSScriptRoot -Parent
$reports = Join-Path $desktopRoot 'artifacts/test-results'
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$selectedRuntime = if ($RuntimeIdentifier -eq 'Auto') {
    if ($osArchitecture -eq 'X64') { 'win-x64' } elseif ($osArchitecture -eq 'X86') { 'win-x86' } else { 'unsupported' }
} else { $RuntimeIdentifier }
$runLabel = if ($TestBothArchitectures) { 'both-architectures' } else { $selectedRuntime }
$summaryPath = Join-Path $reports "local-windows-validation-$runLabel.json"
# Invalidate only our previous summary; an aborted rerun must not retain a pass.
if (Test-Path -LiteralPath $summaryPath) { Remove-Item -LiteralPath $summaryPath -Force }
$summary = [ordered]@{
    Status = 'RUNNING'
    Scope = 'local_windows_build_and_automated_checks_only'
    StartedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    WindowsVersion = [Environment]::OSVersion.VersionString
    OperatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    ProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    OSArchitecture = $osArchitecture
    RequestedRuntimeIdentifier = $RuntimeIdentifier
    TestBothArchitectures = [bool]$TestBothArchitectures
    Architectures = [ordered]@{}
    Checks = [ordered]@{}
    NotTested = @('human-visible captions and real speakers', 'live authenticated subtitle sources',
        'cloud ASR/translation and film lip-sync', 'actual installer/uninstaller on a fresh test account',
        'Windows versions/DPI not used for this run', 'final distribution/signing requirements')
    ReleaseStatus = 'BLOCKED_PENDING_REMAINING_ACCEPTANCE'
}
$transcriptStarted = $false
try {
    # Preflight failures must also replace a previous pass with fresh FAILED evidence.
    if (-not $IsWindows -or $osArchitecture -notin @('X64', 'X86')) {
        throw 'Use PowerShell 7 on actual Windows 10 x86/x64 or Windows 11 x64. Linux/Wine/ARM is not accepted.'
    }
    if ([Environment]::OSVersion.Version.Major -lt 10) { throw 'Windows 10 or newer is required; older Windows versions are outside this validation scope.' }
    if ($TestBothArchitectures -and ($osArchitecture -ne 'X64' -or $RuntimeIdentifier -ne 'Auto')) {
        throw '-TestBothArchitectures requires x64 Windows and the default -RuntimeIdentifier Auto. It tests x86 under WOW64, not on a 32-bit operating system.'
    }
    if ($selectedRuntime -eq 'win-x64' -and $osArchitecture -ne 'X64') { throw 'A 32-bit Windows installation cannot execute or validate the x64 application.' }
    $runtimes = if ($TestBothArchitectures) { @('win-x64', 'win-x86') } else { @($selectedRuntime) }
    # Invalidate only reports owned by the selected architectures before the
    # build: an early tool/build failure cannot leave a prior pass as new proof.
    foreach ($runtime in $runtimes) {
        foreach ($stem in @('windows-native-decode', 'windows-wpf-controls', 'windows-native-segmentation')) {
            $previousReport = Join-Path $reports "$stem-$runtime.json"
            foreach ($previousEvidence in @($previousReport, ($previousReport + '.window.png'), ($previousReport + '.fullscreen.png'))) {
                if (Test-Path -LiteralPath $previousEvidence) { Remove-Item -LiteralPath $previousEvidence -Force }
            }
        }
    }
    if (-not [Environment]::UserInteractive) { throw 'A signed-in interactive Windows desktop is required for the WPF tests.' }
    foreach ($tool in @('dotnet', 'node', 'ffmpeg')) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            throw "Missing build/test tool: $tool. Follow desktop/TEST-ON-YOUR-PC.md; do not disable verification or execution security."
        }
    }
    if (-not $MakeNsis) {
        $nsisCandidates = @(
            if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'NSIS/makensis.exe' }
            if ($env:ProgramFiles) { Join-Path $env:ProgramFiles 'NSIS/makensis.exe' }
        )
        $MakeNsis = $nsisCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
    if (-not $MakeNsis -or -not (Test-Path -LiteralPath $MakeNsis -PathType Leaf)) { throw 'NSIS 3.11 is required. Set -MakeNsis to its makensis.exe.' }
    Start-Transcript -Path (Join-Path $reports "local-windows-validation-$runLabel.log") -Force | Out-Null
    $transcriptStarted = $true
    if ($TestBothArchitectures) {
        & (Join-Path $PSScriptRoot 'build.ps1') -BuildUniversalInstaller -MakeNsis $MakeNsis
    } else {
        & (Join-Path $PSScriptRoot 'build.ps1') -RuntimeIdentifier $selectedRuntime -BuildInstaller -MakeNsis $MakeNsis
    }
    $summary.Checks.LockedBuildCoreTestsAndInstallerCompilation = $true
    $fixture = Join-Path $desktopRoot 'artifacts/test-fixture'
    & (Join-Path $PSScriptRoot 'create-test-fixture.ps1')
    $media = Join-Path $fixture 'Stevan.Validation.2026.mp4'
    $subtitle = Join-Path $fixture 'Stevan.Validation.2026.sr.srt'
    if (-not $SkipSegmentation) {
        & (Join-Path $PSScriptRoot 'create-test-fixture.ps1') -DurationSeconds 610 -FrameRate 1 -Stem 'Stevan.Segmentation.2026'
    } else {
        $summary.NotTested += 'audio segmentation boundary (explicitly skipped)'
    }
    foreach ($runtime in $runtimes) {
        $architectureResult = [ordered]@{
            Status = 'RUNNING'
            ExpectedProcessArchitecture = if ($runtime -eq 'win-x64') { 'X64' } else { 'X86' }
            ExecutionEnvironment = if ($runtime -eq 'win-x86' -and $osArchitecture -eq 'X64') { 'WOW64 on x64 Windows; not a 32-bit Windows operating-system test' } else { 'native operating-system architecture' }
            Checks = [ordered]@{}
        }
        $summary.Architectures[$runtime] = $architectureResult
        & (Join-Path $PSScriptRoot 'test-windows.ps1') -RuntimeIdentifier $runtime -MediaPath $media -SubtitlePath $subtitle
        $architectureResult.Checks.NativePlaybackAndAudioExtraction = $true
        & (Join-Path $PSScriptRoot 'test-windows.ps1') -RuntimeIdentifier $runtime -Mode Ui -MediaPath $media -SubtitlePath $subtitle
        $architectureResult.Checks.WpfControlsFullscreenAndDpapi = $true
        if (-not $SkipSegmentation) {
            & (Join-Path $PSScriptRoot 'test-windows.ps1') -RuntimeIdentifier $runtime -MediaPath (Join-Path $fixture 'Stevan.Segmentation.2026.mp4') -SubtitlePath (Join-Path $fixture 'Stevan.Segmentation.2026.sr.srt') -RequireSegmentationBoundary
            $architectureResult.Checks.AudioSegmentationBoundary = $true
        }
        $shortArchitecture = $runtime.Substring(4)
        $architectureResult.ApplicationSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $desktopRoot "artifacts/$runtime/STEVANplejer.exe")).Hash
        $architectureResult.InstallerSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $desktopRoot "artifacts/STEVANplejer-Windows-$shortArchitecture-candidate-setup.exe")).Hash
        $architectureResult.Status = 'AUTOMATED_CHECKS_PASSED_NOT_A_RELEASE'
    }
    if ($TestBothArchitectures) {
        $summary.UniversalInstallerSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $desktopRoot 'artifacts/STEVANplejer-Windows-universal-candidate-setup.exe')).Hash
        $summary.NotTested += '32-bit Windows 10 operating system (x86 process on x64 Windows is not equivalent)'
    } else {
        $summary.NotTested += 'other application architectures not selected for this run'
    }
    $summary.Status = 'AUTOMATED_CHECKS_PASSED_NOT_A_RELEASE'
    Write-Host 'Automated local checks passed. This does NOT certify the release; follow TEST-ON-YOUR-PC.md for remaining checks.'
}
catch {
    $summary.Status = 'FAILED'
    foreach ($architectureResult in $summary.Architectures.Values) {
        if ($architectureResult.Status -eq 'RUNNING') { $architectureResult.Status = 'FAILED' }
    }
    $summary.Error = 'A build or validation check failed; inspect the local transcript and individual JSON reports.'
    throw
}
finally {
    $summary.CompletedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
    Write-Host "Local evidence: $reports. Review for personal file paths before sharing; never include settings or API keys."
}
