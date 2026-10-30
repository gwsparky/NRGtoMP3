#!/bin/zsh
# Builds the Windows 10/11 version from this Mac, as single self-contained .exe files
# (nothing else needs installing on the Windows PC):
#   dist/windows/ChurchSound.exe          for ordinary (Intel/AMD) PCs
#   dist/windows/ChurchSound (ARM).exe    for Windows on ARM (e.g. a Windows VM on an Apple silicon Mac)
set -e
cd "$(dirname "$0")"
mkdir -p ../dist/windows
for rid name in win-x64 "ChurchSound.exe" win-arm64 "ChurchSound (ARM).exe"; do
  dotnet publish NrgToMp3Win.csproj -c Release -r $rid -o ../dist/windows/publish-$rid
  cp "../dist/windows/publish-$rid/ChurchSound.exe" "../dist/windows/$name"
  rm -rf ../dist/windows/publish-$rid
  echo "Built dist/windows/$name ($(du -h "../dist/windows/$name" | cut -f1 | tr -d ' '))"
done
