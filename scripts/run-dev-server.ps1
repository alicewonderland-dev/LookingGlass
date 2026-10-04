# Runs the server in Development mode: debug accounts and the hosted echo bot
# are ON. For testing on a private network (for example Tailscale) only.
param([int]$Port = 5180)
Set-Location (Join-Path $PSScriptRoot '..')
$env:ASPNETCORE_ENVIRONMENT = 'Development'
# One database for every copy of the code (worktrees, clean builds): outside the
# build output, so rebuilding or switching checkouts never starts an empty one.
# Set LookingGlass__DataDirectory yourself to use somewhere else.
if (-not $env:LookingGlass__DataDirectory) {
    $env:LookingGlass__DataDirectory = Join-Path $env:LOCALAPPDATA 'LookingGlass\dev-server'
}
Write-Host "Data directory: $env:LookingGlass__DataDirectory"
dotnet run --project src/LookingGlass.Server -c Release -- --urls "http://0.0.0.0:$Port" @args
