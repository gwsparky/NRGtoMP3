import SwiftUI
import AVFoundation
import UniformTypeIdentifiers
import AppKit

/// Waveform Viewer: look at and listen to the songs of a disc image or CD, tick neighbouring tracks to see and hear
/// them joined as one waveform (for discs where one song is spread over several tracks), and fix and save the names.
/// It never writes audio files, so pressing Return here can't start an export by accident.
final class WaveTabState: ObservableObject {
    enum Audio { case image(URL, swap: Bool), cd([Int: URL]) }

    @Published var disc: Disc?
    @Published var picked: Set<Int> = []
    @Published var peaks: [Int: [Float]] = [:]                       // small waveform of each track (left and right together)
    @Published var combined: (left: [Float], right: [Float]) = ([], [])
    @Published var bounds: [(number: Int, at: Double)] = []          // where each ticked track starts, 0...1 across the combined view
    @Published var combinedSeconds: Double = 0
    @Published var building = false
    @Published var status = ""
    var audio: Audio?
    private let loader = Converter()
    private var peakJob = UUID()
    private var combineJob = UUID()
    static let combinedID = "waveform-combined"

    func open(_ url: URL) {
        guard ["nrg", "cue"].contains(url.pathExtension.lowercased()) else {
            status = "\(url.lastPathComponent) isn't a disc image (.nrg or .cue)."; return
        }
        reset()
        let d = Disc(url: url)
        disc = d
        loader.load(d)
        waitForTracks(d)
    }

    /// The audio CD in the drive. Its names file is named after the album on the disc (e.g. "Church Music.txt"),
    /// so the names on GitHub for the image of the same CD are found too.
    func openCD() {
        guard let cd = AudioCD.inserted().first else {
            status = "No audio CD found. Put one in the drive, wait for it to appear, then try again."; return
        }
        reset()
        let name = Program_clean(cd.album.isEmpty ? cd.volume.lastPathComponent : cd.album)
        let d = Disc(url: cd.volume.appendingPathComponent(name + ".cue"))
        var files: [Int: URL] = [:], tracks: [TrackInfo] = []
        for f in cd.tracks {
            let n = AudioCD.number(f)
            files[n] = f
            let secs = (try? AVAudioFile(forReading: f)).map { Double($0.length) / $0.fileFormat.sampleRate } ?? 0
            tracks.append(TrackInfo(number: n, title: cd.names[n] ?? String(format: "Track %02d", n), performer: nil,
                                    seconds: secs, start: 0, sectors: 0, sectorSize: 2352, gap: nil))
        }
        d.tracks = tracks
        d.names = Dictionary(uniqueKeysWithValues: tracks.map { ($0.number, $0.title) })
        d.album = cd.album
        d.artist = cd.artist
        d.loading = false
        audio = .cd(files)
        disc = d
        if NamesSync.on { Task { await NamesSync.pull(d, automatic: true) } }
        computePeaks()
    }

    private func reset() {
        disc = nil; audio = nil; picked = []; peaks = [:]; combined = ([], []); bounds = []; combinedSeconds = 0; status = ""
        peakJob = UUID(); combineJob = UUID()
    }

