#!/bin/sh
# Installs a build produced by ./build.sh, the sip2nostr system user, and
# the systemd service that runs it. Nothing is put on $PATH - sip2nostr
# is meant to run under systemd, not invoked directly by name. Writes to
# four places: /usr/local/lib/sip2nostr (the binary),
# /etc/sysusers.d/sip2nostr.conf, /etc/systemd/system/sip2nostr.service,
# and /var/lib/sip2nostr (the service's data directory - created here,
# not just left to the unit's own StateDirectory=, so config.toml has
# somewhere to go before the first start). See "To uninstall" below.
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
sysusers_file="/etc/sysusers.d/sip2nostr.conf"
unit_file="/etc/systemd/system/sip2nostr.service"
data_dir="/var/lib/sip2nostr"

if [ "$(id -u)" -ne 0 ]; then
    echo "install.sh writes to /usr/local, /etc/sysusers.d, /etc/systemd/system, and /var/lib - run it with sudo." >&2
    exit 1
fi

rm -rf "$install_dir"
mkdir -p "$install_dir/runtimes/$rid"

# Only what sip2nostr actually needs at runtime - see Sip2Nostr.csproj
# for why runtimes/<rid>/ has to stay a sibling of the executable.
# Everything else dotnet publish leaves behind (debug symbols, other
# architectures/operating systems' native builds) is simply never copied.
cp "$build_dir/Sip2Nostr" "$install_dir/"
cp "$build_dir"/*.so "$install_dir/"
cp "$build_dir/runtimes/$rid/"* "$install_dir/runtimes/$rid/"
chmod +x "$install_dir/Sip2Nostr"

# Not every base install ships /etc/sysusers.d itself (only
# /usr/lib/sysusers.d is guaranteed to exist), so create it if needed
# rather than assuming it's there.
mkdir -p "$(dirname "$sysusers_file")"
cp "$script_dir/systemd/sysusers.d/sip2nostr.conf" "$sysusers_file"
systemd-sysusers "$sysusers_file"

# The unit's own StateDirectory= would create this at first start
# anyway (same owner and mode); doing it here too just means
# config.toml has somewhere to go before that first start. Safe to
# repeat on an existing install - install -d only touches ownership
# and mode, never a directory's contents.
install -d -o sip2nostr -g sip2nostr -m 0700 "$data_dir"

cp "$script_dir/systemd/sip2nostr.service" "$unit_file"
systemctl daemon-reload

echo ""
echo "Installed to $install_dir. Service unit: $unit_file."
echo "The sip2nostr system user exists; its data directory, $data_dir,"
echo "is ready and owned by it."
echo ""
echo "Copy config.example.toml to $data_dir/config.toml (owned by"
echo "sip2nostr, mode 0600), fill in your credentials, then:"
echo "  systemctl enable --now sip2nostr"
echo ""
echo "To uninstall:"
echo "  systemctl disable --now sip2nostr"
echo "  rm -rf $install_dir $sysusers_file $unit_file"
echo "  systemctl daemon-reload"
echo "(leaves $data_dir - your config.toml and any voicemail recordings -"
echo "in place; remove that separately too if you want those gone as well)"
