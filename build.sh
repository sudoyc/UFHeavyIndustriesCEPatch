#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RELEASE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)/release/UFHeavyIndustries_CE"

# ── 编译 ────────────────────────────────────────────────────────────────────
echo "[build] Compiling C# project..."
dotnet build "$SCRIPT_DIR/Source/UFHeavyIndustries_CE.csproj" \
    --configuration Release \
    --nologo \
    -v quiet

# ── 清理旧 release ──────────────────────────────────────────────────────────
echo "[build] Cleaning release directory..."
rm -rf "$RELEASE_DIR"
mkdir -p "$RELEASE_DIR"

# ── 拷贝 mod 文件 ────────────────────────────────────────────────────────────
echo "[build] Copying mod files..."

cp -r "$SCRIPT_DIR/About"      "$RELEASE_DIR/About"
cp -r "$SCRIPT_DIR/Assemblies" "$RELEASE_DIR/Assemblies"
cp -r "$SCRIPT_DIR/Defs"       "$RELEASE_DIR/Defs"
cp -r "$SCRIPT_DIR/Patches"    "$RELEASE_DIR/Patches"

# README 可选（存在则拷贝）
if [[ -f "$SCRIPT_DIR/README.md" ]]; then
    cp "$SCRIPT_DIR/README.md" "$RELEASE_DIR/README.md"
fi

# ── 完成 ─────────────────────────────────────────────────────────────────────
echo "[build] Done → $RELEASE_DIR"
echo ""
find "$RELEASE_DIR" -type f | sort | sed "s|$RELEASE_DIR/||"
