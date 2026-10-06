#!/usr/bin/env sh
# Installs or updates a published LookingGlass server as a systemd service.
# Run it from the folder scripts/publish-linux.ps1 makes (it holds server/ and
# lookingglass.service), with every address clients connect to:
#   sudo sh ./install-linux.sh wss://chat.example.com/ws [more addresses...]
# The database stays in /var/lib/lookingglass across updates; the previous
# install is kept as /opt/lookingglass.old.
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Run this with sudo." >&2
    exit 1
fi
if [ $# -lt 1 ]; then
    echo "Usage: sudo sh $0 <address clients connect to, e.g. wss://chat.example.com/ws> [more...]" >&2
    exit 1
fi

here=$(cd "$(dirname "$0")" && pwd)

if ! id lookingglass >/dev/null 2>&1; then
    useradd --system --no-create-home --shell /usr/sbin/nologin lookingglass
fi

systemctl stop lookingglass 2>/dev/null || true

rm -rf /opt/lookingglass.new
cp -r "$here/server" /opt/lookingglass.new
chmod 755 /opt/lookingglass.new/LookingGlass.Server
rm -rf /opt/lookingglass.old
if [ -d /opt/lookingglass ]; then
    mv /opt/lookingglass /opt/lookingglass.old
fi
mv /opt/lookingglass.new /opt/lookingglass
# SELinux (Fedora and its relatives): files copied from a home folder keep a label systemd won't run.
if command -v restorecon >/dev/null 2>&1; then
    restorecon -R /opt/lookingglass
fi

cp "$here/lookingglass.service" /etc/systemd/system/lookingglass.service
# The daily backup: installed (and kept up to date if enabled), but only enabled by the operator, as a host that
# replicates the database with Litestream doesn't need it: sudo systemctl enable --now lookingglass-backup.timer
cp "$here/lookingglass-backup.service" "$here/lookingglass-backup.timer" /etc/systemd/system/
# The addresses go in a drop-in, so the unit file itself can be replaced on every update.
mkdir -p /etc/systemd/system/lookingglass.service.d
{
    echo "[Service]"
    i=0
    for url in "$@"; do
        echo "Environment=LookingGlass__PublicUrls__$i=$url"
        i=$((i + 1))
    done
} > /etc/systemd/system/lookingglass.service.d/public-urls.conf

systemctl daemon-reload
systemctl enable lookingglass
systemctl restart lookingglass
sleep 2
systemctl --no-pager status lookingglass || true
echo
echo "Logs: journalctl -u lookingglass -f"
echo "The database: /var/lib/lookingglass/lookingglass.db. Daily backups (without Litestream): sudo systemctl enable --now lookingglass-backup.timer"
