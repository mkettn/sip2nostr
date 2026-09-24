#!/bin/sh
# Installs a build produced by ./build.sh, the sip2nostr system user, and
# the systemd service that runs it. Nothing is put on $PATH - sip2nostr
# is meant to run under systemd, not invoked directly by name. sip2nostr
# isn't a distro package, so everything fixed lives under /usr/local (not
# /usr) and everything variable under /var/local (not /var, and not a
# systemd StateDirectory= - this script owns that job itself, not
# systemd). Writes to four places: /usr/local/lib/sip2nostr (the
# binary), /usr/local/lib/sysusers.d/sip2nostr.conf,
# /etc/systemd/system/sip2nostr.service, and /var/local/lib/sip2nostr
# (the service's data directory). See "To uninstall" below.
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
unit_file="/etc/systemd/system/sip2nostr.service"
data_dir="/var/local/lib/sip2nostr"

if [ "$(id -u)" -ne 0 ]; then
    echo "install.sh writes to /usr/local/lib, /etc/systemd/system, and /var/local/lib - run it with sudo." >&2
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

# /usr/local/lib/sysusers.d is sysusers.d(5)'s own location for locally
# installed, non-distro-packaged software (as opposed to
# /usr/lib/sysusers.d, for the latter) - systemd-sysusers scans it
# automatically, but most base installs don't ship the directory itself
# until something actually uses it, so create it if needed.
mkdir -p "$(dirname "$sysusers_file")"
cp "$script_dir/systemd/sysusers.d/sip2nostr.conf" "$sysusers_file"
systemd-sysusers "$sysusers_file"

# Safe to repeat on an existing install - install -d only touches
# ownership and mode, never a directory's contents - so a live
# deployment's config.toml and recordings survive a re-run untouched.
install -d -o sip2nostr -g sip2nostr -m 0700 "$data_dir"
# Debian's stock /usr/local and /var/local are root:staff with the
# setgid bit, which a directory newly created underneath inherits at
# creation time regardless of the mode just given it - confirmed here:
# install -d's own -m 0700 left this setgid, and even a follow-up
# numeric `chmod 0700` didn't clear it. Grants nothing extra by itself
# (group perms are 000 either way), but strip it anyway so the mode
# actually is 0700, matching what every comment/doc elsewhere calls it.
chmod g-s "$data_dir"

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
