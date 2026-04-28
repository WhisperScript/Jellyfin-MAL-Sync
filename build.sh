#!/usr/bin/env bash
# build.sh – builds the Jellyfin MAL Sync plugin
# Usage:  ./build.sh [--install]
#   --install  copies the built DLL into the Jellyfin plugin folder automatically
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$SCRIPT_DIR/Jellyfin.Plugin.MalSync/Jellyfin.Plugin.MalSync.csproj"
OUT="$SCRIPT_DIR/dist"

# Default Jellyfin plugin path (adjust if yours differs)
JF_PLUGIN_DIR="${JF_PLUGIN_DIR:-/var/lib/jellyfin/plugins/MalSync}"

echo "=== Jellyfin MAL Sync – Build ==="

# ── Check for .NET SDK ─────────────────────────────────────────────────────
if ! command -v dotnet &>/dev/null; then
    echo ""
    echo "ERROR: .NET SDK not found."
    echo "Install it with:"
    echo ""
    echo "  # Ubuntu/Debian:"
    echo "  wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh"
    echo "  chmod +x dotnet-install.sh"
    echo "  ./dotnet-install.sh --channel 8.0"
    echo "  export PATH=\"\$HOME/.dotnet:\$PATH\""
    echo ""
    echo "  # Or via apt (Ubuntu 22.04+):"
    echo "  sudo apt-get install -y dotnet-sdk-8.0"
    echo ""
    exit 1
fi

echo "dotnet: $(dotnet --version)"
echo ""

# ── Build ──────────────────────────────────────────────────────────────────
rm -rf "$OUT"
dotnet publish "$PROJECT" \
    --configuration Release \
    --output "$OUT" \
    --no-self-contained \
    -p:DebugType=none \
    -p:DebugSymbols=false

echo ""
echo "Build output:"
ls -lh "$OUT"/*.dll 2>/dev/null || true

# ── Optional install ───────────────────────────────────────────────────────
if [[ "${1:-}" == "--install" ]]; then
    echo ""
    echo "Installing to $JF_PLUGIN_DIR …"
    sudo mkdir -p "$JF_PLUGIN_DIR"
    sudo cp "$OUT/Jellyfin.Plugin.MalSync.dll" "$JF_PLUGIN_DIR/"
    echo "Done. Restart Jellyfin to load the plugin:"
    echo "  sudo systemctl restart jellyfin"
fi

echo ""
echo "=== Build complete ==="
echo "DLL: $OUT/Jellyfin.Plugin.MalSync.dll"
echo ""
echo "To install manually:"
echo "  sudo mkdir -p $JF_PLUGIN_DIR"
echo "  sudo cp $OUT/Jellyfin.Plugin.MalSync.dll $JF_PLUGIN_DIR/"
echo "  sudo systemctl restart jellyfin"
