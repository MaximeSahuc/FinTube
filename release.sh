#!/usr/bin/env bash
#
# Cuts a new FinTube release:
#   1. Bumps the version in the csproj and Assets/meta.json.
#   2. Builds + packages the plugin (delegates to build.sh) -> dist/FinTube-<version>.zip.
#   3. Updates manifest.json with a new version entry (sourceUrl + md5 checksum).
#   4. Commits everything, tags v<version>, pushes, and creates the GitHub release
#      with the zip the new manifest points at.
#
# Usage:
#   ./release.sh [<version>] [-m "<changelog>"] [--dry-run]
#
#   <version>     e.g. 1.1.0.10. If omitted, the last manifest version's final
#                 component is incremented automatically.
#   -m <text>     Changelog for this release (default: a generic message).
#   --dry-run     Do everything locally (bump, build, manifest) and print the git
#                 and gh commands that WOULD run, but make no commit / tag / push /
#                 release, and restore the version-bumped files afterwards so the
#                 working tree is left untouched.
#
# Requires: dotnet, zip, git, gh (authenticated), md5sum, python3.
#
set -euo pipefail

cd "$(dirname "$0")"
REPO_ROOT="$(pwd)"

PROJECT="Jellyfin.Plugin.FinTube/Jellyfin.Plugin.FinTube.csproj"
META="Assets/meta.json"
MANIFEST="manifest.json"
DIST="dist"
GH_REPO="MaximeSahuc/FinTube"

DRY_RUN=0
NEW_VERSION=""
CHANGELOG=""

# ---- arg parsing ----------------------------------------------------------
while [ $# -gt 0 ]; do
    case "$1" in
        --dry-run) DRY_RUN=1; shift ;;
        -m|--message) CHANGELOG="${2:-}"; shift 2 ;;
        -h|--help) grep '^#' "$0" | sed 's/^# \?//'; exit 0 ;;
        -*) echo "error: unknown option '$1'" >&2; exit 1 ;;
        *) if [ -z "$NEW_VERSION" ]; then NEW_VERSION="$1"; shift
           else echo "error: unexpected argument '$1'" >&2; exit 1; fi ;;
    esac
done

run() {
    # Echo and run, or just echo when in dry-run mode.
    if [ "$DRY_RUN" -eq 1 ]; then
        echo "   [dry-run] $*"
    else
        echo "   + $*"
        "$@"
    fi
}

# ---- preflight ------------------------------------------------------------
for tool in git gh md5sum python3 zip; do
    command -v "$tool" >/dev/null 2>&1 || { echo "error: '$tool' not found" >&2; exit 1; }
done

if [ "$DRY_RUN" -eq 0 ]; then
    gh auth status >/dev/null 2>&1 || { echo "error: gh is not authenticated (run: gh auth login)" >&2; exit 1; }
fi

# Read the most recent manifest version and its targetAbi to stay consistent.
LAST_VERSION="$(python3 -c "import json;d=json.load(open('$MANIFEST'));print(d[0]['versions'][0]['version'])")"
TARGET_ABI="$(python3 -c "import json;d=json.load(open('$MANIFEST'));print(d[0]['versions'][0]['targetAbi'])")"

# Default new version: bump the final dotted component of the last one.
if [ -z "$NEW_VERSION" ]; then
    NEW_VERSION="$(python3 -c "v='$LAST_VERSION'.split('.');v[-1]=str(int(v[-1])+1);print('.'.join(v))")"
fi

# Validate version shape (Jellyfin uses System.Version: up to four numbers).
echo "$NEW_VERSION" | grep -Eq '^[0-9]+(\.[0-9]+){1,3}$' \
    || { echo "error: invalid version '$NEW_VERSION'" >&2; exit 1; }

if [ -z "$CHANGELOG" ]; then
    CHANGELOG="Release v$NEW_VERSION"
fi

TAG="v$NEW_VERSION"
ZIP="$DIST/FinTube-${NEW_VERSION}.zip"
SOURCE_URL="https://github.com/${GH_REPO}/releases/download/${TAG}/FinTube-${NEW_VERSION}.zip"
TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

