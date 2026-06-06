#!/usr/bin/env bash
set -uo pipefail

# G-Helper Linux — Build Script
# Compiles the project as a native AOT binary and copies output to dist/

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="$SCRIPT_DIR/src"
DIST_DIR="$SCRIPT_DIR/dist"
PUBLISH_DIR="$SRC_DIR/bin/Release/net10.0/linux-x64/publish"

echo "=== G-Helper Linux Build ==="
echo ""

# Check .NET SDK
if ! command -v dotnet &>/dev/null; then
    echo "ERROR: .NET SDK not found."
    echo "Install it with:"
    echo "  Ubuntu/Debian:  sudo apt install dotnet-sdk-10.0"
    echo "  Fedora:         sudo dnf install dotnet-sdk-10.0"
    echo "  Arch:           sudo pacman -S dotnet-sdk"
    exit 1
fi

SDK_VERSION=$(dotnet --version 2>/dev/null || echo "unknown")
echo "Using .NET SDK: $SDK_VERSION"

# Check for clang (required for AOT)
if ! command -v clang &>/dev/null; then
    echo ""
    echo "WARNING: clang not found. Native AOT requires clang."
    echo "Install it with:"
    echo "  Ubuntu/Debian:  sudo apt install clang zlib1g-dev"
    echo "  Fedora:         sudo dnf install clang zlib-devel"
    echo "  Arch:           sudo pacman -S clang"
    echo ""
    read -rp "Try building anyway? [y/N] " ans
    [[ "$ans" =~ ^[Yy] ]] || exit 1
fi

# Build wlr-randr (Wayland display tool — vendored v0.5.0, MIT license)
WLR_RANDR_DIR="$SCRIPT_DIR/vendor/wlr-randr"
WLR_RANDR_BIN=""

if command -v wayland-scanner &>/dev/null && command -v cc &>/dev/null; then
    WLR_VERSION=$(cat "$WLR_RANDR_DIR/VERSION" 2>/dev/null || echo "unknown")
    echo ""
    echo "Building wlr-randr v${WLR_VERSION}..."
    (
        cd "$WLR_RANDR_DIR" || exit
        wayland-scanner client-header \
            protocol/wlr-output-management-unstable-v1.xml \
            wlr-output-management-unstable-v1-client-protocol.h
        wayland-scanner private-code \
            protocol/wlr-output-management-unstable-v1.xml \
            wlr-output-management-unstable-v1-protocol.c
        cc -O2 -o wlr-randr main.c wlr-output-management-unstable-v1-protocol.c \
            -I. -lwayland-client -lm
        strip wlr-randr
    )
    if [[ -f "$WLR_RANDR_DIR/wlr-randr" ]]; then
        WLR_RANDR_BIN="$WLR_RANDR_DIR/wlr-randr"
        echo "  wlr-randr built: $(du -sh "$WLR_RANDR_BIN" | cut -f1)"
    else
        echo "WARNING: wlr-randr build failed (Wayland refresh rate switching unavailable)"
    fi
else
    echo ""
    echo "NOTE: wayland-scanner not found, skipping wlr-randr build."
    echo "  Install with: sudo apt install libwayland-dev"
fi

# Build gpu-helper (root-only privileged GPU operations multiplexer, vendored)
GPU_HELPER_DIR="$SCRIPT_DIR/vendor/gpu-helper"
GPU_HELPER_BIN=""

if command -v cc &>/dev/null; then
    echo ""
    echo "Building gpu-helper..."
    (
        cd "$GPU_HELPER_DIR" || exit
        cc -O2 -Wall -o gpu-helper gpu-helper.c -ldl
        strip gpu-helper
    )
    if [[ -f "$GPU_HELPER_DIR/gpu-helper" ]]; then
        GPU_HELPER_BIN="$GPU_HELPER_DIR/gpu-helper"
        echo "  gpu-helper built: $(du -sh "$GPU_HELPER_BIN" | cut -f1)"
    else
        echo "WARNING: gpu-helper build failed (privileged GPU operations unavailable)"
    fi
else
    echo ""
    echo "NOTE: cc not found, skipping gpu-helper build."
fi

