#!/usr/bin/env bash
set -euo pipefail

# Run in the existing checkout. Installation never changes application source or lockfiles.
task_sdk_root=/workspace/.dotnet
task_tools_root=/workspace/.tools/nsis
task_powershell_root=/workspace/.tools/powershell
task_download_dir=$(mktemp -d /tmp/stevan-setup.XXXXXX)
trap 'find "$task_download_dir" -type f -delete; rmdir "$task_download_dir"' EXIT
export DOTNET_CLI_HOME=/workspace/.dotnet-home
export NUGET_PACKAGES=/workspace/.nuget/packages
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

if [[ ! -x "$task_sdk_root/dotnet" ]] || [[ "$("$task_sdk_root/dotnet" --version)" != 10.0.401 ]]; then
  curl --fail --show-error --location \
    https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-linux-x64.tar.gz \
    --output "$task_download_dir/sdk.tar.gz"
  printf '%s  %s\n' \
    51c8b999af9e8dd9998c9edc5944e19a90788862068acd38694e098889054ce8c23d4f0c5cccfa16bf187d044562359e5ee69a9f8ad0bbe913ba90311fbce25b \
    "$task_download_dir/sdk.tar.gz" | sha512sum --check --strict
  mkdir -p "$task_sdk_root"
  tar -xzf "$task_download_dir/sdk.tar.gz" -C "$task_sdk_root"
fi

# SHA256 pins below were checked against the signed Debian trixie Release/Packages
# chain using the machine's Debian archive keyring and sqv during onboarding.
if [[ ! -x "$task_tools_root/usr/bin/makensis" ]]; then
  curl --fail --show-error --location https://deb.debian.org/debian/pool/main/n/nsis/nsis_3.11-1_amd64.deb \
    --output "$task_download_dir/nsis.deb"
  curl --fail --show-error --location https://deb.debian.org/debian/pool/main/n/nsis/nsis-common_3.11-1_all.deb \
    --output "$task_download_dir/nsis-common.deb"
  printf '%s  %s\n' e6eb6ffb70f0df09e95dedb2d2cf55ad3c2679f7c2ae357d0c00cc4c83178690 "$task_download_dir/nsis.deb" | sha256sum --check --strict
  printf '%s  %s\n' 103a3284c1a5356efa0aba90fdfc391c3cde8e7df8726377cef0f98471584047 "$task_download_dir/nsis-common.deb" | sha256sum --check --strict
  mkdir -p "$task_tools_root"
  dpkg-deb -x "$task_download_dir/nsis.deb" "$task_tools_root"
  dpkg-deb -x "$task_download_dir/nsis-common.deb" "$task_tools_root"
fi

if [[ ! -x "$task_powershell_root/pwsh" ]]; then
  curl --fail --show-error --location \
    https://github.com/PowerShell/PowerShell/releases/download/v7.5.4/powershell-7.5.4-linux-x64.tar.gz \
    --output "$task_download_dir/powershell.tar.gz"
  printf '%s  %s\n' 1fd7983fe56ca9e6233f126925edb24bf6b6b33e356b69996d925c4db94e2fef \
    "$task_download_dir/powershell.tar.gz" | sha256sum --check --strict
  mkdir -p "$task_powershell_root"
  tar -xzf "$task_download_dir/powershell.tar.gz" -C "$task_powershell_root"
  chmod u+x "$task_powershell_root/pwsh"
fi

export XDG_CACHE_HOME=/workspace/.tools/cache
export XDG_CONFIG_HOME=/workspace/.tools/config
export XDG_DATA_HOME=/workspace/.tools/data
export NSISDIR="$task_tools_root/usr/share/nsis"
"$task_sdk_root/dotnet" restore StevanPlayer.Tests/StevanPlayer.Tests.csproj --locked-mode
"$task_sdk_root/dotnet" restore StevanPlayer.App/StevanPlayer.App.csproj --locked-mode
"$task_sdk_root/dotnet" test StevanPlayer.Tests/StevanPlayer.Tests.csproj --no-restore --configuration Release \
  --logger 'trx;LogFileName=cloud-core.trx' --results-directory artifacts/test-results
"$task_sdk_root/dotnet" build StevanPlayer.App/StevanPlayer.App.csproj --no-restore --configuration Release
"$task_tools_root/usr/bin/makensis" -VERSION
