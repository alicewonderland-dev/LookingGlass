# Builds a self-contained Linux server for x64 or ARM64 (-Runtime linux-x64, the default, or linux-arm64) and packs
# it with the systemd units and deploy/install-linux.sh into lookingglass-server-<runtime>.tar.gz. On the Linux
# machine (`uname -m` says x86_64 or aarch64): tar -xzf it, then sudo sh ./install-linux.sh <wss:// address>.
param(
    [ValidateSet('linux-x64', 'linux-arm64')]
    [string]$Runtime = 'linux-x64',
    [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
if (-not $Output) {
    $Output = Join-Path (Get-Location) "lookingglass-server-$Runtime.tar.gz"
}

Set-Location (Join-Path $PSScriptRoot '..')

$staging = Join-Path ([IO.Path]::GetTempPath()) ("lookingglass-publish-" + [guid]::NewGuid().ToString('N'))
try {
    $server = Join-Path $staging 'server'
    dotnet publish src/LookingGlass.Server -c Release -r $Runtime --self-contained -o $server
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    # The native libraries the server loads at run time, for this processor: SQLite (through Microsoft.Data.Sqlite) and
    # libsodium (through NSec). A package without them would only fail on the machine, at the first database or key use.
    foreach ($native in 'libe_sqlite3.so', 'libsodium.so') {
        if (-not (Test-Path (Join-Path $server $native))) {
            throw "The $Runtime package has no $native."
        }
    }

    Copy-Item deploy/lookingglass.service, deploy/lookingglass-backup.service, deploy/lookingglass-backup.timer, deploy/install-linux.sh $staging
    tar -czf $Output -C $staging .
    if ($LASTEXITCODE -ne 0) { throw "tar failed." }
    Write-Host "Wrote $Output ($Runtime)"
} finally {
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}
