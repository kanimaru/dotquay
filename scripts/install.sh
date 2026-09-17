#!/bin/sh
# dotquay installer (v0.17). Downloads the newest release for this machine, verifies it against the
# checksum the release publishes, and puts it in ~/.local/bin.
#
# The install location is not a detail: it is the same place `dotquay self update` expects to be able
# to write, so anything installed this way can update itself afterwards without a privilege prompt.
#
# Usage:
#   DOTQUAY_TOKEN=<gitlab token> sh install.sh [--version 0.17.0] [--dir ~/.local/bin]
#
# A token is needed while dotquay's release project is private. Any GitLab personal access token that
# can read that project's releases will do - the same one `dotquay login gitlab.kemomi.de` stores.

set -eu

API="${DOTQUAY_UPDATE_URL:-https://gitlab.kemomi.de/api/v4/projects/128}"
DIR="${DOTQUAY_INSTALL_DIR:-$HOME/.local/bin}"
TOKEN="${DOTQUAY_TOKEN:-}"
VERSION=""

while [ $# -gt 0 ]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --dir) DIR="$2"; shift 2 ;;
    --token) TOKEN="$2"; shift 2 ;;
    --channel) API="$2"; shift 2 ;;
    -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

fail() { echo "install.sh: $*" >&2; exit 1; }

command -v curl >/dev/null 2>&1 || fail "curl is required."

# Whichever this platform ships. macOS has shasum, Linux has sha256sum, and a checksum that cannot be
# computed is a checksum that cannot be checked - so this is a hard requirement, not a warning.
if command -v sha256sum >/dev/null 2>&1; then
  checksum() { sha256sum "$1" | cut -d' ' -f1; }
elif command -v shasum >/dev/null 2>&1; then
  checksum() { shasum -a 256 "$1" | cut -d' ' -f1; }
else
  fail "neither sha256sum nor shasum is available, so a download cannot be verified."
fi

case "$(uname -s)" in
  Linux) OS=linux ;;
  Darwin) OS=osx ;;
  *) fail "unsupported operating system: $(uname -s). Build from source instead." ;;
esac

case "$(uname -m)" in
  x86_64|amd64) ARCH=x64 ;;
  arm64|aarch64) ARCH=arm64 ;;
  *) fail "unsupported architecture: $(uname -m)." ;;
esac

RID="$OS-$ARCH"

fetch() {
  if [ -n "$TOKEN" ]; then
    curl --fail --silent --show-error --location --header "PRIVATE-TOKEN: $TOKEN" "$@"
  else
    curl --fail --silent --show-error --location "$@"
  fi
}

if [ -n "$VERSION" ]; then
  RELEASE_URL="$API/releases/v$VERSION"
else
  RELEASE_URL="$API/releases?per_page=1"
fi

echo "looking up the newest dotquay for $RID"
RELEASE=$(fetch "$RELEASE_URL") || fail "could not reach $API. If that project is private, pass --token."

# Each links entry is a flat object, so splitting on '{' gives one entry per line and the first
# "url" in it is the asset link (GitLab emits url before direct_asset_url).
MANIFEST_URL=$(printf '%s' "$RELEASE" | tr '{' '\n' | grep '"name":"manifest.json"' | grep -o '"url":"[^"]*"' | head -1 | cut -d'"' -f4)
[ -n "$MANIFEST_URL" ] || fail "that release publishes no manifest.json, so there is nothing to verify a download against. It predates v0.17 - download it by hand."

MANIFEST=$(fetch "$MANIFEST_URL") || fail "could not read the release manifest."

ASSET=$(printf '%s' "$MANIFEST" | tr '{' '\n' | grep "\"rid\":\"$RID\"") \
  || fail "that release publishes no build for $RID."

URL=$(printf '%s' "$ASSET" | grep -o '"url":"[^"]*"' | head -1 | cut -d'"' -f4)
WANT=$(printf '%s' "$ASSET" | grep -o '"sha256":"[^"]*"' | head -1 | cut -d'"' -f4)
VERSION=$(printf '%s' "$MANIFEST" | grep -o '"version":"[^"]*"' | head -1 | cut -d'"' -f4)

[ -n "$URL" ] || fail "the manifest names no download URL for $RID."
[ -n "$WANT" ] || fail "the manifest publishes no checksum for $RID. Refusing to install something that cannot be verified."

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

echo "downloading dotquay $VERSION"
fetch --output "$TMP/dotquay" "$URL" || fail "the download failed."

GOT=$(checksum "$TMP/dotquay")
if [ "$GOT" != "$WANT" ]; then
  fail "checksum mismatch - nothing was installed.
  expected $WANT
  got      $GOT"
fi

chmod +x "$TMP/dotquay"

# Run it before installing it, for the same reason `dotquay self update` does: a binary that hashes
# correctly can still be one the trimmer broke, and the only test that catches that is starting it.
REPORTED=$("$TMP/dotquay" --version 2>/dev/null | head -1 || true)
[ -n "$REPORTED" ] || fail "the downloaded binary does not start on this machine. Nothing was installed."

mkdir -p "$DIR"
mv "$TMP/dotquay" "$DIR/dotquay"

echo "installed dotquay $REPORTED to $DIR/dotquay"

case ":$PATH:" in
  *":$DIR:"*) ;;
  *) echo "note: $DIR is not on your PATH. Add it to your shell profile:"
     echo "  export PATH=\"$DIR:\$PATH\"" ;;
esac

echo "from here on, 'dotquay self update' keeps it current."
if [ -n "$TOKEN" ]; then
  echo "run 'dotquay login gitlab.kemomi.de' once so it can reach the release channel on its own."
fi
