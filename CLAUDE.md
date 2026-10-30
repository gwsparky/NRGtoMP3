# ChurchSound (formerly NRG to MP3): project notes for Claude

A desktop app (Mac **and** Windows) for George's CD archive: it opens Nero `.nrg` and `.cue/.bin` disc images, previews and plays the songs,
and converts them to MP3/WAV. It also splits songs, changes their key, copies and burns CDs (Mac only so far), and keeps playlists.
George is not a programmer: explain in plain language, give him clear test steps, and build things for him rather than telling him to.

**GitHub (`gwsparky/NRGtoMP3`, branch `main`) is the master copy.** George works in Claude cloud sessions on this repo and
downloads the ZIP to build on his Mac. At the start of a session, check for other branches (`git ls-remote origin`) and ask
whether his Mac or PC has changes that aren't on GitHub yet; in October 2026 build 17 sat unnoticed on a side branch
(`windows-burn-copy`) while `main` was still build 13. Push finished work to `main` (he has asked for that) so the next
session and his downloads see it. Windows `.exe`s are built in the session and sent with SendUserFile; GitHub releases can't
be created from a cloud session.

## Layout

| Path | What it is | Builds on |
|---|---|---|
| `Nrg.cs`, `Program.cs`, `Wav.cs`, `Id3.cs` | **Shared converter**: reads `.nrg` (DAO/TAO, NER5/NERO footers, CD-Text), `.cue/.bin`, and "raw" images with no Nero track list (songs split at ≥5 s silences); exports MP3/WAV with ID3/INFO tags; `--merge`, `--list` (JSON), `--tracks`, `--meta names.json`. The Mac app runs it as a CLI; the Windows app calls `Program.Unpack` directly. | both |
| `NrgToMp3.csproj` | builds the shared converter as the Mac CLI (`net8.0`, excludes `windows/**`) | Mac |
| `mac/*.swift`, `mac/*.c` | SwiftUI Mac app (tabs: Convert, Player, Burn CD, Copy CD, Split Song, Change Key, Waveform Viewer; Player and Change Key also play audio CDs with their CD-Text names via `AudioCD.swift`) | Mac |
| `build-app.sh` | Mac build: app + `dist/ChurchSound Installer.dmg` | Mac |
| `windows/*.cs`, `windows/NrgToMp3Win.csproj` | WinForms Windows app (`net8.0-windows`, includes the shared files above) | Windows |
| `windows/build-windows.sh` | builds both Windows `.exe`s **from the Mac** (zsh); on Windows use the commands below | Mac |
| `MP3toWAV/`, `NameWriteOn/`, `mp3-to-wav.html` | George's separate projects. **Not part of this app; leave them alone.** | — |
| `bin/`, `obj/`, `dist/`, `windows/bin/`, `windows/obj/` | build output (safe to delete; rebuilt as needed) | — |