    /// The image's song list arrives from the converter a moment later.
    private func waitForTracks(_ d: Disc) {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
            guard self.disc === d else { return }
            if d.loading { self.waitForTracks(d); return }
            self.audio = .image(d.dataURL, swap: d.swap)
            self.objectWillChange.send()
            self.computePeaks()
        }
    }

    func pcm(_ t: TrackInfo) -> Data? {
        switch audio {
        case .image(let url, let swap): return Previewer.pcm(url: url, track: t, swap: swap)
        case .cd(let files):
            guard let f = files[t.number] else { return nil }
            return try? BurnList.pcm(for: BurnItem(title: t.title, source: .file(f), seconds: t.seconds))
        case nil: return nil
        }
    }

    /// Small waveforms, one track at a time in the background.
    private func computePeaks() {
        guard let d = disc else { return }
        let job = UUID(); peakJob = job
        let tracks = d.tracks
        DispatchQueue.global(qos: .utility).async {
            for t in tracks {
                guard self.peakJob == job else { return }
                guard let p = self.pcm(t) else { continue }
                let (l, r) = SplitModel.peaks(p, count: 240)
                let both = zip(l, r).map { max($0, $1) }
                DispatchQueue.main.async { if self.peakJob == job { self.peaks[t.number] = both } }
            }
        }
    }

    func toggle(_ n: Int) {
        if picked.contains(n) { picked.remove(n) } else { picked.insert(n) }
        rebuildCombined()
    }

    /// Ticks every track from the first ticked one to this one (shift-click style, for long runs of tracks).
    func tickRun(to n: Int) {
        guard let d = disc, let first = picked.min() else { toggle(n); return }
        let lo = min(first, n), hi = max(first, n)
        for t in d.tracks where t.number >= lo && t.number <= hi { picked.insert(t.number) }
        rebuildCombined()
    }

    func clearTicks() { picked = []; rebuildCombined() }

    var chosen: [TrackInfo] { (disc?.tracks ?? []).filter { picked.contains($0.number) } }

    /// The ticked tracks joined end to end, as one waveform with a line where each track starts.
    func rebuildCombined() {
        let tracks = chosen
        let job = UUID(); combineJob = job
        guard !tracks.isEmpty else { combined = ([], []); bounds = []; combinedSeconds = 0; building = false; return }
        building = true
        DispatchQueue.global(qos: .userInitiated).async {
            let (pcm, starts) = self.joined(tracks)
            let wave = SplitModel.peaks(pcm, count: 1400)
            let total = Double(pcm.count)
            DispatchQueue.main.async {
                guard self.combineJob == job else { return }
                self.combined = wave
                self.bounds = zip(tracks, starts).map { ($0.number, total > 0 ? Double($1) / total : 0) }
                self.combinedSeconds = total / (44100 * 4)
                self.building = false
            }
        }
    }

    func joined(_ tracks: [TrackInfo]) -> (Data, [Int]) {
        var out = Data(), starts: [Int] = []
        for t in tracks {
            starts.append(out.count)
            if let p = pcm(t) { out.append(p) }
        }
        return (out, starts)
    }

    /// Gives the ticked tracks one name, numbered as parts: "Amazing Grace (part 1 of 3)".
    func nameTicked(_ name: String) {
        guard let d = disc else { return }
        let base = name.trimmingCharacters(in: .whitespaces)
        guard !base.isEmpty else { return }
        let tracks = chosen
        for (i, t) in tracks.enumerated() {
            d.names[t.number] = tracks.count == 1 ? base : "\(base) (part \(i + 1) of \(tracks.count))"
        }
    }
}

/// `Program.Clean` on the Mac side: removes characters that aren't allowed in file names on Windows or macOS.
func Program_clean(_ s: String) -> String {
    let bad = CharacterSet(charactersIn: "<>:\"/\\|?*")
    return s.components(separatedBy: bad).joined().trimmingCharacters(in: .whitespaces)
}

