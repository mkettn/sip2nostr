#!/bin/sh
# Publishes a framework-dependent, single-file build of sip2nostr for the
# current machine's architecture into out/<rid>/. Run ./install.sh
# afterward to put it on $PATH. Requires the .NET 8 SDK - the .NET 8
# runtime alone (dotnet-runtime-8.0) is what's needed to *run* the result,
# not to build it.
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

out_dir="$script_dir/out/$rid"
rm -rf "$out_dir"

# --self-contained false: the .NET runtime itself is not bundled, it must
# already be installed on whatever machine runs the result (see README).
# PublishSingleFile=true collapses every managed dependency into the one
# Sip2Nostr executable; it doesn't touch native libraries (Whisper.net's
# whisper.cpp binding, Nostr.Sdk's Rust binding) at all, which is
# deliberate - see docs/voicemail.md and Sip2Nostr.csproj's
# ExcludeMismatchedLinuxWhisperNatives target for why those need to stay
# loose files next to the executable rather than embedded.
dotnet publish "$script_dir/src/Sip2Nostr/Sip2Nostr.csproj" \
    -c Release \
    -r "$rid" \
    --self-contained false \
    -p:PublishSingleFile=true \
    -o "$out_dir"

rm -f "$out_dir/Sip2Nostr.pdb"

# Whisper.net.Runtime ships every platform's native binaries regardless of
# target RID and dotnet publish -r doesn't prune them (see
# Sip2Nostr.csproj's ExcludeMismatchedLinuxWhisperNatives target for the
# same problem scoped to the other Linux architectures, which that target
# already handles). What's left after that target runs is other
# operating systems' builds - harmless if shipped, but install.sh copies
# this whole directory verbatim, so they'd otherwise end up sitting
# uselessly in /usr/local/lib/sip2nostr too.
rm -rf "$out_dir/runtimes/win-x64" "$out_dir/runtimes/win-arm64" "$out_dir/runtimes/win-x86" \
    "$out_dir/runtimes/macos-x64" "$out_dir/runtimes/macos-arm64"
rm -f "$out_dir/ggml-metal.metal"

echo ""
echo "Built $out_dir/Sip2Nostr"
echo "Run ./install.sh to install it to /usr/local."