# Clean previous build artifacts
echo ""
echo "[1/4] Cleaning previous build..."
rm -rf "$SRC_DIR/bin/Release" 2>/dev/null || true

# Restore packages
echo "[2/4] Restoring packages..."
if ! dotnet restore "$SRC_DIR" --runtime linux-x64 -q; then
    echo "ERROR: Package restore failed."
    exit 1
fi

# Prepare native .so for embedding
EMBED_DIR="$SCRIPT_DIR/build/embedded"
rm -rf "$EMBED_DIR"
mkdir -p "$EMBED_DIR"

NUGET_DIR="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
for lib_spec in \
    "libSkiaSharp.so:skiasharp.nativeassets.linux:runtimes/linux-x64/native/libSkiaSharp.so" \
    "libHarfBuzzSharp.so:harfbuzzsharp.nativeassets.linux:runtimes/linux-x64/native/libHarfBuzzSharp.so"; do
    IFS=':' read -r lib_name pkg_name pkg_path <<< "$lib_spec"
    # Find the latest version directory for this package
    pkg_dir=$(find "$NUGET_DIR/$pkg_name" -maxdepth 1 -mindepth 1 -type d 2>/dev/null | sort -V | tail -1)
    if [[ -n "$pkg_dir" && -f "$pkg_dir/$pkg_path" ]]; then
        cp "$pkg_dir/$pkg_path" "$EMBED_DIR/$lib_name"
        strip --strip-unneeded "$EMBED_DIR/$lib_name" 2>/dev/null || true
        echo "  Embedded $lib_name: $(du -sh "$EMBED_DIR/$lib_name" | cut -f1) (stripped)"
    fi
done

# Publish as native AOT
echo "[3/4] Compiling native AOT binary (this may take a minute)..."
dotnet publish "$SRC_DIR" -c Release --no-restore 2>&1 | grep -v "^.*error : Deleting file" || true

# Verify the binary was produced
if [[ ! -f "$PUBLISH_DIR/ghelper" ]]; then
    echo ""
    echo "ERROR: Build failed — binary not found at $PUBLISH_DIR/ghelper"
    echo "Run 'dotnet publish src/ -c Release' manually to see full errors."
    exit 1
fi

# Copy to dist/
echo "[4/4] Copying to dist/..."
rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR"

cp "$PUBLISH_DIR/ghelper" "$DIST_DIR/"
chmod +x "$DIST_DIR/ghelper"

# UPX compression on main binary (native .so are embedded, UPX compresses everything)
if command -v upx &>/dev/null; then
    echo "[5/5] Compressing with UPX..."
    upx --best --lzma "$DIST_DIR/ghelper" 2>&1 | tail -1 || true
else
    echo ""
    echo "NOTE: upx not found — binary will not be compressed."
    echo "  Install with: sudo apt install upx-ucl"
fi

# Clean wlr-randr build artifacts from vendor dir (binary is embedded in ghelper)
if [[ -n "$WLR_RANDR_BIN" ]]; then
    rm -f "$WLR_RANDR_DIR/wlr-randr" \
          "$WLR_RANDR_DIR/wlr-output-management-unstable-v1-client-protocol.h" \
          "$WLR_RANDR_DIR/wlr-output-management-unstable-v1-protocol.c"
fi

# Clean gpu-helper build artifact from vendor dir (binary is embedded in ghelper)
if [[ -n "$GPU_HELPER_BIN" ]]; then
    rm -f "$GPU_HELPER_DIR/gpu-helper"
fi

# Summary
BINARY_SIZE=$(du -sh "$DIST_DIR/ghelper" | cut -f1)
TOTAL_SIZE=$(du -sh "$DIST_DIR" | cut -f1)
FILE_COUNT=$(find "$DIST_DIR" -mindepth 1 -maxdepth 1 | wc -l)

echo ""
echo "=== Build Complete ==="
echo "  Binary:  $BINARY_SIZE  (ghelper)"
echo "  Total:   $TOTAL_SIZE  ($FILE_COUNT files)"
echo "  Output:  $DIST_DIR/"
echo ""
echo "Run it:"
echo "  $DIST_DIR/ghelper"
