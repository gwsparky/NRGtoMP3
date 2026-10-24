import SwiftUI
import AVFoundation
import UniformTypeIdentifiers
import AppKit

let keyNames = ["C", "C♯/D♭", "D", "E♭", "E", "F", "F♯/G♭", "G", "A♭", "A", "B♭", "B"]
let fileKeyNames = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"]   // safe in file names

func halfStepLabel(_ n: Int) -> String {
    n == 0 ? "Original key" : (n > 0 ? "+\(n)" : "−\(-n)") + (abs(n) == 1 ? " half step" : " half steps")
}

// MARK: - Audio

enum ChannelMode: String, CaseIterable {
    case both, removeLeft, removeRight
    var label: String {
        switch self {
        case .both: return "Keep both sides"
        case .removeLeft: return "Remove left side"
        case .removeRight: return "Remove right side"
        }
    }
}

/// Removes one side of a stereo song: the empty side is either silent, or gets a copy of the
/// side that's kept (so it plays through both speakers).
func applyChannels(_ pcm: Data, _ mode: ChannelMode, duplicate: Bool) -> Data {
    guard mode != .both else { return pcm }
    var out = pcm
    out.withUnsafeMutableBytes { raw in
        let s = raw.bindMemory(to: Int16.self)
        let keep = mode == .removeRight ? 0 : 1          // 0 = left sample, 1 = right sample
        var f = 0
        while f + 1 < s.count {
            s[f + 1 - keep] = duplicate ? s[f + keep] : 0
            f += 2
        }
    }
    return out
}

/// Pitch-shifts CD audio without changing its speed, using Apple's time-pitch processor.
enum PitchShift {
    static let format = AVAudioFormat(standardFormatWithSampleRate: 44_100, channels: 2)!

    /// 16-bit little-endian stereo PCM → floating-point buffer for AVAudioEngine.
    static func buffer(from pcm: Data) -> AVAudioPCMBuffer? {
        let frames = pcm.count / 4
        guard frames > 0, let buf = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)) else { return nil }
        buf.frameLength = AVAudioFrameCount(frames)
        let l = buf.floatChannelData![0], r = buf.floatChannelData![1]
        pcm.withUnsafeBytes { raw in
            let s = raw.bindMemory(to: Int16.self)
            for f in 0..<frames {
                l[f] = Float(Int16(littleEndian: s[2 * f])) / 32768
                r[f] = Float(Int16(littleEndian: s[2 * f + 1])) / 32768
            }
        }
        return buf
    }

    static func makeUnit(cents: Float) -> AVAudioUnitTimePitch {
        let tp = AVAudioUnitTimePitch()
        tp.pitch = cents
        tp.rate = 1
        tp.overlap = 16          // higher = smoother sound, a little more work
        return tp
    }

    /// Renders the whole song shifted by `cents` (100 cents = 1 half step). Same length as the original.
    static func render(_ input: AVAudioPCMBuffer, cents: Float, progress: (Double) -> Void) throws -> Data {
        let engine = AVAudioEngine()
        let player = AVAudioPlayerNode()
        let tp = makeUnit(cents: cents)
        engine.attach(player)
        engine.attach(tp)
        engine.connect(player, to: tp, format: format)
        engine.connect(tp, to: engine.mainMixerNode, format: format)
        let chunk: AVAudioFrameCount = 4096
        try engine.enableManualRenderingMode(.offline, format: format, maximumFrameCount: chunk)
        try engine.start()
        defer { engine.stop() }
        player.scheduleBuffer(input, at: nil)
        player.play()

        // the processor delays its output slightly; render that much extra and drop it from the start
        let delay = Int((tp.auAudioUnit.latency * 44_100).rounded())
        let total = Int(input.frameLength) + delay
        let out = AVAudioPCMBuffer(pcmFormat: engine.manualRenderingFormat, frameCapacity: chunk)!
        var pcm = Data(capacity: Int(input.frameLength) * 4)
        var rendered = 0
        while rendered < total {
            let n = AVAudioFrameCount(min(Int(chunk), total - rendered))
            guard try engine.renderOffline(n, to: out) == .success else { break }
            let l = out.floatChannelData![0], r = out.floatChannelData![1]
            var frames = [Int16]()
            frames.reserveCapacity(Int(out.frameLength) * 2)
            for f in 0..<Int(out.frameLength) where rendered + f >= delay {
                frames.append(Int16(max(-32768, min(32767, (l[f] * 32768).rounded()))).littleEndian)
                frames.append(Int16(max(-32768, min(32767, (r[f] * 32768).rounded()))).littleEndian)
            }
            frames.withUnsafeBytes { pcm.append(contentsOf: $0) }
            rendered += Int(out.frameLength)
            progress(Double(rendered) / Double(total))
        }
        return pcm
    }
}

