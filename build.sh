#!/usr/bin/env bash
#
# Builds the FinTube Jellyfin plugin and the companion browser extensions,
# then packages them as release archives.
#
# Output:
#   dist/FinTube-<version>.zip                        (DLL + meta.json + logo.png)
#   browser-extensions/fintube-extension-firefox.xpi  (Firefox/Gecko build)
#   browser-extensions/fintube-extension-chrome.zip   (Chrome/Chromium build)
#
# The packaged extensions are committed to the repo so users can download and
# install them directly without cloning or building. Re-run this after changing
# anything under browser-extensions/firefox or .../chrome, then commit the
# updated archives.
#
# Also prints the MD5 checksum used in manifest.json.
#
set -euo pipefail

cd "$(dirname "$0")"

PROJECT="Jellyfin.Plugin.FinTube/Jellyfin.Plugin.FinTube.csproj"
CONFIG="Release"
FRAMEWORK="net8.0"
OUTDIR="Jellyfin.Plugin.FinTube/bin/${CONFIG}/${FRAMEWORK}"
DIST="dist"
EXTDIR="browser-extensions"

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

# ---- Browser extensions ----
#
# Each extension is a folder of plain HTML/CSS/JS - no compile step, just zip its
# contents so manifest.json sits at the archive root. Firefox wants a .xpi,
# Chromium wants a .zip; both are the same kind of archive.

# Read the shared extension version from the Firefox manifest.
EXT_VERSION="$(grep -oP '(?<="version": ")[^"]+' "$EXTDIR/firefox/manifest.json" | head -n1)"
if [ -z "${EXT_VERSION:-}" ]; then
    echo "error: could not read \"version\" from $EXTDIR/firefox/manifest.json" >&2
    exit 1
fi

# package_extension <source-subdir> <output-archive>
package_extension() {
    local src="$1" out="$2"
    rm -f "$out"
    (
        cd "$EXTDIR/$src"
        # Bundle everything except docs and OS cruft. -X strips extra metadata
        # so the archive is reproducible.
        zip -q -r -X "$OLDPWD/$out" . -x "README.md" "*.DS_Store"
    )
}

echo ">> Packaging browser extensions v${EXT_VERSION}"
# Stable, version-less names: the committed archive updates in place instead of
# accumulating one file per version in git history.
FF_XPI="$EXTDIR/fintube-extension-firefox.xpi"
CR_ZIP="$EXTDIR/fintube-extension-chrome.zip"
package_extension "firefox" "$FF_XPI"
package_extension "chrome" "$CR_ZIP"

echo
echo ">> Done"
echo "   Plugin    : $ZIP"
echo "   Version   : $VERSION"
echo "   MD5       : $CHECKSUM"
echo "   Firefox   : $FF_XPI"
echo "   Chrome    : $CR_ZIP"
echo "   Ext. ver. : $EXT_VERSION"