struct WaveTab: View {
    @ObservedObject var state: WaveTabState
    @ObservedObject var player: Previewer
    let openInConvert: (URL) -> Void
    @State private var targeted = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Image(systemName: "waveform").font(.title2)
                Text(state.disc?.url.deletingPathExtension().lastPathComponent ?? "Waveform Viewer").font(.headline)
                Spacer()
                Button("Open Disc Image…") { chooseImage() }
                Button("Open CD") { player.stop(); state.openCD() }
                    .help("Show the songs on the audio CD in the drive")
                if state.disc != nil {
                    Button("Close") { player.stop(); state.disc = nil; state.picked = []; state.rebuildCombined() }
                }
            }
            if let d = state.disc {
                DiscWaves(disc: d, state: state, player: player, openInConvert: openInConvert)
            } else {
                dropZone
            }
            if !state.status.isEmpty { Text(state.status).font(.callout) }
        }
        .padding(18)
    }

    var dropZone: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(style: StrokeStyle(lineWidth: 2, dash: [8]))
                .foregroundStyle(targeted ? Color.accentColor : .secondary)
                .background(RoundedRectangle(cornerRadius: 12).fill(targeted ? Color.accentColor.opacity(0.08) : .clear))
            VStack(spacing: 8) {
                Image(systemName: "waveform").font(.system(size: 40)).foregroundStyle(.secondary)
                Text("Drag a .nrg or .cue disc image here, or open the CD in the drive")
                Text("See every track's waveform, tick tracks to hear them joined, and fix the song names. Nothing is exported from this tab.")
                    .font(.caption).foregroundStyle(.secondary)
                HStack { Button("Open Disc Image…") { chooseImage() }; Button("Open CD") { state.openCD() } }
            }
        }
        .frame(maxHeight: .infinity)
        .onDrop(of: [.fileURL], isTargeted: $targeted) { providers in
            _ = providers.first?.loadObject(ofClass: URL.self) { url, _ in
                if let url { DispatchQueue.main.async { player.stop(); state.open(url) } }
            }
            return true
        }
    }

    func chooseImage() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = ["nrg", "cue"].compactMap { UTType(filenameExtension: $0) }
        if panel.runModal() == .OK, let url = panel.url { player.stop(); state.open(url) }
    }
}

/// The opened disc: names, the track list with small waveforms, and the joined waveform of the ticked tracks.
struct DiscWaves: View {
    @ObservedObject var disc: Disc
    @ObservedObject var state: WaveTabState
    @ObservedObject var player: Previewer
    let openInConvert: (URL) -> Void
    @State private var lookingUp = false
    @State private var groupName = ""
    @State private var pendingSeek: Double?

