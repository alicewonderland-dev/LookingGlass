# Builds a self-contained Linux (x64) server and packs it with the systemd unit
# and deploy/install-linux.sh into lookingglass-server-linux-x64.tar.gz. On the
# Linux machine: tar -xzf it, then sudo sh ./install-linux.sh <wss:// address>.
param(
    [string]$Output = (Join-Path (Get-Location) 'lookingglass-server-linux-x64.tar.gz')
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$staging = Join-Path ([IO.Path]::GetTempPath()) ("lookingglass-publish-" + [guid]::NewGuid().ToString('N'))
try {
    dotnet publish src/LookingGlass.Server -c Release -r linux-x64 --self-contained -o (Join-Path $staging 'server')
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
    Copy-Item deploy/lookingglass.service, deploy/install-linux.sh $staging
    tar -czf $Output -C $staging .
    if ($LASTEXITCODE -ne 0) { throw "tar failed." }
    Write-Host "Wrote $Output"
} finally {
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}
