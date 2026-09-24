#!/bin/sh
# Publishes a framework-dependent, single-file build of sip2nostr for the
# current machine's architecture into out/<rid>/. Run ./install.sh
# afterward to install it as a systemd service.
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

# Version format: v1.2.3-<short commit> or v1.2.3-rc5-<short commit>,
# with -dirty appended to the commit when the working tree has uncommitted
# changes. The base version normally comes from the nearest v*-shaped git
# tag; a VERSION file overrides that (e.g. for a build from a tarball with
# no tags reachable). Falls back to v0.0.0-xxxxxx when neither git nor
# VERSION is available.
version_file="$script_dir/VERSION"
if [ -f "$version_file" ]; then
    base_version="$(tr -d '[:space:]' < "$version_file")"
else
    base_version="$(git -C "$script_dir" describe --tags --abbrev=0 --match 'v[0-9]*' 2>/dev/null || true)"
    [ -n "$base_version" ] || base_version="v0.0.0"
fi

short_commit="$(git -C "$script_dir" rev-parse --short HEAD 2>/dev/null || true)"
if [ -n "$short_commit" ]; then
    [ -z "$(git -C "$script_dir" status --porcelain 2>/dev/null)" ] || short_commit="$short_commit-dirty"
else
    short_commit="xxxxxx"
fi

version="$base_version-$short_commit"

dotnet publish "$script_dir/src/Sip2Nostr/Sip2Nostr.csproj" \
    -c Release \
    -r "$rid" \
    --self-contained false \
    -p:PublishSingleFile=true \
    -p:InformationalVersion="$version" \
    -p:IncludeSourceRevisionInInformationalVersion=false \
    -o "$out_dir"

echo ""
echo "Built $out_dir/Sip2Nostr"
echo "Run ./install.sh to install it and its systemd service."