    var isCD: Bool { if case .cd = state.audio { return true }; return false }
    func pid(_ t: TrackInfo) -> String { "wave#\(disc.url.path)#\(t.number)" }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if disc.loading {
                HStack { ProgressView().controlSize(.small); Text("Reading \(disc.url.lastPathComponent)…") }
            } else if let e = disc.error {
                Text("Couldn't read this disc: \(e)").foregroundStyle(.red)
            } else {
                namesBar
                trackList
                combinedPanel
            }
        }
        .sheet(isPresented: $lookingUp) { LookupSheet(disc: disc) }
        .onChange(of: player.duration) { _ in
            if let s = pendingSeek, player.playingID == WaveTabState.combinedID, player.duration > 0 { player.seek(s); pendingSeek = nil }
        }
    }

    var namesBar: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 8) {
                Text("Album").foregroundStyle(.secondary)
                TextField("Album", text: $disc.album).textFieldStyle(.roundedBorder)
                Text("Artist").foregroundStyle(.secondary)
                TextField("Artist", text: $disc.artist).textFieldStyle(.roundedBorder)
                Button("Look Up Names…") { lookingUp = true }
                    .help("Find the album and song names on MusicBrainz")
                Button("Save Names") { saveNames() }
                    .help("Save the album, artist and song names to \(disc.namesFile.lastPathComponent) (and to GitHub), the same as on the Convert tab")
                Button("Load Names…") { loadNames() }
                Button("Get from GitHub") { Task { await NamesSync.pull(disc, automatic: false) } }
                    .disabled(!NamesSync.on)
            }
            HStack {
                if let note = disc.namesNote { Text(note).font(.caption).foregroundStyle(.secondary) }
                Spacer()
                if !isCD {
                    Button("Open in Convert Tab") { player.stop(); openInConvert(disc.url) }
                        .help("Open this disc on the Convert tab to export songs (save the names first so they come along)")
                }
            }
        }
    }

    var trackList: some View {
        List(disc.tracks) { t in
            HStack(spacing: 10) {
                Toggle("", isOn: Binding(get: { state.picked.contains(t.number) }, set: { _ in state.toggle(t.number) }))
                    .labelsHidden()
                    .help("Tick tracks to see and hear them joined below")
                Button {
                    player.toggle(id: pid(t), title: disc.name(t), subtitle: disc.album, source: .data { state.pcm(t).map(Previewer.wav) })
                } label: {
                    Image(systemName: player.isPlaying(pid(t)) ? "stop.circle.fill" : "play.circle.fill").font(.title2)
                        .foregroundStyle(player.isPlaying(pid(t)) ? Color.red : Color.accentColor)
                }
                .buttonStyle(.borderless)
                Text(String(format: "%02d", t.number)).monospacedDigit().foregroundStyle(.secondary)
                TextField("Song name", text: Binding(get: { disc.name(t) }, set: { disc.names[t.number] = $0 }))
                    .textFieldStyle(.plain).frame(minWidth: 180, maxWidth: 320)
                MiniWave(peaks: state.peaks[t.number] ?? [], ticked: state.picked.contains(t.number))
                    .frame(height: 26)
                    .contentShape(Rectangle())
                    .onTapGesture { state.toggle(t.number) }
                    .help("Click to tick or untick this track")
                Text(timeString(t.seconds)).monospacedDigit().foregroundStyle(.secondary).frame(width: 44, alignment: .trailing)
                Button { state.tickRun(to: t.number) } label: { Image(systemName: "arrow.up.and.down.text.horizontal") }
                    .buttonStyle(.borderless)
                    .help("Tick every track from the first ticked one down (or up) to this one")
            }
        }
        .frame(minHeight: 200)
    }

    var combinedPanel: some View {
        let chosen = state.chosen
        let playing = player.playingID == WaveTabState.combinedID
        return VStack(alignment: .leading, spacing: 6) {
            HStack {
                if chosen.isEmpty {
                    Text("Tick tracks above to see them joined as one waveform.").foregroundStyle(.secondary)
                } else {
                    Text(chosen.count == 1 ? "Track \(chosen[0].number)" : "Tracks " + chosen.map { "\($0.number)" }.joined(separator: ", "))
                        .bold()
                    Text("· \(timeString(state.combinedSeconds))").foregroundStyle(.secondary)
                    if hasHoles(chosen) {
                        Text("(not next to each other on the disc)").font(.caption).foregroundStyle(.orange)
                    }
                }
                if state.building { ProgressView().controlSize(.small) }
                Spacer()
                if !chosen.isEmpty {
                    Button { playPause() } label: {
                        Label(playing && !player.paused ? "Pause" : "Play Joined", systemImage: playing && !player.paused ? "pause.fill" : "play.fill")
                    }
                    if playing { Button("Stop") { player.stop() } }
                    Button("Untick All") { player.stop(); state.clearTicks() }
                }
            }
            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.05))
                WaveformCanvas(left: state.combined.left, right: state.combined.right, combined: [])
                GeometryReader { g in
                    ForEach(Array(state.bounds.enumerated()), id: \.offset) { i, b in
                        if i > 0 {
                            Rectangle().fill(Color.orange).frame(width: 2, height: g.size.height).offset(x: g.size.width * b.at - 1)
                        }
                        Text("\(b.number)").font(.caption2.bold()).padding(.horizontal, 3)
                            .background(RoundedRectangle(cornerRadius: 3).fill(Color.orange.opacity(0.85)))
                            .foregroundStyle(.white)
                            .offset(x: g.size.width * b.at + 3, y: 2)
                    }
                    if playing, player.duration > 0 {
                        Rectangle().fill(Color.red).frame(width: 2, height: g.size.height)
                            .offset(x: g.size.width * player.position / player.duration - 1)
                    }
                    Color.clear.contentShape(Rectangle())
                        .gesture(DragGesture(minimumDistance: 0).onEnded { v in
                            seek(to: max(0, min(1, v.location.x / g.size.width)))
                        })
                }
            }
            .frame(height: 150)
            .help("Orange lines show where each track starts. Click to play from that point.")
            if chosen.count > 0 {
                HStack {
                    Text("Name the ticked tracks:").foregroundStyle(.secondary)
                    TextField("Song name", text: $groupName).textFieldStyle(.roundedBorder).frame(maxWidth: 300)
                    Button("Apply Name") { state.nameTicked(groupName) }
                        .disabled(groupName.trimmingCharacters(in: .whitespaces).isEmpty)
                        .help(chosen.count == 1 ? "Rename this track" : "Name them \"\(groupName.isEmpty ? "Song" : groupName) (part 1 of \(chosen.count))\" and so on; then Save Names")
                    Spacer()
                }
            }
        }
    }

    func hasHoles(_ c: [TrackInfo]) -> Bool {
        let all = disc.tracks.map(\.number)
        guard let a = all.firstIndex(of: c.first?.number ?? -1), let b = all.firstIndex(of: c.last?.number ?? -1) else { return false }
        return b - a + 1 != c.count
    }

    func joinedItem() -> Previewer.Item {
        let tracks = state.chosen, s = state
        let title = tracks.map { disc.name($0) }.first ?? "Joined tracks"
        return Previewer.Item(id: WaveTabState.combinedID, title: title,
                              subtitle: "Tracks " + tracks.map { "\($0.number)" }.joined(separator: ", "),
                              source: .data { Previewer.wav(s.joined(tracks).0) })
    }

    func playPause() {
        if player.playingID == WaveTabState.combinedID { player.togglePause(); return }
        player.stop()
        player.playAll([joinedItem()])
    }

    func seek(to fraction: Double) {
        if player.playingID == WaveTabState.combinedID, player.duration > 0 { player.seek(fraction * player.duration); return }
        guard !state.chosen.isEmpty else { return }
        pendingSeek = fraction * state.combinedSeconds
        player.stop()
        player.playAll([joinedItem()])
    }

    func saveNames() {
        let text = disc.namesText()
        func saved(_ url: URL) {
            disc.namesNote = "Names saved to \(url.lastPathComponent)"
            Task { await NamesSync.push(disc, text: text) }
        }
        if !isCD, (try? text.write(to: disc.namesFile, atomically: true, encoding: .utf8)) != nil { saved(disc.namesFile); return }
        // a CD (or a read-only folder): choose where to put the names file
        let panel = NSSavePanel()
        panel.nameFieldStringValue = disc.namesFile.lastPathComponent
        panel.allowedContentTypes = [.plainText]
        panel.message = isCD ? "Save the names next to this CD's disc image, if you have one, so they load with it."
                             : "Couldn't save next to the image. Choose where to save the names file."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do { try text.write(to: url, atomically: true, encoding: .utf8); saved(url) }
        catch { disc.namesNote = "Couldn't save the names: \(error.localizedDescription)" }
    }

    func loadNames() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.plainText, .text]
        if !isCD { panel.directoryURL = disc.url.deletingLastPathComponent() }
        guard panel.runModal() == .OK, let url = panel.url,
              let text = (try? String(contentsOf: url, encoding: .utf8)) ?? (try? String(contentsOf: url, encoding: .isoLatin1)) else { return }
        disc.namesNote = "\(disc.applyNames(text)) of \(disc.tracks.count) name(s) loaded from \(url.lastPathComponent)"
    }
}

struct MiniWave: View {
    let peaks: [Float]
    let ticked: Bool

    var body: some View {
        Canvas { ctx, size in
            guard !peaks.isEmpty else { return }
            let bw = size.width / CGFloat(peaks.count), mid = size.height / 2
            var path = Path()
            for (i, p) in peaks.enumerated() {
                let h = max(1, CGFloat(p) * (size.height - 2))
                path.addRect(CGRect(x: CGFloat(i) * bw, y: mid - h / 2, width: max(1, bw), height: h))
            }
            ctx.fill(path, with: .color(ticked ? Color.orange : Color.accentColor.opacity(0.7)))
        }
        .background(RoundedRectangle(cornerRadius: 4).fill(Color.primary.opacity(ticked ? 0.08 : 0.04)))
        .overlay { if peaks.isEmpty { ProgressView().controlSize(.mini) } }
    }
}
