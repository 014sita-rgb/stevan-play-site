#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$PublishDirectory,
    [ValidateSet('Auto', 'win-x64', 'win-x86')][string]$RuntimeIdentifier = 'Auto',
    [Parameter(Mandatory)][string]$MediaPath,
    [Parameter(Mandatory)][string]$SubtitlePath,
    [string]$ReportPath,
    [switch]$RequireSegmentationBoundary,
    [ValidateSet('Native', 'Ui')][string]$Mode = 'Native'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Actual Windows execution is required. Cross-compilation/Wine is not Windows validation.' }
if ([Environment]::OSVersion.Version.Major -lt 10) { throw 'Windows 10 or newer is required; older Windows versions are outside this validation scope.' }
if ($Mode -eq 'Ui' -and $RequireSegmentationBoundary) { throw 'Segmentation is a Native check, not a UI check.' }
$osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
if ($osArchitecture -notin @('X64', 'X86')) { throw "This validation supports actual Windows x64/x86, not $osArchitecture." }
if ($RuntimeIdentifier -eq 'Auto') { $RuntimeIdentifier = if ($osArchitecture -eq 'X64') { 'win-x64' } else { 'win-x86' } }
if ($RuntimeIdentifier -eq 'win-x64' -and $osArchitecture -ne 'X64') { throw 'A 32-bit Windows installation cannot execute the x64 application.' }
$expectedArchitecture = if ($RuntimeIdentifier -eq 'win-x64') { 'X64' } else { 'X86' }
$desktopRoot = Split-Path $PSScriptRoot -Parent
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $desktopRoot "artifacts/$RuntimeIdentifier" }
if (-not $ReportPath) {
    $reportStem = if ($Mode -eq 'Ui') { 'windows-wpf-controls' } elseif ($RequireSegmentationBoundary) { 'windows-native-segmentation' } else { 'windows-native-decode' }
    $ReportPath = Join-Path $desktopRoot "artifacts/test-results/$reportStem-$RuntimeIdentifier.json"
}
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
New-Item -ItemType Directory -Path (Split-Path $ReportPath -Parent) -Force | Out-Null
if (Test-Path -LiteralPath $ReportPath) { Remove-Item -LiteralPath $ReportPath -Force }
if ($Mode -eq 'Ui') {
    # The screenshots are generated beside this exact report. An aborted rerun
    # must not accidentally reuse pixels from a previous successful execution.
    foreach ($suffix in @('.window.png', '.fullscreen.png')) {
        $previousScreenshot = $ReportPath + $suffix
        if (Test-Path -LiteralPath $previousScreenshot) { Remove-Item -LiteralPath $previousScreenshot -Force }
    }
}
$application = Join-Path $PublishDirectory 'STEVANplejer.exe'
foreach ($required in @($application, $MediaPath, $SubtitlePath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing test input: $required" }
}
$application = (Resolve-Path -LiteralPath $application).Path
$MediaPath = (Resolve-Path -LiteralPath $MediaPath).Path
$SubtitlePath = (Resolve-Path -LiteralPath $SubtitlePath).Path
# Start-Process quotes paths explicitly: the executable is a GUI application, so
# synchronous '& exe' is not a reliable waiting mechanism in every PowerShell host.
$entry = if ($Mode -eq 'Ui') { '--ui-smoke-test' } else { '--smoke-test' }
$expectedScope = if ($Mode -eq 'Ui') { 'windows_wpf_controls' } else { 'native_windows_decode_and_audio_extraction' }
$arguments = '{0} "{1}" "{2}" "{3}"' -f $entry, $MediaPath, $SubtitlePath, $ReportPath
$process = Start-Process -FilePath $application -ArgumentList $arguments -PassThru
if (-not $process.WaitForExit(220000)) {
    $process.Kill()
    throw 'The native decode/audio extraction process exceeded 220 seconds; no pass is recorded.'
}
if (-not (Test-Path -LiteralPath $ReportPath)) { throw "No fresh native report was written. Process exit code: $($process.ExitCode)" }
$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
if (-not $report.PSObject.Properties['ProcessArchitecture'] -or $report.ProcessArchitecture -ne $expectedArchitecture) {
    throw "The fresh report must prove $expectedArchitecture execution for $RuntimeIdentifier; another architecture's result is not accepted. Report: $ReportPath"
}
if ($process.ExitCode -ne 0 -or $report.Status -ne 'PASS' -or $report.Scope -ne $expectedScope) {
    throw "Windows $expectedScope failed. Read $ReportPath. Exit code: $($process.ExitCode); error: $($report.Error)"
}
if ($Mode -eq 'Ui') {
    if (@($report.Screenshots).Count -lt 2) { throw 'UI validation did not supply normal and fullscreen screenshots.' }
    foreach ($screenshot in $report.Screenshots) {
        if (-not (Test-Path -LiteralPath $screenshot) -or (Get-Item -LiteralPath $screenshot).Length -le 0) {
            throw "Missing or empty actual desktop screenshot: $screenshot"
        }
    }
}
if ($RequireSegmentationBoundary -and (-not $report.AudioSegmentationBoundaryExercised -or @($report.AudioSegments).Count -lt 2)) {
    throw 'This check required a media file longer than 600 seconds and at least two validated extracted audio segments.'
}
$checks = @($report.Checks.PSObject.Properties)
if ($checks.Count -eq 0 -or @($checks | Where-Object { $_.Value -ne $true }).Count -ne 0) {
    throw 'The Windows report contains no assertions or a failed assertion.'
}
Write-Host "PASS: $($checks.Count) $expectedScope assertions on $expectedArchitecture. Report: $ReportPath"
Write-Host 'Pending separate validation:' ($report.NotTested -join ', ')
