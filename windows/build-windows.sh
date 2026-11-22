#!/bin/zsh
# Builds the Windows 10/11 version from this Mac. Each is a folder (nothing else needs installing on the PC):
# the .exe, plus a few separate parts and a Licenses folder that must stay next to it (licence rules for selling).
#   dist/windows/ChurchSound/           and  ChurchSound (Windows).zip        for ordinary (Intel/AMD) PCs
#   dist/windows/ChurchSound (ARM)/     and  ChurchSound (Windows ARM).zip    for Windows on ARM (e.g. a VM on an Apple silicon Mac)
set -e
cd "$(dirname "$0")"
mkdir -p ../dist/windows
for rid folder zip in win-x64 "ChurchSound" "ChurchSound (Windows).zip" win-arm64 "ChurchSound (ARM)" "ChurchSound (Windows ARM).zip"; do
  rm -rf "../dist/windows/$folder" "../dist/windows/$zip"
  dotnet publish NrgToMp3Win.csproj -c Release -r $rid -o "../dist/windows/$folder"
  rm -f "../dist/windows/$folder"/*.pdb
  (cd ../dist/windows && ditto -c -k --keepParent "$folder" "$zip")
  echo "Built dist/windows/$folder and $zip ($(du -sh "../dist/windows/$folder" | cut -f1 | tr -d ' '))"
done
