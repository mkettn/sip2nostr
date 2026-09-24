#!/bin/sh
set -eu

script_dir="$(cd "$(dirname "$0")" && pwd)"

case "$(uname -m)" in
    x86_64) rid="linux-x64" ;;
    aarch64|arm64) rid="linux-arm64" ;;
    *)
        echo "Unsupported architecture: $(uname -m) - sip2nostr targets linux-x64 (amd64) and linux-arm64 (e.g. Raspberry Pi 4/5) only." >&2
        exit 1
        ;;
esac

build_dir="$script_dir/out/$rid"
if [ ! -x "$build_dir/Sip2Nostr" ]; then
    echo "No build found at $build_dir - run ./build.sh first." >&2
    exit 1
fi

install_dir="/usr/local/lib/sip2nostr"
sysusers_file="/usr/local/lib/sysusers.d/sip2nostr.conf"
unit_file="/usr/local/lib/systemd/system/sip2nostr.service"
data_dir="/var/local/lib/sip2nostr"

if [ "$(id -u)" -ne 0 ]; then
    echo "install.sh writes to /usr/local/lib and /var/local/lib - run it with sudo." >&2
    exit 1
fi

rm -rf "$install_dir"
mkdir -p "$install_dir/runtimes/$rid"
cp "$build_dir/Sip2Nostr" "$install_dir/"
cp "$build_dir"/*.so "$install_dir/"
cp "$build_dir/runtimes/$rid/"* "$install_dir/runtimes/$rid/"
chmod +x "$install_dir/Sip2Nostr"

mkdir -p "$(dirname "$sysusers_file")"
cp "$script_dir/systemd/sysusers.d/sip2nostr.conf" "$sysusers_file"
systemd-sysusers "$sysusers_file"

install -d -o sip2nostr -g sip2nostr -m 0700 "$data_dir"
chmod g-s "$data_dir"

if [ ! -e "$data_dir/config.toml" ]; then
    install -o sip2nostr -g sip2nostr -m 0600 "$script_dir/config.example.toml" "$data_dir/config.toml"
fi

mkdir -p "$(dirname "$unit_file")"
cp "$script_dir/systemd/sip2nostr.service" "$unit_file"
systemctl daemon-reload

echo ""
echo "sip2nostr needs to be configured:"
echo "  sudo nano $data_dir/config.toml"
echo "then enabled:"
echo "  sudo systemctl enable --now sip2nostr"
