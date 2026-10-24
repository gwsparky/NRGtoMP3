import SwiftUI
import UniformTypeIdentifiers
import AppKit

/// A music player for songs from audio files and disc images, with playlists that can be saved and opened.
/// Its song list uses the same model as the Burn CD list (adding files and images, playlists), so playlists
/// open in either tab.
struct PlayerTab: View {
    @ObservedObject var list: BurnList
    @ObservedObject var burn: BurnList
    @ObservedObject var player: Previewer
    let showBurnList: () -> Void
    @State private var targeted = false
    @State private var selection = Set<UUID>()

    var playingIndex: Int? { list.items.firstIndex { $0.id.uuidString == player.playingID } }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            nowPlaying
            HStack {
                Text("Playlist").font(.headline)
                TextField("Name (optional)", text: $list.discTitle).frame(maxWidth: 260)
                Spacer()
                Button("Open Playlist…") { openPlaylist() }
                Button("Save Playlist…") { savePlaylist() }.disabled(list.items.isEmpty)
            }
            ZStack {
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(style: StrokeStyle(lineWidth: 2, dash: [8]))
                    .foregroundStyle(targeted ? Color.accentColor : .secondary)
                    .background(RoundedRectangle(cornerRadius: 12).fill(targeted ? Color.accentColor.opacity(0.08) : .clear))
                if list.items.isEmpty {
                    VStack(spacing: 8) {
                        Image(systemName: "music.note.list").font(.system(size: 40)).foregroundStyle(.secondary)
                        Text("Drag songs (WAV, MP3, AIFF, M4A) or .nrg / .cue disc images here")
                        Text("or open a saved playlist").font(.caption).foregroundStyle(.secondary)
                        HStack { Button("Add Files…") { chooseFiles() }; Button("Add Disc Image…") { chooseImage() }; Button("Play CD") { playCD() } }
                    }
                } else {
                    List(selection: $selection) {
                        ForEach(Array(list.items.enumerated()), id: \.element.id) { i, item in row(i, item) }
                            .onMove { list.items.move(fromOffsets: $0, toOffset: $1) }
                            .onDelete { list.items.remove(atOffsets: $0) }
                    }
                    .scrollContentBackground(.hidden)
                    .padding(6)
                    .onDeleteCommand { list.items.removeAll { selection.contains($0.id) }; selection = [] }
                }
            }
            .frame(minHeight: 240)
            .onDrop(of: [.fileURL], isTargeted: $targeted) { providers in
                for p in providers {
                    _ = p.loadObject(ofClass: URL.self) { url, _ in
                        guard let url else { return }
                        DispatchQueue.main.async { add(url) }
                    }
                }
                return true
            }
            HStack {
                Button("Add Files…") { chooseFiles() }
                Button("Add Disc Image…") { chooseImage() }
                    .help("Add every song from a .nrg or .cue image")
                Button("Play CD") { playCD() }
                    .help("Play the audio CD in the drive, with the song names burned on it")
                Button("Clear") { player.stop(); list.items.removeAll() }.disabled(list.items.isEmpty)
                Spacer()
                Text("\(list.items.count) song(s) · \(timeString(list.items.reduce(0) { $0 + $1.seconds }))").foregroundStyle(.secondary)
                Button("Send to Burn List") {
                    burn.items += list.items.map { BurnItem(title: $0.title, source: $0.source, seconds: $0.seconds, gap: $0.gap, note: $0.note) }
                    if burn.discTitle.isEmpty { burn.discTitle = list.discTitle }
                    showBurnList()
                }
                .disabled(list.items.isEmpty)
                .help("Copy these songs to the Burn CD tab")
            }
            if !list.status.isEmpty { Text(list.status).font(.callout).textSelection(.enabled) }
        }
        .padding(18)
    }

    // MARK: now playing

    var nowPlaying: some View {
        VStack(spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(player.playingID == nil ? "Nothing playing" : player.title)
                        .font(.title2).bold().lineLimit(1)
                    Text(player.playingID == nil ? "Choose a song below, or press Play to start the playlist"
                         : (player.queue.isEmpty ? player.subtitle : "Song \(player.queueIndex + 1) of \(player.queue.count)"))
                        .foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer()
                if player.loading { ProgressView().controlSize(.small) }
            }
            HStack(spacing: 8) {
                Text(timeString(player.position)).monospacedDigit().font(.caption)
                Slider(value: Binding(get: { player.position }, set: { player.seek($0) }), in: 0...max(player.duration, 1))
                    .disabled(player.playingID == nil)
                Text(timeString(player.duration)).monospacedDigit().font(.caption)
            }
            HStack(spacing: 16) {
                Button { player.previous() } label: { Image(systemName: "backward.end.fill").font(.title2) }
                    .disabled(player.playingID == nil).help("Previous song (or back to the start of this one)")
                Button { playPause() } label: {
                    Image(systemName: player.playingID != nil && !player.paused ? "pause.circle.fill" : "play.circle.fill")
                        .font(.system(size: 42))
                }
                .buttonStyle(.borderless)
                .disabled(list.items.isEmpty && player.playingID == nil)
                .help("Play / pause")
                Button { player.stop() } label: { Image(systemName: "stop.fill").font(.title2) }
                    .disabled(player.playingID == nil).help("Stop")
                Button { player.next() } label: { Image(systemName: "forward.end.fill").font(.title2) }
                    .disabled(!player.hasNext).help("Next song")
                Button("Last 10 s") { player.lastTen() }.disabled(player.playingID == nil)
                    .help("Jump to the last 10 seconds of the song")
                Toggle(isOn: $player.repeatAll) { Label("Repeat", systemImage: "repeat") }
                    .toggleStyle(.button).help("Start the playlist again after the last song")
                Spacer()
                Image(systemName: "speaker.fill").foregroundStyle(.secondary)
                Slider(value: $player.volume, in: 0...1).frame(width: 120)
                Image(systemName: "speaker.wave.3.fill").foregroundStyle(.secondary)
            }
        }
        .padding(14)
        .background(RoundedRectangle(cornerRadius: 12).fill(Color.accentColor.opacity(0.10)))
    }

    func playPause() {
        if player.playingID != nil { player.togglePause(); return }
        let from = selection.compactMap { id in list.items.firstIndex { $0.id == id } }.min() ?? 0
        player.playAll(list.playItems, from: from)
    }

    // MARK: rows

    func row(_ i: Int, _ item: BurnItem) -> some View {
        let isPlaying = player.playingID == item.id.uuidString
        return HStack(spacing: 10) {
            Text("\(i + 1)").monospacedDigit().foregroundStyle(.secondary).frame(width: 26, alignment: .trailing)
            Button {
                if isPlaying { player.togglePause() } else { player.playAll(list.playItems, from: i) }
            } label: {
                Image(systemName: isPlaying ? (player.paused ? "play.circle.fill" : "speaker.wave.2.circle.fill") : "play.circle")
                    .font(.title2).foregroundStyle(Color.accentColor)
            }
            .buttonStyle(.borderless)
            .help(isPlaying ? "Pause / resume" : "Play the playlist from this song")
            VStack(alignment: .leading, spacing: 1) {
                TextField("Song name", text: Binding(
                    get: { list.items.first { $0.id == item.id }?.title ?? item.title },
                    set: { v in if let k = list.items.firstIndex(where: { $0.id == item.id }) { list.items[k].title = v } }))
                    .textFieldStyle(.plain).fontWeight(isPlaying ? .bold : .regular)
                Text(sourceLabel(item)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer()
            Text(timeString(item.seconds)).monospacedDigit().foregroundStyle(.secondary)
            Button { list.items.removeAll { $0.id == item.id } } label: { Image(systemName: "xmark.circle.fill") }
                .buttonStyle(.borderless).help("Remove from the playlist")
        }
        .padding(.vertical, 2)
        .listRowBackground(isPlaying ? Color.accentColor.opacity(0.18) : Color.clear)
    }

    func sourceLabel(_ item: BurnItem) -> String {
        if let n = item.note { return n }
        switch item.source {
        case .file(let u): return u.lastPathComponent
        case .nrg(let u, let t, _): return "\(u.lastPathComponent), track \(t.number)"
        case .raw: return "Made in the app"
        }
    }

    // MARK: adding and playlists

    func add(_ url: URL) {
        if ["nrg", "cue"].contains(url.pathExtension.lowercased()) { list.addImage(url) }
        else if url.pathExtension.lowercased() == "nrgplaylist" { load(url) }
        else { list.addFiles([url]) }
    }

    func playCD() {
        let cds = AudioCD.inserted()
        guard let cd = cds.first else { list.status = "No audio CD found. Put one in the drive, wait for it to appear, then try again."; return }
        let start = list.items.count
        list.addCD(cd)
        if list.items.count > start { player.playAll(list.playItems, from: start) }
    }

    func chooseFiles() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = true
        panel.allowedContentTypes = [.audio, .folder]
        if panel.runModal() == .OK { list.addFiles(panel.urls) }
    }

    func chooseImage() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.allowedContentTypes = ["nrg", "cue"].compactMap { UTType(filenameExtension: $0) }
        if panel.runModal() == .OK { panel.urls.forEach(list.addImage) }
    }

    func savePlaylist() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = (list.discTitle.isEmpty ? "My Playlist" : list.discTitle) + ".nrgplaylist"
        panel.allowedContentTypes = [BurnTab.playlistType]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let n = try list.savePlaylist(to: url)
            list.status = "Saved playlist \(url.lastPathComponent)" + (n > 0 ? " (\(n) song(s) saved as WAV beside it)" : "") + "."
        } catch { list.status = "Couldn't save the playlist: \(error.localizedDescription)" }
    }

    func openPlaylist() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [BurnTab.playlistType, .json]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        load(url)
    }

    func load(_ url: URL) {
        var replace = true
        if !list.items.isEmpty {
            let a = NSAlert()
            a.messageText = "Replace the current playlist?"
            a.informativeText = "The playlist already has \(list.items.count) song(s). Replace them, or add the new playlist's songs to the end?"
            a.addButton(withTitle: "Replace"); a.addButton(withTitle: "Add to End"); a.addButton(withTitle: "Cancel")
            switch a.runModal() {
            case .alertFirstButtonReturn: replace = true
            case .alertSecondButtonReturn: replace = false
            default: return
            }
        }
        if replace { player.stop() }
        do {
            let missing = try list.loadPlaylist(from: url, replace: replace)
            list.status = "Opened \(url.lastPathComponent)."
                + (missing.isEmpty ? "" : " Couldn't find \(missing.count) song(s) (moved, or on a drive that isn't connected): " + missing.prefix(5).joined(separator: ", ") + (missing.count > 5 ? "…" : ""))
        } catch { list.status = "Couldn't open the playlist: \(error.localizedDescription)" }
    }
}