/// Live preview: plays the song through the pitch shifter so changes are heard straight away.
final class KeyPlayer: ObservableObject {
    private let engine = AVAudioEngine()
    private let node = AVAudioPlayerNode()
    private let tp = PitchShift.makeUnit(cents: 0)
    private var buffer: AVAudioPCMBuffer?
    private var startFrame: AVAudioFramePosition = 0
    private var timer: Timer?
    @Published var playing = false
    @Published var position: Double = 0
    var duration: Double { Double(buffer?.frameLength ?? 0) / 44_100 }

    init() {
        engine.attach(node)
        engine.attach(tp)
        engine.connect(node, to: tp, format: PitchShift.format)
        engine.connect(tp, to: engine.mainMixerNode, format: PitchShift.format)
    }

    func load(_ b: AVAudioPCMBuffer) { stop(); buffer = b; position = 0 }

    /// Swaps in a changed version of the same song, carrying on from the same spot.
    func replace(_ b: AVAudioPCMBuffer) {
        let wasPlaying = playing
        if playing { pause() }
        buffer = b
        if wasPlaying { play() }
    }

    func set(cents: Float, original: Bool) {
        tp.pitch = cents
        tp.bypass = original
    }

    func play() {
        guard let buffer, !playing else { return }
        do { if !engine.isRunning { try engine.start() } } catch { return }
        startFrame = AVAudioFramePosition(position * 44_100)
        let remaining = AVAudioFrameCount(max(0, Int64(buffer.frameLength) - startFrame))
        guard remaining > 0 else { position = 0; return }
        // play from the current position: copy the rest of the song into its own buffer
        guard let tail = AVAudioPCMBuffer(pcmFormat: PitchShift.format, frameCapacity: remaining) else { return }
        tail.frameLength = remaining
        for c in 0..<2 {
            tail.floatChannelData![c].update(from: buffer.floatChannelData![c] + Int(startFrame), count: Int(remaining))
        }
        node.stop()
        node.scheduleBuffer(tail, at: nil) { [weak self] in
            DispatchQueue.main.async {
                guard let self, self.playing, self.position >= self.duration - 0.3 else { return }
                self.pause(); self.position = 0
            }
        }
        node.play()
        playing = true
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in self?.tick() }
    }

    private func tick() {
        guard playing, let t = node.lastRenderTime, let pt = node.playerTime(forNodeTime: t) else { return }
        position = min(duration, Double(startFrame + pt.sampleTime) / 44_100)
    }

    func pause() {
        tick()
        playing = false
        node.stop()
        timer?.invalidate(); timer = nil
    }

    func seek(_ t: Double) {
        let wasPlaying = playing
        if playing { pause() }
        position = min(max(0, t), duration)
        if wasPlaying { play() }
    }

    func stop() { pause(); position = 0 }
}

// MARK: - Tab state

final class KeyTabState: ObservableObject {
    struct ImageChoice { let url: URL; let result: ListResult }

