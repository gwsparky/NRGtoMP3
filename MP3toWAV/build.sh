#!/bin/zsh
# Builds "MP3 to WAV.app" next to this script.
set -e
cd "$(dirname "$0")"
APP="MP3 to WAV.app"
DEST="$PWD/$APP"
BUILD="$(mktemp -d)"
cp main.swift icon.swift "$BUILD/"
cd "$BUILD"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
swift icon.swift
iconutil -c icns AppIcon.iconset -o "$APP/Contents/Resources/AppIcon.icns"
cp AppIcon.iconset/icon_512x512@2x.png "$DEST/../AppIcon.png" 2>/dev/null || true
swiftc -O -parse-as-library main.swift -o "$APP/Contents/MacOS/MP3toWAV"
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>MP3 to WAV</string>
  <key>CFBundleIdentifier</key><string>local.mp3towav</string>
  <key>CFBundleExecutable</key><string>MP3toWAV</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
EOF
xattr -cr "$APP"
codesign --force --sign - "$APP"
rm -rf "$DEST"
ditto --noextattr "$APP" "$DEST"
echo "Built $DEST"
