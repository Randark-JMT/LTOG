#!/bin/bash
# LTOG one-time setup. Run inside an MSYS2 MINGW64 shell:
#   ./scripts/setup.sh
#
# Installs build dependencies, stages WinFsp developer files into build/wfsp
# (a path without spaces, which autotools needs), applies the WinFsp port
# patch series to the LTFS submodule, and runs autoreconf + configure.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/third_party/ltfs/ltfs"

if [ "${MSYSTEM:-}" != "MINGW64" ]; then
    echo "error: run this from an MSYS2 MINGW64 shell (not MSYS/UCRT64/cmd)" >&2
    exit 1
fi

if [ ! -f "$SRC/configure.ac" ]; then
    echo "error: LTFS submodule is empty - run: git submodule update --init" >&2
    exit 1
fi

echo "==> Installing build dependencies (pacman)"
pacman -S --needed --noconfirm \
    git autoconf automake libtool make pkgconf \
    mingw-w64-x86_64-toolchain mingw-w64-x86_64-libxml2 \
    mingw-w64-x86_64-icu mingw-w64-x86_64-pkgconf

echo "==> Staging WinFsp developer files into build/wfsp"
WINFSP="${WINFSP:-/c/Program Files (x86)/WinFsp}"
if [ ! -f "$WINFSP/lib/winfsp-x64.lib" ]; then
    echo "error: WinFsp developer files not found at: $WINFSP" >&2
    echo "Install WinFsp (https://winfsp.dev) and select the 'Developer' feature," >&2
    echo "or point the WINFSP environment variable at the install directory." >&2
    exit 1
fi
mkdir -p "$ROOT/build/wfsp/lib" "$ROOT/build/wfsp/bin"
cp -r "$WINFSP/inc" "$ROOT/build/wfsp/"
# MSVC import library; GNU ld links it fine under a MinGW-style name
cp "$WINFSP/lib/winfsp-x64.lib" "$ROOT/build/wfsp/lib/libwinfsp-x64.a"
cp "$WINFSP/bin/winfsp-x64.dll" "$ROOT/build/wfsp/bin/"

echo "==> Applying WinFsp port patches to the LTFS submodule"
cd "$ROOT/third_party/ltfs"
# The LTFS submodule is committed with CRLF line endings, so EVERY fresh clone
# (any OS, any core.autocrlf setting — git blobs are identical everywhere) lands
# a CRLF worktree. The patch series and the autotools shell scripts the build
# runs are LF, so without this `git apply` fails ("patch does not apply") and the
# build is impossible from a clean checkout. Force every tracked *text* file to
# LF up front. This is idempotent — already-LF files are untouched — so it is
# safe on every invocation. (A plain `git checkout` cannot do this: it would just
# re-materialise the CRLF blobs.)
git config core.autocrlf false
echo "    normalizing submodule worktree to LF"
git ls-files -z \
    | perl -0 -ne 'chomp; next if /\.(png|jpe?g|gif|ico|bmp|pdf|dll|exe|lib|a|o|lo|so|dylib|zip|gz|tgz|tar|bz2|xz|7z|dat|mo|gmo|jar|class|ttf|otf|woff2?)$/i; print "$_\0"' \
    | xargs -0 --no-run-if-empty perl -i -pe 's/\r\n/\n/g'
for p in "$ROOT"/patches/*.patch; do
    if git apply --reverse --check "$p" 2>/dev/null; then
        echo "    already applied: $(basename "$p")"
        continue
    fi
    git apply --whitespace=nowarn "$p"
    echo "    applied: $(basename "$p")"
done

echo "==> autoreconf (regenerating 2012-era autotools files)"
cd "$SRC"
autoreconf -fi 2>&1 | grep -v 'warning\|obsolete\|expanded from\|the top level\|^\.\./\|^aclocal\|^configure\.ac' || true

echo "==> configure"
export PKG_CONFIG_PATH="$ROOT/pc:${PKG_CONFIG_PATH:-}"
# -D_FILE_OFFSET_BITS=64 MUST be a command-line define so it is set before any
# system header in every TU: modern MinGW-w64 then types off_t as off64_t
# (64-bit). Without it off_t is a 32-bit long on Win64 and all file I/O caps at
# 2 GiB (offset 0x80000000 wraps negative). win_util.h's late "#define
# _FILE_OFFSET_BITS 64" lands after <sys/types.h> and is too late to matter.
#
# HPE StoreOpen 3.4.2 renamed the Windows build macro HP_mingw_BUILD ->
# HPE_mingw_BUILD; we define BOTH so the upstream 3.4.2 code (HPE_) and our
# WinFsp port's guards (HP_) are all active.
./configure --host=x86_64-w64-mingw32 --build=x86_64-w64-mingw32 \
    CFLAGS="-Dmingw_PLATFORM=1 -DHP_mingw_BUILD=1 -DHPE_mingw_BUILD=1 -D_FILE_OFFSET_BITS=64"

echo
echo "Setup complete. Build with: ./scripts/build.sh"