    @Published var item: BurnItem?
    @Published var choice: ImageChoice?
    @Published var lastChoice: ImageChoice?        // the disc image the current song came from, for "Song List"
    @Published var cd: AudioCD?                    // an audio CD whose song list is showing
    @Published var lastCD: AudioCD?                // the CD the current song came from, for "Song List"
    @Published var picked: Set<Int> = []           // songs ticked in the list, to combine
    @Published var loading = false
    @Published var halfSteps = 0 { didSet { updatePlayer() } }
    @Published var cents = 0 { didSet { updatePlayer() } }         // fine tune, −50...50
    @Published var original = false { didSet { updatePlayer(); swapPreview() } }  // hear the song unchanged, to compare
    @Published var channels = ChannelMode.both { didSet { rebuildChannels() } }
    @Published var duplicate = true { didSet { if channels != .both { rebuildChannels() } } }
    @Published var originalKey = -1                                // index into keyNames, −1 = not set
    @Published var running = false
    @Published var started: Date?
    @Published var progress: Double = 0
    @Published var status = ""
    @Published var log = ""
    @Published var outputFolder: URL? = UserDefaults.standard.url(forKey: "keyFolder") ?? UserDefaults.standard.url(forKey: "outputFolder")
    let player = KeyPlayer()
    @Published var peaks: (left: [Float], right: [Float]) = ([], [])         // waveform of the song as it will be saved
    @Published var sourcePeaks: (left: [Float], right: [Float]) = ([], [])   // waveform of the untouched song
    private var sourcePCM = Data()                 // the song as loaded
    private var sourceBuffer: AVAudioPCMBuffer?
    private var pcm = Data()                       // with the chosen side removed (same as source for "both")
    private var buffer: AVAudioPCMBuffer?

    var totalCents: Float { Float(halfSteps * 100 + cents) }
    var newKey: String? { originalKey < 0 ? nil : keyNames[((originalKey + halfSteps) % 12 + 12) % 12] }

    func updatePlayer() { player.set(cents: totalCents, original: original) }

    /// Re-applies the left/right choice and updates the preview.
    func rebuildChannels() {
        guard item != nil else { return }
        let mode = channels, dup = duplicate, src = sourcePCM
        DispatchQueue.global().async {
            let out = applyChannels(src, mode, duplicate: dup)
            let buf = mode == .both ? self.sourceBuffer : PitchShift.buffer(from: out)
            let wave = mode == .both ? self.sourcePeaks : SplitModel.peaks(out, count: 1200)
            DispatchQueue.main.async {
                guard self.channels == mode, self.duplicate == dup, let buf else { return }   // a newer choice is on its way
                self.pcm = out; self.buffer = buf
                self.peaks = wave
                self.swapPreview()
            }
        }
    }

    /// The preview plays the untouched song when "Original" is chosen, otherwise the changed one.
    func swapPreview() {
        if let b = original ? sourceBuffer : buffer { player.replace(b) }
    }

    var channelNote: String? {
        switch channels {
        case .both: return nil
        case .removeRight: return duplicate ? "left side in both speakers" : "left side only"
        case .removeLeft: return duplicate ? "right side in both speakers" : "right side only"
        }
    }

    func setOutput(_ url: URL?) {
        outputFolder = url
        UserDefaults.standard.set(url, forKey: "keyFolder")
    }

    func open(_ url: URL) {
        close()
        let ext = url.pathExtension.lowercased()
        if audioExtensions.contains(ext) {
            load(BurnItem(title: url.deletingPathExtension().lastPathComponent, source: .file(url), seconds: 0))
        } else if ["nrg", "cue"].contains(ext) {
            loading = true
            status = "Reading \(url.lastPathComponent)…"
            listImage(url) { r in
                self.loading = false
                guard let r, r.error == nil, let tracks = r.tracks, !tracks.isEmpty else {
                    self.status = "Couldn't read \(url.lastPathComponent): \(r?.error ?? "unknown problem")"; return
                }
                self.status = ""
                if tracks.count == 1 { self.pick(tracks[0], from: ImageChoice(url: url, result: r)) }
                else { self.choice = ImageChoice(url: url, result: r) }
            }
        } else {
            status = "\(url.lastPathComponent) isn't a song or disc image this app can open."
        }
    }

    func pick(_ t: TrackInfo, from c: ImageChoice) {
        choice = nil
        lastChoice = c
        load(BurnItem(title: t.title, source: .nrg(URL(fileURLWithPath: c.result.dataPath ?? c.url.path), t, swap: c.result.swap ?? false),
                      seconds: t.seconds))
    }

