#!/bin/sh
# Builds LAME (the MP3 encoder, LGPL) as a separate, replaceable library for the Mac app, from LAME's own source
# (fetched by Homebrew: `brew fetch -s lame`). No MP3 decoder, so mpg123 isn't needed. Runs on macOS 13 and newer.
# Result: ~/Library/Caches/ChurchApps-lame/lib/libmp3lame.0.dylib (built once, reused).
set -e
DEST="$HOME/Library/Caches/ChurchApps-lame"
[ -f "$DEST/lib/libmp3lame.0.dylib" ] && exit 0
SRC=$(ls "$HOME"/Library/Caches/Homebrew/downloads/*--lame-*.tar.gz 2>/dev/null | grep -v bottle | tail -1)
[ -z "$SRC" ] && { brew fetch -s lame >/dev/null; SRC=$(ls "$HOME"/Library/Caches/Homebrew/downloads/*--lame-*.tar.gz | grep -v bottle | tail -1); }
mkdir -p "$DEST"; cp "$SRC" "$DEST/lame-source.tar.gz"       # kept: the source we ship with the app's credits
W=$(mktemp -d); tar -xzf "$SRC" -C "$W"; cd "$W"/lame-*
./configure --quiet --prefix="$DEST" --enable-shared --disable-static --disable-decoder --disable-frontend \
  PKG_CONFIG=/usr/bin/true CFLAGS="-O2 -arch arm64 -mmacosx-version-min=13.0" LDFLAGS="-arch arm64 -mmacosx-version-min=13.0"
make -s -j8 >/dev/null && make -s install >/dev/null
install_name_tool -id @rpath/libmp3lame.0.dylib "$DEST/lib/libmp3lame.0.dylib"
rm -rf "$W"
