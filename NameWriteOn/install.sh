#!/bin/zsh
# Copies the title into Final Cut Pro's Titles browser (category "Handwritten").
set -e
cd "$(dirname "$0")"
DEST="$HOME/Movies/Motion Templates.localized/Titles.localized/Handwritten/Name Write-On"
mkdir -p "$DEST"
cp "Name Write-On.moti" large.png small.png "$DEST/"
echo "Installed to $DEST — restart Final Cut Pro to see it."