    func load(_ item: BurnItem) {
        loading = true
        status = "Reading the song…"
        DispatchQueue.global().async {
            do {
                let pcm = try BurnList.pcm(for: item)
                let wave = SplitModel.peaks(pcm, count: 1200)
                guard let buf = PitchShift.buffer(from: pcm) else { throw NSError(domain: "Key", code: 1, userInfo: [NSLocalizedDescriptionKey: "The song is empty."]) }
                DispatchQueue.main.async {
                    self.sourcePCM = pcm; self.sourceBuffer = buf
                    self.sourcePeaks = wave; self.peaks = wave
                    self.pcm = pcm; self.buffer = buf
                    self.item = item
                    self.player.load(buf)
                    self.updatePlayer()
                    self.loading = false; self.status = ""
                    if self.channels != .both { self.rebuildChannels() }
                }
            } catch {
                DispatchQueue.main.async { self.loading = false; self.status = "Couldn't read the song: \(error.localizedDescription)" }
            }
        }
    }

    /// Shows the songs of the audio CD in the drive, named from the disc's CD-Text.
    func openCD() {
        guard let found = AudioCD.inserted().first else {
            close(); status = "No audio CD found. Put one in the drive, wait for it to appear, then try again."; return
        }
        close()
        cd = found
    }

    func pickCD(_ f: URL, from c: AudioCD) {
        cd = nil
        lastCD = c
        let n = AudioCD.number(f)
        load(BurnItem(title: c.names[n] ?? String(format: "Track %02d", n), source: .file(f), seconds: 0))
    }

    /// Back to the disc's song list, keeping the key settings for the next song.
    func backToList() {
        if let c = lastChoice { unloadSong(); choice = c }
        else if let c = lastCD { unloadSong(); cd = c }
    }

    /// Joins the ticked songs into one, then opens it. Songs next to each other on the disc keep the
    /// original gap between them; with songs skipped in between, they're joined end to end.
    func combine(from c: ImageChoice) {
        let all = c.result.tracks ?? []
        let chosen = all.filter { picked.contains($0.number) }
        guard !chosen.isEmpty else { return }
        if chosen.count == 1 { pick(chosen[0], from: c); return }
        let url = URL(fileURLWithPath: c.result.dataPath ?? c.url.path), swap = c.result.swap ?? false
        let album = c.result.album ?? c.url.deletingPathExtension().lastPathComponent
        let title = chosen.count == all.count ? album
            : chosen.count == 2 ? "\(chosen[0].title) + \(chosen[1].title)"
            : "\(chosen[0].title) + \(chosen.count - 1) more"
        choice = nil
        lastChoice = c
        loading = true
        status = "Combining \(chosen.count) songs…"
        DispatchQueue.global().async {
            do {
                let dir = FileManager.default.temporaryDirectory.appendingPathComponent("NRG to MP3 key changes")
                try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                let file = dir.appendingPathComponent(UUID().uuidString + ".pcm")
                FileManager.default.createFile(atPath: file.path, contents: nil)
                let fh = try FileHandle(forWritingTo: file)
                var total: Int64 = 0
                for (i, t) in chosen.enumerated() {
                    var piece = t
                    if i + 1 < chosen.count, let at = all.firstIndex(where: { $0.number == t.number }),
                       at + 1 < all.count, all[at + 1].number == chosen[i + 1].number {
                        // run on to where the next song starts, keeping the gap between them
                        piece = TrackInfo(number: t.number, title: t.title, performer: t.performer, seconds: t.seconds, start: t.start,
                                          sectors: (chosen[i + 1].start - t.start) / Int64(t.sectorSize), sectorSize: t.sectorSize, gap: nil)
                    }
                    guard let pcm = Previewer.pcm(url: url, track: piece, swap: swap) else {
                        throw NSError(domain: "Key", code: 2, userInfo: [NSLocalizedDescriptionKey: "Couldn't read song \(t.number)."])
                    }
                    fh.write(pcm); total += Int64(pcm.count)
                }
                try fh.close()
                let item = BurnItem(title: title, source: .raw(file, offset: 0, length: total), seconds: Double(total) / 176_400,
                                    note: "Songs " + chosen.map { String($0.number) }.joined(separator: ", ") + " combined")
                DispatchQueue.main.async { self.load(item) }
            } catch {
                DispatchQueue.main.async { self.loading = false; self.status = "Couldn't combine the songs: \(error.localizedDescription)" }
            }
        }
    }

