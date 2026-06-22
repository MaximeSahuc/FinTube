#!/usr/bin/env bash
#
# Builds the FinTube Jellyfin plugin and packages it as a release zip.
#
# Output: dist/FinTube-<version>.zip  (DLL + meta.json + logo.png)
# Also prints the MD5 checksum used in manifest.json.
#
set -euo pipefail

cd "$(dirname "$0")"

PROJECT="Jellyfin.Plugin.FinTube/Jellyfin.Plugin.FinTube.csproj"
CONFIG="Release"
FRAMEWORK="net8.0"
OUTDIR="Jellyfin.Plugin.FinTube/bin/${CONFIG}/${FRAMEWORK}"
DIST="dist"

# Locate dotnet: prefer the local install in ~/.dotnet, fall back to PATH.
if [ -x "$HOME/.dotnet/dotnet" ]; then
    DOTNET="$HOME/.dotnet/dotnet"
elif command -v dotnet >/dev/null 2>&1; then
    DOTNET="$(command -v dotnet)"
else
    echo "error: dotnet not found (looked in ~/.dotnet and PATH)" >&2
    exit 1
fi

# Read <Version> from the csproj.
VERSION="$(grep -oP '(?<=<Version>)[^<]+' "$PROJECT" | head -n1)"
if [ -z "${VERSION:-}" ]; then
    echo "error: could not read <Version> from $PROJECT" >&2
    exit 1
fi

echo ">> Building FinTube v${VERSION} ($CONFIG) with $DOTNET"
"$DOTNET" build "$PROJECT" -c "$CONFIG" --nologo

echo ">> Packaging release"
mkdir -p "$DIST"
ZIP="$DIST/FinTube-${VERSION}.zip"
rm -f "$ZIP"

# Bundle the plugin DLL and the two assets Jellyfin expects alongside it.
(
    cd "$OUTDIR"
    zip -q -X "$OLDPWD/$ZIP" \
        Jellyfin.Plugin.FinTube.dll \
        meta.json \
        logo.png
)

CHECKSUM="$(md5sum "$ZIP" | awk '{print $1}')"

echo
echo ">> Done"
echo "   Package : $ZIP"
echo "   Version : $VERSION"
echo "   MD5     : $CHECKSUM"
