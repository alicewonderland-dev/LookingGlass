#!/usr/bin/env sh
# Runs the server in Development mode: debug accounts and the hosted echo bot
# are ON. For testing on a private network (for example Tailscale) only.
set -eu
cd "$(dirname "$0")/.."
export ASPNETCORE_ENVIRONMENT=Development
# One database for every copy of the code (worktrees, clean builds): outside the
# build output, so rebuilding or switching checkouts never starts an empty one.
# Set LookingGlass__DataDirectory yourself to use somewhere else.
if [ -n "${LookingGlass__DataDirectory:-}" ]; then
    echo "Data directory: $LookingGlass__DataDirectory (from LookingGlass__DataDirectory)"
else
    LookingGlass__DataDirectory="${XDG_DATA_HOME:-$HOME/.local/share}/lookingglass/dev-server"
    echo "Data directory: $LookingGlass__DataDirectory"
fi
export LookingGlass__DataDirectory
exec dotnet run --project src/LookingGlass.Server -c Release -- --urls "http://0.0.0.0:${PORT:-5180}" "$@"
