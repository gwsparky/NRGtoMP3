#!/bin/zsh
# Builds "dist/ChurchSound.app" (SwiftUI front end + self-contained .NET converter + MP3 encoder)
# and "dist/ChurchSound Installer.dmg" for installing it on another Mac.
set -e
cd "$(dirname "$0")"
# Assemble and sign outside the Desktop: iCloud Drive keeps adding Finder info there, which codesign rejects.
WORK=$(mktemp -d)
APP="$WORK/ChurchSound.app"
BUILD=$(tr -d '[:space:]' < BUILD)   # the build number, shared with the Windows app
dotnet publish NrgToMp3.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o dist/cli
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
clang -O2 -c mac/cdread.c -o "$WORK/cdread.o"
swiftc -O -parse-as-library -import-objc-header mac/cdread.h "$WORK/cdread.o" -target arm64-apple-macos13 mac/NrgToMp3App.swift mac/BurnList.swift mac/Lookup.swift mac/CDCopy.swift mac/SplitEditor.swift mac/SplitTab.swift mac/KeyChanger.swift mac/PlayerTab.swift mac/NamesSync.swift mac/AudioCD.swift mac/WaveformTab.swift -framework DiscRecording -framework DiscRecordingUI -o "$APP/Contents/MacOS/ChurchSound"
cp dist/cli/NrgToMp3 "$APP/Contents/Resources/"
# MP3 encoder with LAME built in, so MP3 export works on Macs without Homebrew/ffmpeg
clang -O2 -I/opt/homebrew/opt/lame/include mac/mp3enc.c /opt/homebrew/opt/lame/lib/libmp3lame.a /opt/homebrew/opt/mpg123/lib/libmpg123.a -o "$APP/Contents/Resources/mp3enc"
# App icon: mac/icon.png (made from Logo.png, cropped to the heart and cross), scale to every size macOS wants, pack into .icns
ICONSET=$(mktemp -d)/AppIcon.iconset; mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s mac/icon.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) mac/icon.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>ChurchSound</string>
  <key>CFBundleExecutable</key><string>ChurchSound</string>
  <key>CFBundleIdentifier</key><string>local.nrgtomp3</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>$BUILD</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
xattr -cr "$APP"
codesign --force --deep -s - "$APP"
rm -rf "dist/ChurchSound.app"
ditto "$APP" "dist/ChurchSound.app"
codesign -v "dist/ChurchSound.app"
echo "Built dist/ChurchSound.app (build $BUILD)"

# Installer: a disk image with the app, a shortcut to Applications to drag it onto, and the install guide
DMG="dist/ChurchSound Installer.dmg"
mkdir -p "$WORK/dmg"
ditto "$APP" "$WORK/dmg/ChurchSound.app"
ln -s /Applications "$WORK/dmg/Applications"
cp "mac/How to Install.txt" "$WORK/dmg/"
hdiutil create -quiet -volname "ChurchSound" -srcfolder "$WORK/dmg" -format UDZO -ov "$WORK/installer.dmg"
hdiutil verify -quiet "$WORK/installer.dmg"
rm -f "$DMG"
ditto "$WORK/installer.dmg" "$DMG"
rm -rf "$WORK"
echo "Built $DMG ($(du -h "$DMG" | cut -f1 | tr -d ' '))"