if git rev-parse "$TAG" >/dev/null 2>&1; then
    echo "error: tag $TAG already exists locally" >&2; exit 1
fi
if gh release view "$TAG" -R "$GH_REPO" >/dev/null 2>&1; then
    echo "error: release $TAG already exists on $GH_REPO" >&2; exit 1
fi

echo ">> Releasing FinTube $TAG (previous: v$LAST_VERSION, targetAbi $TARGET_ABI)"
echo "   Changelog: $CHANGELOG"
[ "$DRY_RUN" -eq 1 ] && echo "   *** DRY RUN - no commit / tag / push / release ***"

# Files we mutate; used to restore the tree after a dry run.
BUMPED_FILES=("$PROJECT" "$META" "$MANIFEST")

# ---- 1. bump version in csproj + meta.json --------------------------------
echo ">> Bumping version to $NEW_VERSION"
python3 - "$PROJECT" "$NEW_VERSION" <<'PY'
import re, sys
path, version = sys.argv[1], sys.argv[2]
s = open(path).read()
s2 = re.sub(r'<Version>[^<]+</Version>', f'<Version>{version}</Version>', s, count=1)
if s == s2:
    sys.exit(f"error: <Version> not found in {path}")
open(path, 'w').write(s2)
PY

python3 - "$META" "$NEW_VERSION" "$CHANGELOG" "$TIMESTAMP" <<'PY'
import json, sys
path, version, changelog, ts = sys.argv[1:5]
d = json.load(open(path))
d['version'] = version
d['changelog'] = changelog
d['timestamp'] = ts
json.dump(d, open(path, 'w'), indent=2)
open(path, 'a').write('\n')
PY

# ---- 2. build + package ---------------------------------------------------
echo ">> Building & packaging"
./build.sh >/dev/null
[ -f "$ZIP" ] || { echo "error: expected artifact $ZIP was not produced" >&2; exit 1; }
CHECKSUM="$(md5sum "$ZIP" | awk '{print $1}')"
echo "   Artifact : $ZIP"
echo "   MD5      : $CHECKSUM"

# ---- 3. update manifest.json ----------------------------------------------
echo ">> Updating $MANIFEST"
python3 - "$MANIFEST" "$NEW_VERSION" "$CHANGELOG" "$TARGET_ABI" "$SOURCE_URL" "$CHECKSUM" "$TIMESTAMP" <<'PY'
import json, sys
path, version, changelog, abi, url, checksum, ts = sys.argv[1:8]
d = json.load(open(path))
entry = {
    "version": version,
    "changelog": changelog,
    "targetAbi": abi,
    "sourceUrl": url,
    "checksum": checksum,
    "timestamp": ts,
}
# Newest first; drop any pre-existing entry with the same version.
versions = [v for v in d[0].get("versions", []) if v.get("version") != version]
d[0]["versions"] = [entry] + versions
json.dump(d, open(path, 'w'), indent=2)
open(path, 'a').write('\n')
PY

echo ">> New manifest entry:"
python3 -c "import json;print(json.dumps(json.load(open('$MANIFEST'))[0]['versions'][0], indent=2))" | sed 's/^/     /'

# ---- 4. commit, tag, push, release ----------------------------------------
echo ">> Publishing"
run git add -A
run git commit -m "$TAG"
run git tag -a "$TAG" -m "$TAG"
run git push origin HEAD
run git push origin "$TAG"
run gh release create "$TAG" "$ZIP" -R "$GH_REPO" --title "$TAG" --notes "$CHANGELOG"

if [ "$DRY_RUN" -eq 1 ]; then
    echo ">> Restoring version-bumped files (dry run)"
    git checkout -- "${BUMPED_FILES[@]}"
fi

echo
echo ">> Done: $TAG"
echo "   Artifact : $ZIP"
echo "   MD5      : $CHECKSUM"
echo "   SourceUrl: $SOURCE_URL"
[ "$DRY_RUN" -eq 1 ] && echo "   (dry run - nothing was pushed or released)"