### Windows app files
- `App.cs`: entry point, main window and tabs, `Settings` (`%APPDATA%\NRG to MP3\settings.json`), `Dpi` scaling, background-colour bar. `MainForm.Build` is the build number shown in the title bar, read from the root **`BUILD`** file, which the Mac app shows too (via `CFBundleVersion` in `build-app.sh`). Bump `BUILD` with every build George tests, so the Mac and Windows numbers match (currently **30**).
- `Theme.cs`: optional background colour, applied to every control.
- `Disc.cs`: `DiscItem` (an opened image: songs, names, ticks, MusicBrainz TOC, names files) and `Audio.ReadSong` / `Audio.CombineSongs`. **Cross-platform**, so it can be tested on any OS.
- `ConvertTab.cs`: Convert tab (disc list, song grid, preview, Play All, merge + `MergeNamesForm`, names files, export).
- `Audio.cs`: `PreviewPlayer` (NAudio `WaveOutEvent`: queue/Play All, pause, repeat, volume) and MP3 encoding with Windows' Media Foundation encoder.
- `SongCore.cs`: cross-platform song tools: time parsing and lists, waveform peaks, removing a side, pitch shift (SoundTouch), `SplitPlan`, and `Export` (writes a temp CUE/BIN and runs `Program.Unpack`).
- `SongUI.cs`: `SongFiles.Decode` (Media Foundation), `SongPlayer` (live pitch), `WaveformView`, `SongOpener` (drop zone + song list with ticks / Combine Selected), `ExportBar`.
- `NamesSync.cs` (cross-platform GitHub up/download of names files) + `NamesSyncForm.cs` (`SyncSettings`, the "GitHub…" dialog; the key is stored with DPAPI via `Settings.SetSecret`).
- `SplitTab.cs` (+ `TimeListForm`), `KeyTab.cs`, `PlayerTab.cs`, `PlaylistCore.cs` (`.nrgplaylist` JSON, the same format as the Mac), `Lookup.cs` + `MusicBrainz.cs`.
- `BurnCore.cs`: **cross-platform** burn list (`BurnList.Shared`, used by every tab's "Add to Burn List"): gaps, capacity, CUE/BIN writing, per-track files for the burner, playlists.
- `Burner.cs`: burning with IMAPI2 via late-bound COM (`dynamic`) on an STA thread: `MsftRawCDImageCreator` + `MsftDiscFormat2RawCD` (disc-at-once, so exact/zero gaps). Each track file is the song plus the silence before the next one. Tries each raw sector type; if all fail (George's burner gave C0AA0602 "media not prepared" in builds 14–15), `BurnTab` offers `Burner.BurnStandard` (`MsftDiscFormat2TrackAtOnce`, always 2 s gaps).
- `BurnTab.cs`: Burn CD tab. ✂ raises `BurnTab.SplitRequested` → `SplitTab.OpenBurnItem`, whose parts replace the song.
- `CdReader.cs` (`IOCTL_CDROM_READ_TOC` / `IOCTL_CDROM_RAW_READ`, retries then sector-by-sector) + `CopyTab.cs`: Copy CD tab.
- `Install.cs`: the app installs itself ("Install on This PC…" button): copies the exe to `%LOCALAPPDATA%\Programs\ChurchSound`, Start-menu/desktop shortcuts, an uninstall entry in HKCU (`--uninstall`).

## Building

**From Linux (e.g. a Claude cloud session):** Ubuntu's `dotnet-sdk-8.0` lacks the Windows Desktop targets. Copy
`sdk/*/Sdks/Microsoft.NET.Sdk.WindowsDesktop` from Microsoft's package
(`packages.microsoft.com/ubuntu/22.04/prod/pool/main/d/dotnet-sdk-8.0/`, extract with `dpkg-deb -x`) into
`/usr/lib/dotnet/sdk/<version>/Sdks/`; then the `dotnet publish` commands below work unchanged.

### On Windows

Needs the **.NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`). From the project folder:

```powershell
dotnet build windows\NrgToMp3Win.csproj -c Debug -r win-x64     # quick build to test
dotnet run --project windows\NrgToMp3Win.csproj -r win-x64       # build and start it
# the single-file exe George copies around (self-contained, nothing to install):
dotnet publish windows\NrgToMp3Win.csproj -c Release -r win-x64 -o dist\windows\x64
dotnet publish windows\NrgToMp3Win.csproj -c Release -r win-arm64 -o dist\windows\arm64
```

`-r win-arm64` is for Windows on ARM (George's VMware VM on his Apple-silicon Mac). His PC is presumably x64: check with
`$env:PROCESSOR_ARCHITECTURE` before choosing.

## Status of the Windows version

**Confirmed working on Windows by George:** opening `.nrg` files, song preview, MP3 and WAV export, display scaling (after the fix below).

**Built but not yet tried on Windows**, so test these first and fix what breaks (build 14 added 6–8):
1. **Split Song:** open a song or an image track, waveform (L/R lanes, click to seek, drag red markers), Ctrl+P / Ctrl+K, "Split at" box, Enter Times…, parts grid (play, rename, nudge, typed start, 👂, ✕), Export Parts, ◀ Song List, Combine Selected.
2. **Change Key:** −/+ half steps (live pitch while playing), Original key → new key name, fine tune, New key / Original, Channels (remove a side; copy or silent) with the waveform following, Export, ◀ Song List, Combine Selected.
3. **Player:** add files / disc images / drag, play from a song, pause/resume, ⏮ ⏭ ⏹, Last 10 s, Repeat, volume, ▲▼ ✕, rename, Save/Open Playlist (also opening a playlist saved on the Mac).
4. **Convert extras:** Play All + ⏮ ⏭ + Last 10 s + highlighting; Check All / Uncheck All / Uncheck All Discs; Merge into one file → the "Name the Merged File" window (pre-filled with the first ticked song's name; Song Name / Album Name buttons); Save Names / Load Names… / automatic loading of `<image>.txt`; Look Up Names (MusicBrainz).
5. **Background colour:** every tab, the dialogs, and readable text on dark colours.
6. **Burn CD:** add files / disc images / playlists, gaps (default + per song + Apply to All Songs), Play All, ✂, Save Disc Image, Burn CD (needs a real burner + blank CD-R), the "Add to Burn List" buttons on Convert, Player, Split Song (replaces the song when opened with ✂) and Change Key.
7. **Copy CD:** a music CD is detected within ~3 s, preview, names / Look Up Names, Save Image, Stop, Eject, Open in Convert Tab, Add to Burn List.
8. **Install on This PC…** (top bar), then uninstall from Settings → Apps.

`BurnCore.cs` is tested headlessly (images written and read back with `Nrg.Read`: exact gaps, audio identical, playlists round-trip).

**Added in build 28 (from the Mac's builds 21–27), not yet tried on Windows:**
9. **Waveform Viewer tab** (`WaveTab.cs`): open an image or the CD, small waveform per track (click it to tick), ↕ ticks a run, joined waveform of the ticked tracks with orange track starts, Play Joined / click to play from a point, Apply Name ("Song (part 1 of 3)"), Save/Load Names, Look Up, Get from GitHub, Open in Convert Tab. No export, so Enter can't export.
10. **CDs as discs:** `DiscItem.FromCd` (CD-Text names via `CdReader.ReadCdText` → `IOCTL_CDROM_READ_TOC_EX` format 5, parsed by the cross-platform `CdText.cs`; songs read on demand by `DiscItem.ReadTrack`, last 3 cached). Used by **Player → Play CD**, **Change Key → Open CD** (`SongOpener.OpenCd`) and the Waveform Viewer.
11. **Burn CD:** Look Up Names… (`LookupForm` now takes any song count/TOC/apply callback; `BurnList.Toc()` is inexact), and adding an image loads its names file and then `names/<image>.txt` from GitHub. `DiscArtist` goes into the CUE as PERFORMER.

**Differences from the Mac still open:**
- Burn CD on Windows writes **no CD-Text** (song names on the disc) and has **no test burn** or speed choice. CD-Text with the raw burner needs R-W subcode packs (`IRawCDImageCreator.AddSubcodeRWGenerator`).
- Copy CD doesn't read CD-Text from the disc (names come from MusicBrainz or typing).
- Burning shows elapsed time, not a percentage (IMAPI2 progress events aren't hooked up).
- The Convert previews, Player and Burn CD tabs use separate players on Windows, so they can sound at once (the Mac shares one).

## The name

The app is called **ChurchSound** (renamed in build 30). Only what George sees changed. These keep the old name **on purpose**, so don't "tidy" them: the GitHub repo `gwsparky/NRGtoMP3`, project files and code namespaces (`NrgToMp3*`), the CLI tool name, the Mac bundle ID `local.nrgtomp3`, the Mac Keychain entry `NRG to MP3 GitHub` and the Windows settings folder `%APPDATA%\NRG to MP3` (changing the last three would lose his saved key and settings).

## Problems already found and fixed (don't undo)

- **Display scaling:** George's Windows runs at ~200%. Layouts are written for 96 DPI and `Dpi.Scale(form)` scales them on `HandleCreated`. Rows use `AutoSize` (not fixed heights). Every new `Form` must call `Theme.Apply(this); Dpi.Scale(this);` at the end of its constructor.
- **MP3 export "file is being used by another process":** `Audio.EncodeMp3` opens the temp PCM itself with `FileShare.Delete` and closes it before the converter deletes it.
- **Grid re-entrancy:** never rebuild a `DataGridView`'s rows inside its own `CellContentClick`/`CellEndEdit`; use `BeginInvoke(Rebuild)`.
- **Shared converter state:** `Program.outputDir/wav/merge/onlyTracks/meta` are static, so any export holds `SongCore.ConsoleLock` (also used to redirect `Console` to the log box).
- **File names** go through `Program.Clean`, which removes characters invalid on Windows **or** macOS (`< > : " / \ | ? *`), because files move between the two.
- **The `.nrg` byte order** is detected by sample smoothness (`Program.IsBigEndian`); CUE/BIN images are little-endian.

## File formats shared by both apps (keep them compatible)

- **Names file:** `<image name>.txt` next to the image, loaded automatically when it's opened. Lines: `Album: …`, `Artist: …`, `1. Song name` (also `1)`, `01 -`, `1 `), or unnumbered lines in order; `#` comments. Accept UTF-8 (with or without BOM) and Latin-1.
- **Playlist:** `.nrgplaylist` JSON: `{version, title, defaultGap, songs:[{title, gap?, note?, file? | image+track{number,title,performer,seconds,start,sectors,sectorSize,gap}+swap}]}`. Paths are absolute; when loading, also look for the same file name next to the playlist (and in its "<playlist> files" folder).
- **Names on GitHub** (both apps, Windows build 18): names files are also kept at `names/<image name>.txt` in a GitHub repo (default `gwsparky/NRGtoMP3`, set with **GitHub…**). Opening a disc downloads that file and, if it differs from the local `.txt`, applies it and overwrites the local file (GitHub wins). **Save Names** writes the local file, then uploads (needs a fine-grained token with Contents read/write; Mac: Keychain, Windows: DPAPI in settings.json). **Get from GitHub** re-downloads. **Copy CD** (both apps, build 19) also writes `<name>.txt` next to the CUE/BIN and uploads it. Code: `mac/NamesSync.swift`, `windows/NamesSync.cs`. Not yet tested on a real Mac/PC.
- **Merged-file names** default to the first ticked song's name; the user confirms or edits before anything is written.

## Working with George

- Every build he tests: bump the number in `BUILD` (both apps use it), rebuild **both** `win-x64` and `win-arm64` if he's using the VM too, and tell him exactly where the `.exe` is and what to try.
- Test what you can headlessly first. `Disc.cs`, `SongCore.cs`, `PlaylistCore.cs` and the shared converter run without a UI, so a small console project that includes them makes a good test harness.
- He reports problems with screenshots; ask for the exact error text.