    private func unloadSong() {
        player.stop()
        item = nil; pcm = Data(); buffer = nil; sourcePCM = Data(); sourceBuffer = nil
        peaks = ([], []); sourcePeaks = ([], [])
        original = false; status = ""; log = ""
    }

    func close() {
        lastChoice = nil; picked = []; cd = nil; lastCD = nil
        player.stop()
        item = nil; choice = nil; pcm = Data(); buffer = nil; sourcePCM = Data(); sourceBuffer = nil
        peaks = ([], []); sourcePeaks = ([], [])
        halfSteps = 0; cents = 0; original = false; status = ""; log = ""
    }

    /// "Amazing Grace (+2, key of A)"
    var newTitle: String {
        guard let item else { return "" }
        var parts: [String] = []
        if halfSteps != 0 { parts.append(halfSteps > 0 ? "+\(halfSteps)" : "-\(-halfSteps)") }
        if cents != 0 { parts.append("\(cents > 0 ? "+" : "")\(cents) cents") }
        if originalKey >= 0 { parts.append("key of " + fileKeyNames[((originalKey + halfSteps) % 12 + 12) % 12]) }
        if let c = channelNote { parts.append(c) }
        return parts.isEmpty ? item.title : "\(item.title) (\(parts.joined(separator: ", ")))"
    }

    /// Renders the shifted song to a CD-format file and hands back a burn-list song pointing at it.
    func renderSong(then: @escaping (BurnItem) -> Void) {
        guard let buffer, item != nil else { return }
        player.pause()
        running = true; started = Date(); progress = 0; status = "Changing the key…"
        let cents = totalCents, title = newTitle, halfSteps = halfSteps, channelNote = channelNote
        DispatchQueue.global().async {
            do {
                let out: Data
                if cents == 0 { out = self.pcm }
                else {
                    var last = 0.0
                    out = try PitchShift.render(buffer, cents: cents) { p in
                        if p - last > 0.01 { last = p; DispatchQueue.main.async { self.progress = p } }
                    }
                }
                let dir = FileManager.default.temporaryDirectory.appendingPathComponent("NRG to MP3 key changes")
                try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                let file = dir.appendingPathComponent(UUID().uuidString + ".pcm")
                try out.write(to: file)
                let song = BurnItem(title: title, source: .raw(file, offset: 0, length: Int64(out.count)),
                                    seconds: Double(out.count) / 176_400,
                                    note: [halfSteps == 0 ? nil : halfStepLabel(halfSteps), channelNote].compactMap { $0 }.joined(separator: " · "))
                DispatchQueue.main.async { self.progress = 1; then(song) }
            } catch {
                DispatchQueue.main.async { self.running = false; self.started = nil; self.status = "Failed: \(error.localizedDescription)" }
            }
        }
    }

    func export(wav: Bool) {
        guard let folder = outputFolder else { return }
        log = ""
        renderSong { song in
            self.status = "Saving…"
            exportSongs([song], album: song.title, to: folder, wav: wav,
                        onStatus: { self.status = $0 }, onLog: { self.log += $0 }) { ok in
                let took = timeString(Date().timeIntervalSince(self.started ?? Date()))
                self.running = false; self.started = nil
                self.status = ok ? "Saved “\(song.title)” in \(took)." : (self.status.hasPrefix("Failed") ? self.status : "Export failed (see details below).")
            }
        }
    }

    func addToBurnList(_ burn: BurnList, then: @escaping () -> Void) {
        renderSong { song in
            burn.items.append(song)
            let took = timeString(Date().timeIntervalSince(self.started ?? Date()))
            self.running = false; self.started = nil
            self.status = "Added “\(song.title)” to the burn list (\(took))."
            then()
        }
    }
}

// MARK: - View

