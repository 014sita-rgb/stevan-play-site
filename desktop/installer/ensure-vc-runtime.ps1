# Windows PowerShell 5.1-compatible installer helper. NSIS executes this bundled
# source as an inline ScriptBlock; it does not change the machine execution policy.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$prerequisiteExitCode = 1
$downloadDirectory = $null
$minimumRuntimeVersion = [Version]'14.44.0.0' # LLama 0.27.0 native PE linker: 14.44.
$installerUri = 'https://download.visualstudio.microsoft.com/download/pr/bd1c8d9d-ba95-4eee-bc6e-df1fcc876373/CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B/VC_redist.x64.exe'
$installerSha256 = 'CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B'

function Get-NativePeMachine([string]$FilePath) {
    $stream = [IO.File]::Open($FilePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d) { return 0 }
        $stream.Position = 60
        $header = $reader.ReadUInt32()
        if ($header -lt 64 -or $header -gt 1048576 -or ($header + 6) -gt $stream.Length) { return 0 }
        $stream.Position = $header
        if ($reader.ReadUInt32() -ne 0x4550) { return 0 }
        return $reader.ReadUInt16()
    }
    finally { $reader.Dispose() }
}

function Get-InstalledRuntimeVersion {
    $highest = $null
    # Microsoft's bootstrapper has used both registry views. Always ask for the
    # x64 runtime key explicitly; an installed x86 runtime cannot satisfy it.
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
        $runtimeKey = $null
        try {
            $runtimeKey = $registry.OpenSubKey('SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64')
            if ($null -eq $runtimeKey -or $runtimeKey.GetValue('Installed', 0) -ne 1) { continue }
            $versionText = [string]$runtimeKey.GetValue('Version', '')
            $version = $null
            if ([Version]::TryParse($versionText.TrimStart('v', 'V'), [ref]$version) -and ($null -eq $highest -or $version -gt $highest)) {
                $highest = $version
            }
        }
        finally {
            if ($null -ne $runtimeKey) { $runtimeKey.Dispose() }
            $registry.Dispose()
        }
    }
    return $highest
}

function Test-CompatibleRuntime {
    $installed = Get-InstalledRuntimeVersion
    if ($null -eq $installed -or $installed -lt $minimumRuntimeVersion) { return $false }
    foreach ($name in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'vcomp140.dll')) {
        $file = Join-Path ([Environment]::SystemDirectory) $name
        if (-not [IO.File]::Exists($file) -or (Get-NativePeMachine $file) -ne 0x8664) { return $false }
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($file)
        $version = New-Object Version($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
        if ($version -lt $minimumRuntimeVersion) { return $false }
    }
    return $true
}

function Save-VerifiedInstaller([string]$Destination) {
    Add-Type -AssemblyName System.Net.Http
    # Enable TLS 1.2 on older .NET defaults without accepting invalid certificates.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $handler = New-Object Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $client = New-Object Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(10)
    $cancellation = New-Object Threading.CancellationTokenSource
    $cancellation.CancelAfter([TimeSpan]::FromMinutes(10))
    $response = $null
    $source = $null
    $destinationStream = $null
    try {
        $uri = [Uri]$installerUri
        for ($redirect = 0; $redirect -le 5; $redirect++) {
            if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'download.visualstudio.microsoft.com') {
                throw 'Microsoft runtime download redirected outside its allowed HTTPS host.'
            }
            $request = New-Object Net.Http.HttpRequestMessage([Net.Http.HttpMethod]::Get, $uri)
            try { $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $cancellation.Token).GetAwaiter().GetResult() }
            finally { $request.Dispose() }
            if ([int]$response.StatusCode -ge 300 -and [int]$response.StatusCode -lt 400) {
                if ($null -eq $response.Headers.Location) { throw 'Microsoft runtime redirect has no destination.' }
                $uri = New-Object Uri($uri, $response.Headers.Location)
                $response.Dispose()
                $response = $null
                continue
            }
            break
        }
        if ($null -eq $response -or $response.StatusCode -ne [Net.HttpStatusCode]::OK) {
            throw 'Microsoft runtime download did not return HTTP 200.'
        }
        $maximumBytes = 64MB
        if ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -gt $maximumBytes) {
            throw 'Microsoft runtime download is larger than the allowed limit.'
        }
        $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $destinationStream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $buffer = New-Object byte[] 65536
        $bytesWritten = 0L
        while (($read = $source.ReadAsync($buffer, 0, $buffer.Length, $cancellation.Token).GetAwaiter().GetResult()) -gt 0) {
            $bytesWritten += $read
            if ($bytesWritten -gt $maximumBytes) { throw 'Microsoft runtime download exceeded the allowed limit.' }
            $destinationStream.Write($buffer, 0, $read)
        }
        if ($bytesWritten -eq 0) { throw 'Microsoft runtime download is empty.' }
    }
    finally {
        if ($null -ne $destinationStream) { $destinationStream.Dispose() }
        if ($null -ne $source) { $source.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        $cancellation.Dispose()
        $client.Dispose()
    }
    if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $installerSha256) {
        throw 'Microsoft runtime checksum does not match the pinned SHA-256. Nothing was executed.'
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Destination
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
        throw 'Microsoft runtime does not have a valid Microsoft Authenticode signature. Nothing was executed.'
    }
}

