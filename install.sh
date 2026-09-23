#!/bin/sh
# Installs a build produced by ./build.sh to /usr/local. Everything this
# writes lives in exactly two places - a symlink at $bin_link and a
# directory at $install_dir - so uninstalling is always just:
#   rm -rf /usr/local/lib/sip2nostr /usr/local/bin/sip2nostr
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
bin_link="/usr/local/bin/sip2nostr"

rm -rf "$install_dir"
mkdir -p "$install_dir/runtimes/$rid"

# Only what sip2nostr actually needs at runtime: the executable,
# Nostr.Sdk's native library (a plain sibling file), and Whisper.net's
# native library for this architecture (has to stay under
# runtimes/<rid>/ next to the executable - that's where its own loader
# looks). Everything else dotnet publish leaves behind - debug symbols,
# other architectures' and operating systems' native builds - is simply
# never copied.
cp "$build_dir/Sip2Nostr" "$install_dir/"
cp "$build_dir/libnostr_sdk_ffi.so" "$install_dir/"
cp "$build_dir/runtimes/$rid/"* "$install_dir/runtimes/$rid/"
chmod +x "$install_dir/Sip2Nostr"

ln -sf "$install_dir/Sip2Nostr" "$bin_link"

echo ""
echo "Installed to $install_dir, linked as $bin_link."
echo "Copy config.example.toml to a config.toml of your choosing, fill in your credentials, and run: sip2nostr /path/to/config.toml"
echo ""
echo "To uninstall: rm -rf $install_dir $bin_link"