struct KeyTab: View {
    @ObservedObject var state: KeyTabState
    @ObservedObject var burn: BurnList
    @ObservedObject var preview: Previewer
    let showBurnList: () -> Void
    @AppStorage("wav") private var wav = false
    @State private var targeted = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Image(systemName: "music.quarternote.3").font(.title2)
                VStack(alignment: .leading, spacing: 1) {
                    Text(state.item?.title ?? "Change the key of a song").font(.headline)
                    if state.item != nil {
                        Text("\(timeString(state.player.duration)) · speed stays the same").font(.caption).foregroundStyle(.secondary)
                    }
                }
                Spacer()
                Button("Open Song…") { chooseSong() }.disabled(state.running)
                Button("Open CD") { state.openCD() }.disabled(state.running)
                    .help("Choose a song from the audio CD in the drive")
                if state.item != nil && (state.lastChoice != nil || state.lastCD != nil) {
                    Button { state.backToList() } label: { Label("Song List", systemImage: "chevron.left") }
                        .disabled(state.running)
                        .help("Back to the list of songs on \(state.lastChoice?.url.lastPathComponent ?? state.lastCD?.volume.lastPathComponent ?? "the disc")")
                }
                if state.item != nil || state.choice != nil || state.cd != nil { Button("Close") { state.close() }.disabled(state.running) }
            }

            if state.item != nil {
                KeyControls(state: state, player: state.player)
                Spacer(minLength: 0)
                outputControls
            } else if let c = state.choice {
                trackPicker(c)
            } else if let c = state.cd {
                cdPicker(c)
            } else if state.loading {
                HStack { ProgressView().controlSize(.small); Text(state.status) }.frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                dropZone
                if !state.status.isEmpty { Text(state.status).font(.callout) }
            }
        }
        .padding(18)
        .onAppear { preview.stop() }
        .onDisappear { state.player.pause() }
    }

    var outputControls: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Save to:").bold()
                Text(state.outputFolder?.path ?? "Choose a folder").lineLimit(1).truncationMode(.middle)
                    .foregroundStyle(state.outputFolder == nil ? .secondary : .primary)
                Button("Choose…") { chooseFolder() }.disabled(state.running)
                Spacer()
                Picker("", selection: $wav) {
                    Text("MP3").tag(false)
                    Text("WAV").tag(true)
                }
                .pickerStyle(.segmented).labelsHidden().frame(width: 120).disabled(state.running)
            }
            if state.running && state.progress < 1 {
                ProgressView(value: state.progress)
            }
            HStack {
                if state.running { ProgressView().controlSize(.small) }
                if state.running, let s = state.started { ElapsedLabel(start: s) }
                Text(state.status).font(.callout)
                Spacer()
                Text("Will be named “\(state.newTitle)”").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                Button("Add to Burn List") { state.addToBurnList(burn) { showBurnList() } }
                    .disabled(state.running)
                Button("Export \(wav ? "WAV" : "MP3")") { state.export(wav: wav) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(state.running || state.outputFolder == nil)
                    .help(state.outputFolder == nil ? "Choose a folder to save to first" : "Save the song in its new key")
            }
            if !state.log.isEmpty {
                ScrollView {
                    Text(state.log).font(.system(.caption, design: .monospaced))
                        .frame(maxWidth: .infinity, alignment: .leading).textSelection(.enabled).padding(6)
                }
                .frame(maxHeight: 70)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.06)))   // light tint of the window background, so it follows the chosen colour
            }
        }
    }

    var dropZone: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(style: StrokeStyle(lineWidth: 2, dash: [8]))
                .foregroundStyle(targeted ? Color.accentColor : .secondary)
                .background(RoundedRectangle(cornerRadius: 12).fill(targeted ? Color.accentColor.opacity(0.08) : .clear))
            VStack(spacing: 8) {
                Image(systemName: "music.quarternote.3").font(.system(size: 40)).foregroundStyle(.secondary)
                Text("Drag a song here (WAV, MP3, AIFF, M4A)")
                Text("or a .nrg / .cue disc image, then pick the song").font(.caption).foregroundStyle(.secondary)
                HStack { Button("Open Song…") { chooseSong() }; Button("Open CD") { state.openCD() } }
            }
        }
        .frame(maxHeight: .infinity)
        .onDrop(of: [.fileURL], isTargeted: $targeted) { providers in
            _ = providers.first?.loadObject(ofClass: URL.self) { url, _ in
                if let url { DispatchQueue.main.async { state.open(url) } }
            }
            return true
        }
    }

    func trackPicker(_ c: KeyTabState.ImageChoice) -> some View {
        let tracks = c.result.tracks ?? []
        return VStack(alignment: .leading, spacing: 6) {
            Text("Songs on \(c.url.lastPathComponent): choose one, or tick several and combine them into one song.").font(.callout)
            List(tracks) { t in
                HStack {
                    Toggle("", isOn: Binding(
                        get: { state.picked.contains(t.number) },
                        set: { on in if on { state.picked.insert(t.number) } else { state.picked.remove(t.number) } }))
                        .labelsHidden()
                    Text(String(format: "%02d", t.number)).monospacedDigit().foregroundStyle(.secondary)
                    Text(t.title)
                    Spacer()
                    Text(timeString(t.seconds)).monospacedDigit().foregroundStyle(.secondary)
                    Button("Choose") { state.pick(t, from: c) }
                }
            }
            HStack {
                Button("Select All") { state.picked = Set(tracks.map(\.number)) }
                Button("Select None") { state.picked = [] }.disabled(state.picked.isEmpty)
                Spacer()
                let n = state.picked.count, secs = tracks.filter { state.picked.contains($0.number) }.reduce(0) { $0 + $1.seconds }
                if n > 0 { Text("\(n) song(s) · about \(timeString(secs))").foregroundStyle(.secondary) }
                Button("Combine Selected") { state.combine(from: c) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(n < 2)
                    .help("Join the ticked songs into one song (neighbouring songs keep their original gap)")
            }
        }
    }

    func cdPicker(_ c: AudioCD) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text((c.album.isEmpty ? "Songs on the CD" : "Songs on \(c.album)") + ": choose one.").font(.callout)
            List(c.tracks, id: \.self) { f in
                let n = AudioCD.number(f)
                HStack {
                    Text(String(format: "%02d", n)).monospacedDigit().foregroundStyle(.secondary)
                    Text(c.names[n] ?? "Track \(n)")
                    Spacer()
                    Button("Choose") { state.pickCD(f, from: c) }
                }
            }
            if c.names.isEmpty { Text("This CD has no song names on it.").font(.caption).foregroundStyle(.secondary) }
        }
    }

    func chooseSong() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [.audio] + ["nrg", "cue"].compactMap { UTType(filenameExtension: $0) }
        if panel.runModal() == .OK, let url = panel.url { state.open(url) }
    }

    func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Use Folder"
        if panel.runModal() == .OK { state.setOutput(panel.url) }
    }
}

