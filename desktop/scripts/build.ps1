#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-x86')][string]$RuntimeIdentifier = 'win-x64',
    [switch]$BuildInstaller,
    [switch]$BuildUniversalInstaller,
    [string]$MakeNsis = 'makensis'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$desktopRoot = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $desktopRoot 'artifacts'
Push-Location $desktopRoot
try {
    $pinned = (Get-Content (Join-Path $desktopRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $actual = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne $pinned) { throw "Install the pinned .NET SDK $pinned (found $actual)." }
    # The app already declares both architectures. A global restore -r would change the
    # cross-platform referenced Core library's locked runtime identifiers.
    & dotnet restore 'StevanPlayer.App/StevanPlayer.App.csproj' --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed. No lockfile update is permitted by this build.' }
    & dotnet restore 'StevanPlayer.Tests/StevanPlayer.Tests.csproj' --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked test restore failed.' }
    & dotnet test 'StevanPlayer.Tests/StevanPlayer.Tests.csproj' -c $Configuration --no-restore --logger 'trx;LogFileName=core-tests.trx' --results-directory (Join-Path $artifacts 'test-results')
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed; publishing is blocked.' }
    $targets = if ($BuildUniversalInstaller) { @('win-x64', 'win-x86') } else { @($RuntimeIdentifier) }
    foreach ($target in $targets) {
        $publish = Join-Path $artifacts $target
        $manifest = Join-Path $artifacts "installer-payload-$target.nsh"
        if (Test-Path -LiteralPath $publish) {
            if (-not (Test-Path -LiteralPath (Join-Path $publish '.stevan-generated-output'))) {
                throw "Refusing to clear an unowned output directory: $publish. Select a clean checkout/output first."
            }
            Remove-Item -LiteralPath $publish -Force -Recurse
        }
        New-Item -ItemType Directory -Path $publish -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $publish '.stevan-generated-output') -Value "Owned generated $target publish output; safe to rebuild." -Encoding utf8
        & dotnet publish 'StevanPlayer.App/StevanPlayer.App.csproj' -c $Configuration -r $target --self-contained true --no-restore -p:PublishSingleFile=false -p:PublishTrimmed=false -o $publish
        if ($LASTEXITCODE -ne 0) { throw "Windows $target publish failed." }
        & node (Join-Path $PSScriptRoot 'prepare-publish.mjs') $publish $manifest $target
        if ($LASTEXITCODE -ne 0) { throw "License and $target installer manifest generation failed." }
        $architecture = $target.Substring(4)
        $archive = Join-Path $artifacts "STEVANplejer-Windows-$architecture-candidate.zip"
        Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -Force
        if ($BuildInstaller -or $BuildUniversalInstaller) {
            # NSIS uses different command-line prefixes on Unix and Windows.
            $definePrefix = if ($IsWindows) { '/D' } else { '-D' }
            & $MakeNsis "$($definePrefix)RID=$target" (Join-Path $desktopRoot 'installer/StevanPlayer.nsi')
            if ($LASTEXITCODE -ne 0) { throw "NSIS $target installer compilation failed." }
        }
        Get-FileHash -Algorithm SHA256 -LiteralPath $archive | Format-List Path, Hash
    }
    if ($BuildUniversalInstaller) {
        $definePrefix = if ($IsWindows) { '/D' } else { '-D' }
        & $MakeNsis "$($definePrefix)UNIVERSAL" (Join-Path $desktopRoot 'installer/StevanPlayer.nsi')
        if ($LASTEXITCODE -ne 0) { throw 'Universal installer compilation failed.' }
        Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $artifacts 'STEVANplejer-Windows-universal-candidate-setup.exe') | Format-List Path, Hash
    }
    Write-Host 'Compilation completed. Candidate artifacts remain internal until VALIDATION.md release checks pass.'
}
finally { Pop-Location }
