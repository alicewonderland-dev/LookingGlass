#!/usr/bin/env sh
# Runs the server in Development mode: debug accounts and the hosted echo bot
# are ON. For testing on a private network (for example Tailscale) only.
set -eu
cd "$(dirname "$0")/.."
export ASPNETCORE_ENVIRONMENT=Development
exec dotnet run --project src/LookingGlass.Server -c Release -- --urls "http://0.0.0.0:${PORT:-5180}" "$@"
