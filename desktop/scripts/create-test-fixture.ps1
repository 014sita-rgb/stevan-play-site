#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/test-fixture'),
    [string]$Ffmpeg = 'ffmpeg',
    [ValidateRange(15, 3600)][int]$DurationSeconds = 20,
    [ValidateRange(1, 60)][int]$FrameRate = 25,
    [ValidatePattern('^[A-Za-z0-9._-]+$')][string]$Stem = 'Stevan.Validation.2026'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$media = Join-Path $OutputDirectory ($Stem + '.mp4')
$subtitle = Join-Path $OutputDirectory ($Stem + '.sr.srt')
# Synthetic, legally distributable media with actual moving video and sine-wave audio.
& $Ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=640x360:rate=$FrameRate" -f lavfi -i 'sine=frequency=440:sample_rate=48000' -t $DurationSeconds -c:v mpeg4 -q:v 4 -c:a aac -b:a 96k -movflags +faststart $media
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg did not generate the native decode fixture.' }
$srt = @'
1
00:00:00,000 --> 00:00:06,000
СТЕВАНplejer — провера српског титла

2
00:00:06,000 --> 00:00:12,000
Č, Ć, Ž, Š, Đ — hrvatski i bosanski

3
00:00:12,000 --> 00:00:20,000
Пауза, померање и наставак репродукције

'@
[IO.File]::WriteAllText($subtitle, $srt.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.UTF8Encoding]::new($false))
Write-Host "Created native test fixture: $media"