/// Half steps, key names, fine tune and the preview player.
struct KeyControls: View {
    @ObservedObject var state: KeyTabState
    @ObservedObject var player: KeyPlayer

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            // big half-step control
            HStack(spacing: 18) {
                Button { state.halfSteps = max(-12, state.halfSteps - 1) } label: {
                    Image(systemName: "minus").font(.title).frame(width: 44, height: 36)
                }
                .disabled(state.halfSteps <= -12 || state.running)
                .help("Down a half step")
                VStack(spacing: 2) {
                    Text(state.halfSteps == 0 ? "0" : (state.halfSteps > 0 ? "+\(state.halfSteps)" : "−\(-state.halfSteps)"))
                        .font(.system(size: 54, weight: .bold, design: .rounded)).monospacedDigit()
                    Text(halfStepLabel(state.halfSteps)).foregroundStyle(.secondary)
                }
                .frame(width: 180)
                Button { state.halfSteps = min(12, state.halfSteps + 1) } label: {
                    Image(systemName: "plus").font(.title).frame(width: 44, height: 36)
                }
                .disabled(state.halfSteps >= 12 || state.running)
                .help("Up a half step")
                Button("Reset") { state.halfSteps = 0; state.cents = 0 }
                    .disabled((state.halfSteps == 0 && state.cents == 0) || state.running)

                Divider().frame(height: 60)

                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text("Original key:")
                        Picker("", selection: $state.originalKey) {
                            Text("Not set").tag(-1)
                            ForEach(0..<12, id: \.self) { Text(keyNames[$0]).tag($0) }
                        }
                        .labelsHidden().frame(width: 100)
                    }
                    if let k = state.newKey, state.originalKey >= 0 {
                        Text("\(keyNames[state.originalKey])  →  \(k)").font(.title2).bold()
                    } else {
                        Text("Set it to see the new key's name").font(.caption).foregroundStyle(.secondary)
                    }
                }
            }

            HStack {
                Text("Fine tune:")
                Slider(value: Binding(get: { Double(state.cents) }, set: { state.cents = Int($0.rounded()) }), in: -50...50, step: 1)
                    .frame(width: 260).disabled(state.running)
                Text("\(state.cents > 0 ? "+" : "")\(state.cents) cents").monospacedDigit().frame(width: 80, alignment: .leading)
                Text("For recordings that are slightly sharp or flat (100 cents = 1 half step)").font(.caption).foregroundStyle(.secondary)
            }

            HStack(spacing: 12) {
                Text("Channels:")
                Picker("", selection: $state.channels) {
                    ForEach(ChannelMode.allCases, id: \.self) { Text($0.label).tag($0) }
                }
                .pickerStyle(.segmented).labelsHidden().frame(width: 420).disabled(state.running)
                if state.channels != .both {
                    Picker("", selection: $state.duplicate) {
                        Text("Copy the \(state.channels == .removeRight ? "left" : "right") side to both speakers").tag(true)
                        Text("Leave the \(state.channels == .removeRight ? "right" : "left") side silent").tag(false)
                    }
                    .labelsHidden().frame(width: 290).disabled(state.running)
                    .help("Copying makes a stereo song that plays the kept side through both speakers")
                }
            }

            if abs(state.halfSteps) > 5 {
                Label("Big changes can sound less natural, especially for voices.", systemImage: "info.circle")
                    .font(.caption).foregroundStyle(.orange)
            }

            // waveform of what will be saved (or of the untouched song when comparing)
            KeyWaveform(state: state, player: player)

            // preview
            HStack(spacing: 12) {
                Button { player.playing ? player.pause() : player.play() } label: {
                    Label(player.playing ? "Pause" : "Play", systemImage: player.playing ? "pause.fill" : "play.fill").frame(width: 70)
                }
                .keyboardShortcut("p", modifiers: .command)
                .disabled(state.running)
                Text(timeString(player.position)).monospacedDigit().font(.caption)
                Slider(value: Binding(get: { player.position }, set: { player.seek($0) }), in: 0...max(player.duration, 1))
                Text(timeString(player.duration)).monospacedDigit().font(.caption)
                Picker("", selection: $state.original) {
                    Text("New key").tag(false)
                    Text("Original").tag(true)
                }
                .pickerStyle(.segmented).labelsHidden().frame(width: 160)
                .help("Switch while playing to compare")
            }
            .padding(10)
            .background(RoundedRectangle(cornerRadius: 10).fill(Color.accentColor.opacity(0.08)))
        }
    }
}

