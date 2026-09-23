#!/bin/sh
# Publishes a framework-dependent, single-file build of sip2nostr for the
# current machine's architecture into out/<rid>/. Run ./install.sh
# afterward to put it on $PATH.
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

out_dir="$script_dir/out/$rid"
rm -rf "$out_dir"

dotnet publish "$script_dir/src/Sip2Nostr/Sip2Nostr.csproj" \
    -c Release \
    -r "$rid" \
    --self-contained false \
    -p:PublishSingleFile=true \
    -o "$out_dir"

echo ""
echo "Built $out_dir/Sip2Nostr"
echo "Run ./install.sh to install it to /usr/local."
