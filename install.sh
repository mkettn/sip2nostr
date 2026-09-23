#!/bin/sh
# Installs a build produced by ./build.sh to /usr/local. Everything this
# writes lives in exactly two places - a symlink at $BIN_LINK and a
# directory at $INSTALL_DIR - so uninstalling is always just:
#   rm -rf /usr/local/lib/sip2nostr /usr/local/bin/sip2nostr
# No package manager state, no systemd unit, nothing else touched.
set -eu

script_dir="$(cd "$(dirname "$0")" && pwd)"

case "$(uname -m)" in
    x86_64) rid="linux-x64" ;;
    aarch64|arm64) rid="linux-arm64" ;;
    *)
        echo "Unsupported architecture: $(uname -m) - sip2nostr targets linux-x64 and linux-arm64 only." >&2
        exit 1
        ;;
esac

build_dir="$script_dir/out/$rid"
if [ ! -x "$build_dir/Sip2Nostr" ]; then
    echo "No build found at $build_dir - run ./build.sh first." >&2
    exit 1
fi

install_dir="/usr/local/lib/sip2nostr"
bin_link="/usr/local/bin/sip2nostr"

# Whisper.net resolves its native library relative to wherever Sip2Nostr
# itself lives (see build.sh's comment on PublishSingleFile) - the exe
# and runtimes/<rid>/ have to land in the same directory together, which
# is why this installs everything as one directory rather than splitting
# the binary into bin/ and its dependencies into lib/ the way a
# traditional Unix install would.
rm -rf "$install_dir"
mkdir -p "$install_dir"
cp -a "$build_dir/." "$install_dir/"
chmod +x "$install_dir/Sip2Nostr"
ln -sf "$install_dir/Sip2Nostr" "$bin_link"

echo ""
echo "Installed to $install_dir, linked as $bin_link."
echo "Copy config.example.toml to a config.toml of your choosing, fill in your credentials, and run: sip2nostr /path/to/config.toml"
echo ""
echo "To uninstall: rm -rf $install_dir $bin_link"