/// Left and right waveforms with the play head; click or drag to move around the song.
struct KeyWaveform: View {
    @ObservedObject var state: KeyTabState
    @ObservedObject var player: KeyPlayer

    var body: some View {
        let p = state.original ? state.sourcePeaks : state.peaks
        VStack(alignment: .leading, spacing: 4) {
            GeometryReader { geo in
                let w = geo.size.width
                ZStack(alignment: .topLeading) {
                    RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.05))
                    WaveformCanvas(left: p.left, right: p.right, combined: zip(p.left, p.right).map { max($0, $1) })
                    Rectangle().fill(Color.blue).frame(width: 2, height: geo.size.height)
                        .offset(x: player.duration > 0 ? CGFloat(player.position / player.duration) * w - 1 : -1)
                        .allowsHitTesting(false)
                }
                .contentShape(Rectangle())
                .gesture(DragGesture(minimumDistance: 0).onEnded { v in
                    player.seek(Double(min(max(0, v.location.x), w) / w) * player.duration)
                })
            }
            .frame(height: 170)
            .help("Click to jump to that point in the song")
            Text(state.original ? "Showing the original song"
                 : state.channels == .both ? "Left (top) and right (bottom) channels"
                 : "Showing the song with the \(state.channels == .removeRight ? "right" : "left") side \(state.duplicate ? "replaced by a copy of the other" : "removed")")
                .font(.caption).foregroundStyle(.secondary)
        }
    }
}