try {
    if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess) {
        throw 'The x64 runtime prerequisite must run in native 64-bit Windows PowerShell.'
    }
    if (Test-CompatibleRuntime) {
        Write-Output 'Compatible Microsoft Visual C++ x64 runtime is already installed; no download or elevation is needed.'
        $prerequisiteExitCode = 0
    }
    else {
        Write-Output 'Installing Microsoft Visual C++ x64 runtime for local subtitle recognition and translation. Windows may request administrator approval.'
        $downloadDirectory = Join-Path ([IO.Path]::GetTempPath()) ('StevanPlayer-vc-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($downloadDirectory) | Out-Null
        $installer = Join-Path $downloadDirectory 'VC_redist.x64.exe'
        Save-VerifiedInstaller $installer
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($installer)
        $downloadedVersion = New-Object Version($info.ProductMajorPart, $info.ProductMinorPart, $info.ProductBuildPart, $info.ProductPrivatePart)
        if ($downloadedVersion -lt $minimumRuntimeVersion) { throw 'The pinned Microsoft runtime is older than the native subtitle engines require.' }
        # Check again after downloading so a concurrently installed newer runtime
        # is preserved. The app installer never removes this shared prerequisite.
        if (Test-CompatibleRuntime) {
            Write-Output 'A compatible runtime became available; installation was skipped.'
            $prerequisiteExitCode = 0
        }
        else {
            $logDirectory = Join-Path $env:LOCALAPPDATA 'StevanPlayer\installer'
            [IO.Directory]::CreateDirectory($logDirectory) | Out-Null
            $logPath = Join-Path $logDirectory ('vc-runtime-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '.log')
            $process = Start-Process -FilePath $installer -ArgumentList @('/install', '/passive', '/norestart', '/log', ('"' + $logPath + '"')) -Verb RunAs -Wait -PassThru
            $result = $process.ExitCode
            if ($result -eq -2147023258) { $result = 1638 } # HRESULT_FROM_WIN32(ERROR_PRODUCT_VERSION).
            if ($result -eq 3010) {
                Write-Output 'Microsoft runtime installation requires a Windows restart before starting the player.'
                $prerequisiteExitCode = 3010
            }
            elseif ($result -eq 0 -and (Test-CompatibleRuntime)) { $prerequisiteExitCode = 0 }
            elseif ($result -eq 1638 -and (Test-CompatibleRuntime) -and (Get-InstalledRuntimeVersion) -ge $downloadedVersion) {
                Write-Output 'A compatible newer Microsoft runtime is installed.'
                $prerequisiteExitCode = 0
            }
            else { throw "Microsoft runtime prerequisite failed (exit $result). Log: $logPath" }
        }
    }
}
catch {
    Write-Output ('Microsoft Visual C++ prerequisite failed: ' + $_.Exception.Message)
    $prerequisiteExitCode = 1
}
finally {
    if ($null -ne $downloadDirectory -and [IO.Directory]::Exists($downloadDirectory)) {
        Remove-Item -LiteralPath $downloadDirectory -Force -Recurse -ErrorAction SilentlyContinue
    }
}
exit $prerequisiteExitCode
