# Runs the server in Development mode: debug accounts and the hosted echo bot
# are ON. For testing on a private network (for example Tailscale) only.
param([int]$Port = 5180)
Set-Location (Join-Path $PSScriptRoot '..')
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/LookingGlass.Server -c Release -- --urls "http://0.0.0.0:$Port" @args
