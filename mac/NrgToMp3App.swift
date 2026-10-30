import SwiftUI
import UniformTypeIdentifiers
import AppKit
import AVFoundation

#if !BURNTEST
@main
#endif
struct NrgToMp3App: App {
    var body: some Scene {
        // the build number comes from the BUILD file (shared with the Windows app), via Info.plist
        WindowGroup("ChurchSound (Mac build \(Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "?"))") {
            RootView().frame(minWidth: 700, minHeight: 700)
        }
        .windowResizability(.contentMinSize)
    }
}

// MARK: - Model

struct TrackInfo: Codable, Identifiable {
    let number: Int
    let title: String
    let performer: String?
    let seconds: Double
    let start: Int64
    let sectors: Int64
    let sectorSize: Int
    let gap: Double?          // seconds of silence before the song on the original disc
    var id: Int { number }
}

struct ListResult: Decodable {
    let path: String
    let dataPath: String?
    let artist: String?
    let album: String?
    let note: String?
    let swap: Bool?
    let toc: TOC?
    let tracks: [TrackInfo]?
    let error: String?
}

final class Disc: ObservableObject, Identifiable {
    let url: URL
    @Published var loading = true
    @Published var album = ""
    @Published var artist = ""
    @Published var names: [Int: String] = [:]   // song names, edited or looked up; start as the disc's own
    @Published var mergeName = ""                // "Merge into one file": the file's name (empty = the album)
    @Published var note: String?
    @Published var error: String?
    @Published var tracks: [TrackInfo] = []
    @Published var selected: Set<Int> = []
    @Published var expanded = true
    var swap = false
    var toc: TOC?
    lazy var dataURL: URL = url   // file holding the audio: the image itself, or a .cue sheet's .bin
    var id: URL { url }
    init(url: URL) { self.url = url }

    func name(_ t: TrackInfo) -> String {
        let n = names[t.number]?.trimmingCharacters(in: .whitespaces) ?? ""
        return n.isEmpty ? t.title : n
    }

    @Published var namesNote: String?           // e.g. "Names loaded from Church Music.txt"

    /// The names file that sits next to the image: "Church Music.nrg" → "Church Music.txt".
    var namesFile: URL { url.deletingPathExtension().appendingPathExtension("txt") }

    /// Album, artist and song names as a simple text file anyone can write by hand.
    func namesText(for image: URL? = nil) -> String {
        var lines = ["# Song names for \((image ?? url).lastPathComponent)",
                     "# One song per line: a number, then the name. Lines starting with # are ignored."]
        if !album.isEmpty { lines.append("Album: \(album)") }
        if !artist.isEmpty { lines.append("Artist: \(artist)") }
        lines += tracks.map { "\($0.number). \(name($0))" }
        return lines.joined(separator: "\n") + "\n"
    }

    /// Reads a names file (see namesText). Numbered lines go to that song; lines without a number go
    /// to the songs in order. Returns how many songs got a name.
    @discardableResult
    func applyNames(_ text: String) -> Int {
        var numbered: [Int: String] = [:], plain: [String] = []
        for raw in text.components(separatedBy: .newlines) {
            // the invisible marker Notepad puts at the start of a file doesn't count
            let line = raw.replacingOccurrences(of: "\u{FEFF}", with: "").trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") { continue }
            let lower = line.lowercased()
            if let r = ["album:", "title:"].first(where: { lower.hasPrefix($0) }) {
                album = String(line.dropFirst(r.count)).trimmingCharacters(in: .whitespaces); continue
            }
            if let r = ["artist:", "performer:"].first(where: { lower.hasPrefix($0) }) {
                artist = String(line.dropFirst(r.count)).trimmingCharacters(in: .whitespaces); continue
            }
            // "1. Name", "01 - Name", "1) Name", "1 Name"
            if let m = line.range(of: #"^\d{1,2}\s*[.):\-]?\s+"#, options: .regularExpression),
               let n = Int(line[m].filter(\.isNumber)) {
                let name = String(line[m.upperBound...]).trimmingCharacters(in: .whitespaces)
                if !name.isEmpty { numbered[n] = name }
            } else {
                plain.append(line)
            }
        }
        var count = 0
        for (i, t) in tracks.enumerated() {
            if let n = numbered[t.number] { names[t.number] = n; count += 1 }
            else if numbered.isEmpty, i < plain.count { names[t.number] = plain[i]; count += 1 }
        }
        return count
    }

    /// Default name for "Merge into one file": the first ticked song's name.
    var firstSongName: String {
        tracks.first { selected.contains($0.number) }.map(name) ?? album
    }
}

/// "07 - Amazing Grace" when the "Number song names" option is on, so songs with the same name stay distinguishable.
func numberedName(_ number: Int, _ name: String) -> String {
    guard UserDefaults.standard.bool(forKey: "numberNames") else { return name }
    let prefix = String(format: "%02d - ", number)
    return name.hasPrefix(prefix) ? name : prefix + name
}

func timeString(_ s: Double) -> String { String(format: "%d:%02d", Int(s) / 60, Int(s) % 60) }

final class Converter: ObservableObject {
    @Published var discs: [Disc] = []
    @Published var started: Date?
    @Published var outputFolder: URL? = UserDefaults.standard.url(forKey: "outputFolder")
    @Published var log = ""
    @Published var running = false
    @Published var wav = UserDefaults.standard.bool(forKey: "wav") {
        didSet { UserDefaults.standard.set(wav, forKey: "wav") }
    }
    @Published var merge = UserDefaults.standard.bool(forKey: "merge") {   // one file per disc instead of one per song
        didSet { UserDefaults.standard.set(merge, forKey: "merge") }
    }

    var tool: URL? { Bundle.main.url(forResource: "NrgToMp3", withExtension: nil) }
    var selectedCount: Int { discs.reduce(0) { $0 + $1.selected.count } }

    func add(_ urls: [URL]) {
        for u in urls {
            var isDir: ObjCBool = false
            FileManager.default.fileExists(atPath: u.path, isDirectory: &isDir)
            let found: [URL] = isDir.boolValue
                ? ((try? FileManager.default.contentsOfDirectory(at: u, includingPropertiesForKeys: nil)) ?? [])
                    .filter { ["nrg", "cue"].contains($0.pathExtension.lowercased()) }
                    .sorted { $0.lastPathComponent < $1.lastPathComponent }
                : (["nrg", "cue"].contains(u.pathExtension.lowercased()) ? [u] : [])
            for f in found where !discs.contains(where: { $0.url == f }) {
                let d = Disc(url: f)
                discs.append(d)
                load(d)
            }
        }
    }

    /// Asks the converter for the song list (without converting anything).
    func load(_ d: Disc) {
        guard let tool else { return }
        DispatchQueue.global().async {
            let p = Process()
            p.executableURL = tool
            p.arguments = ["--list", d.url.path]
            let pipe = Pipe()
            p.standardOutput = pipe
            p.standardError = Pipe()
            try? p.run()
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            p.waitUntilExit()
            let r = try? JSONDecoder().decode(ListResult.self, from: data)
            DispatchQueue.main.async {
                d.loading = false
                guard let r else { d.error = "Couldn't read this file."; return }
                d.error = r.error
                d.album = r.album ?? d.url.deletingPathExtension().lastPathComponent
                d.note = r.note
                d.swap = r.swap ?? false
                d.toc = r.toc
                if let p = r.dataPath { d.dataURL = URL(fileURLWithPath: p) }
                d.tracks = r.tracks ?? []
                d.names = Dictionary(uniqueKeysWithValues: d.tracks.map { ($0.number, $0.title) })
                d.artist = r.artist ?? d.tracks.first?.performer ?? ""
                d.selected = Set(d.tracks.map(\.number))
                // names saved for this image last time (or written by hand) load automatically
                if let text = (try? String(contentsOf: d.namesFile, encoding: .utf8)) ?? (try? String(contentsOf: d.namesFile, encoding: .isoLatin1)) {
                    let n = d.applyNames(text)
                    d.namesNote = "\(n) name(s) loaded from \(d.namesFile.lastPathComponent)"
                }
                // names edited on github.com replace them
                if d.error == nil, !d.tracks.isEmpty, NamesSync.on { Task { await NamesSync.pull(d, automatic: true) } }
                self.objectWillChange.send()
            }
        }
    }

    func remove(_ d: Disc) {
        discs.removeAll { $0 === d }
    }

    func setOutput(_ url: URL?) {
        outputFolder = url
        UserDefaults.standard.set(url, forKey: "outputFolder")
    }

    /// Exports each disc's checked songs, one disc at a time.
    func convert() {
        let jobs = discs.filter { !$0.loading && $0.error == nil && !$0.selected.isEmpty }
        guard !jobs.isEmpty, !running, let tool else { return }
        running = true
        started = Date()
        log = ""
        var queue = jobs
        func next() {
            guard !queue.isEmpty else {
                running = false
                let took = timeString(Date().timeIntervalSince(started ?? Date()))
                started = nil
                if !log.contains("FAILED") {
                    log += "\nAll done in \(took).\n"
                    if let out = outputFolder { NSWorkspace.shared.open(out) }
                    else if let first = jobs.first { NSWorkspace.shared.open(first.url.deletingLastPathComponent()) }
                }
                return
            }
            let d = queue.removeFirst()
            let p = Process()
            p.executableURL = tool
            var args: [String] = wav ? ["--wav"] : []
            if merge { args.append("--merge") }
            if let out = outputFolder { args += ["-o", out.path] }
            if d.selected.count < d.tracks.count {
                args += ["--tracks", d.selected.sorted().map(String.init).joined(separator: ",")]
            }
            // album, artist and song names as shown (and maybe edited) in the app
            let meta: [String: Any] = ["album": d.album, "artist": d.artist,
                                       "titles": Dictionary(uniqueKeysWithValues: d.tracks.map { (String($0.number), numberedName($0.number, d.name($0))) }),
                                       "numbered": UserDefaults.standard.bool(forKey: "numberNames"),
                                       "mergeName": d.mergeName]
            let metaURL = FileManager.default.temporaryDirectory.appendingPathComponent("nrgmeta-\(UUID().uuidString).json")
            if let data = try? JSONSerialization.data(withJSONObject: meta), (try? data.write(to: metaURL)) != nil {
                args += ["--meta", metaURL.path]
            }
            p.arguments = args + [d.url.path]
            let pipe = Pipe()
            p.standardOutput = pipe
            p.standardError = pipe
            pipe.fileHandleForReading.readabilityHandler = { h in
                let data = h.availableData
                guard !data.isEmpty, let s = String(data: data, encoding: .utf8) else { return }
                // the per-image summary line is noise when the app runs one image at a time
                let shown = s.split(separator: "\n", omittingEmptySubsequences: false)
                    .filter { !$0.hasPrefix("Done:") && !$0.hasPrefix("Finished with") }.joined(separator: "\n")
                DispatchQueue.main.async { self.log += shown }
            }
            p.terminationHandler = { _ in
                pipe.fileHandleForReading.readabilityHandler = nil
                DispatchQueue.main.async { next() }
            }
            do { try p.run() } catch { log += "Couldn't start the converter: \(error.localizedDescription)\n"; running = false }
        }
        next()
    }
}

// MARK: - Preview player

/// Plays previews: songs straight out of an image (read into memory as a WAV) or ordinary audio files.
final class Previewer: NSObject, ObservableObject, AVAudioPlayerDelegate {
    enum Source { case data(() -> Data?), file(URL) }
    struct Item { let id: String; let title: String; let subtitle: String; let source: Source }
    @Published var queue: [Item] = []              // "Play All": songs played one after another
    @Published var queueIndex = 0
    @Published var paused = false
    @Published var repeatAll = false                // Player tab: start the list again after the last song
    @Published var volume: Float = (UserDefaults.standard.object(forKey: "volume") as? Float) ?? 1 {
        didSet { player?.volume = volume; UserDefaults.standard.set(volume, forKey: "volume") }
    }
    @Published var playingID: String?
    @Published var title = ""
    @Published var subtitle = ""
    @Published var position: Double = 0
    @Published var duration: Double = 0
    @Published var loading = false
    private var player: AVAudioPlayer?
    private var timer: Timer?

    func isPlaying(_ id: String) -> Bool { playingID == id }

    func toggle(id: String, title: String, subtitle: String, source: Source) {
        if isPlaying(id) { stop(); return }
        stop()
        start(Item(id: id, title: title, subtitle: subtitle, source: source))
    }

    /// Plays `items` in order from `index`, moving straight on to the next song as each one ends.
    func playAll(_ items: [Item], from index: Int = 0) {
        stop()
        guard items.indices.contains(index) else { return }
        queue = items
        queueIndex = index
        start(items[index])
    }

    var hasPrevious: Bool { !queue.isEmpty && queueIndex > 0 }
    var hasNext: Bool { !queue.isEmpty && queueIndex < queue.count - 1 }
    func next() { if hasNext { queueIndex += 1; start(queue[queueIndex]) } }
    func previous() {
        if position > 3 || !hasPrevious { seek(0) }          // like a CD player: first press goes back to the start of the song
        else { queueIndex -= 1; start(queue[queueIndex]) }
    }
    /// Jump to the last 10 seconds: with Play All, the quickest way to hear each song run into the next.
    func lastTen() { seek(max(0, duration - 10)) }

    func pause() { guard player != nil, !paused else { return }; player?.pause(); paused = true }
    func resume() { guard player != nil, paused else { return }; player?.play(); paused = false }
    func togglePause() { paused ? resume() : pause() }

    private func start(_ item: Item) {
        halt()
        let id = item.id, title = item.title, subtitle = item.subtitle, source = item.source
        loading = true
        playingID = id
        self.title = title
        self.subtitle = subtitle
        DispatchQueue.global().async {
            let p: AVAudioPlayer?
            switch source {
            case .data(let load): p = load().flatMap { try? AVAudioPlayer(data: $0) }
            case .file(let url): p = try? AVAudioPlayer(contentsOf: url)
            }
            DispatchQueue.main.async {
                self.loading = false
                guard self.playingID == id, let p else { if self.playingID == id { self.stop() }; return }
                p.delegate = self
                p.volume = self.volume
                p.play()
                self.paused = false
                self.player = p
                self.duration = p.duration
                self.timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
                    self?.position = self?.player?.currentTime ?? 0
                }
            }
        }
    }

    func seek(_ to: Double) { player?.currentTime = to; position = to }

    /// Stops the sound but keeps the Play All list.
    private func halt() {
        player?.stop(); player = nil
        timer?.invalidate(); timer = nil
        playingID = nil; position = 0; duration = 0; loading = false; paused = false
    }

    func stop() { halt(); queue = []; queueIndex = 0 }

    func audioPlayerDidFinishPlaying(_ player: AVAudioPlayer, successfully flag: Bool) {
        if hasNext { next() }
        else if repeatAll && !queue.isEmpty { queueIndex = 0; start(queue[0]) }
        else { stop() }
    }

    /// A song's audio as 44.1 kHz / 16-bit / stereo little-endian PCM: subchannel bytes dropped, byte order fixed.
    static func pcm(url: URL, track t: TrackInfo, swap: Bool) -> Data? {
        guard let fh = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? fh.close() }
        try? fh.seek(toOffset: UInt64(t.start))
        let audioBytes = 2352
        var pcm = Data(capacity: Int(t.sectors) * audioBytes)
        var left = t.sectors
        while left > 0 {
            let n = Int(min(left, 512))
            guard let chunk = try? fh.read(upToCount: n * t.sectorSize), chunk.count == n * t.sectorSize else { break }
            if t.sectorSize == audioBytes && !swap { pcm.append(chunk) }
            else {
                chunk.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
                    for s in 0..<n {
                        var sector = Data(raw[(s * t.sectorSize)..<(s * t.sectorSize + audioBytes)])
                        if swap { sector.withUnsafeMutableBytes { b in
                            var i = 0
                            while i < audioBytes { b.swapAt(i, i + 1); i += 2 }
                        } }
                        pcm.append(sector)
                    }
                }
            }
            left -= Int64(n)
        }
        return pcm
    }

    static func wav(_ pcm: Data) -> Data { wavHeader(dataBytes: pcm.count) + pcm }

    /// 44-byte header for 44.1 kHz / 16-bit / stereo PCM.
    static func wavHeader(dataBytes count: Int) -> Data {
        var h = Data()
        func u32(_ v: UInt32) { withUnsafeBytes(of: v.littleEndian) { h.append(contentsOf: $0) } }
        func u16(_ v: UInt16) { withUnsafeBytes(of: v.littleEndian) { h.append(contentsOf: $0) } }
        h.append("RIFF".data(using: .ascii)!); u32(UInt32(36 + count)); h.append("WAVE".data(using: .ascii)!)
        h.append("fmt ".data(using: .ascii)!); u32(16); u16(1); u16(2); u32(44100); u32(44100 * 4); u16(4); u16(16)
        h.append("data".data(using: .ascii)!); u32(UInt32(count))
        return h
    }
}

// MARK: - Views

struct RootView: View {
    @StateObject private var c = Converter()
    @StateObject private var burn = BurnList()
    @StateObject private var player = Previewer()
    @StateObject private var copier = CDCopier()
    @StateObject private var splitter = SplitTabState()
    @StateObject private var keyChanger = KeyTabState()
    @StateObject private var waves = WaveTabState()
    @StateObject private var playlist = BurnList()   // the Player tab's songs (same model as the burn list, so playlists work in both)
    @State private var tab = 0
    @AppStorage("backgroundColor") private var backgroundHex = ""
    @AppStorage("numberNames") private var numberNames = false   // empty = the normal window color

    var background: Color? { Color(hex: backgroundHex) }

    var body: some View {
        ZStack(alignment: .topTrailing) {
            TabView(selection: $tab) {
                ConvertTab(c: c, burn: burn, player: player) { tab = 1 }
                    .tabItem { Label("Convert", systemImage: "opticaldisc") }.tag(0)
                PlayerTab(list: playlist, burn: burn, player: player) { tab = 1 }
                    .tabItem { Label("Player", systemImage: "play.circle") }.tag(5)
                BurnTab(burn: burn, player: player)
                    .tabItem { Label("Burn CD", systemImage: "flame") }.tag(1)
                SplitTab(state: splitter, burn: burn, player: player) { tab = 1 }
                    .tabItem { Label("Split Song", systemImage: "scissors") }.tag(3)
                KeyTab(state: keyChanger, burn: burn, preview: player) { tab = 1 }
                    .tabItem { Label("Change Key", systemImage: "music.quarternote.3") }.tag(4)
                WaveTab(state: waves, player: player) { url in c.add([url]); tab = 0 }
                    .tabItem { Label("Waveform Viewer", systemImage: "waveform") }.tag(6)
                CopyTab(copier: copier, player: player) { cue in c.add([cue]); tab = 0 }
                    .tabItem { Label("Copy CD", systemImage: "square.and.arrow.down.on.square") }.tag(2)
            }
            .padding(.top, 6)

            HStack(spacing: 6) {
                Toggle("Number song names", isOn: $numberNames)
                    .help("Put the track number in front of each song name (\"07 - Amazing Grace\") in exported tags, burned CD-Text and copied CD images, so songs with the same name can be told apart")
                    .padding(.trailing, 10)
                ColorPicker("Background", selection: Binding(
                    get: { background ?? Color(nsColor: .windowBackgroundColor) },
                    set: { backgroundHex = $0.hex }
                ), supportsOpacity: false)
                if background != nil {
                    Button("Default") { backgroundHex = "" }.controlSize(.small)
                }
            }
            .font(.caption)
            .padding(.trailing, 16).padding(.top, 8)
        }
        .background((background ?? Color(nsColor: .windowBackgroundColor)).ignoresSafeArea())
        // dark background → light text, and the other way round, so everything stays readable
        .preferredColorScheme(background.map { $0.isDark ? .dark : .light })
    }
}

extension Color {
    init?(hex: String) {
        guard hex.count == 6, let v = UInt32(hex, radix: 16) else { return nil }
        self.init(.sRGB, red: Double(v >> 16 & 0xFF) / 255, green: Double(v >> 8 & 0xFF) / 255, blue: Double(v & 0xFF) / 255)
    }

    var hex: String {
        let c = NSColor(self).usingColorSpace(.sRGB) ?? .gray
        return String(format: "%02X%02X%02X", Int(c.redComponent * 255), Int(c.greenComponent * 255), Int(c.blueComponent * 255))
    }

    var isDark: Bool {
        let c = NSColor(self).usingColorSpace(.sRGB) ?? .gray
        return 0.299 * c.redComponent + 0.587 * c.greenComponent + 0.114 * c.blueComponent < 0.5
    }
}

struct ConvertTab: View {
    @ObservedObject var c: Converter
    @ObservedObject var burn: BurnList
    @ObservedObject var player: Previewer
    let showBurnList: () -> Void
    @State private var targeted = false
    @State private var namingMerge = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text("Save to:").bold()
                Text(c.outputFolder?.path ?? "Same folder as each .nrg file")
                    .foregroundStyle(c.outputFolder == nil ? .secondary : .primary)
                    .lineLimit(1).truncationMode(.middle)
                Spacer()
                if c.outputFolder != nil { Button("Reset") { c.setOutput(nil) } }
                Button("Choose…") { chooseOutput() }
            }
            HStack {
                Text("Format:").bold()
                Picker("", selection: $c.wav) {
                    Text("MP3 (256 kbps, smaller)").tag(false)
                    Text("WAV (CD quality, lossless)").tag(true)
                }
                .pickerStyle(.radioGroup).horizontalRadioGroupLayout().labelsHidden()
                .disabled(c.running)
                Toggle("Merge into one file", isOn: $c.merge)
                    .disabled(c.running)
                    .help("Join each disc's ticked songs into a single file named after the album, keeping the original gaps between neighbouring songs")
                    .padding(.leading, 16)
                Spacer()
            }

            ZStack {
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(style: StrokeStyle(lineWidth: 2, dash: [8]))
                    .foregroundStyle(targeted ? Color.accentColor : .secondary)
                    .background(RoundedRectangle(cornerRadius: 12).fill(targeted ? Color.accentColor.opacity(0.08) : .clear))
                if c.discs.isEmpty {
                    VStack(spacing: 8) {
                        Image(systemName: "opticaldisc").font(.system(size: 40)).foregroundStyle(.secondary)
                        Text("Drag .nrg or .cue disc images (or folders) here")
                        Button("Add Files…") { chooseFiles() }
                    }
                } else {
                    // each disc's header (name, album, artist, buttons) stays pinned at the top while its songs scroll
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 0, pinnedViews: [.sectionHeaders]) {
                            ForEach(c.discs) { d in
                                DiscSection(disc: d, player: player, disabled: c.running, merge: c.merge, wav: c.wav, onRemove: {
                                    if player.playingID?.hasPrefix(d.url.path + "#") == true { player.stop() }
                                    c.remove(d)
                                }, onAddToBurn: {
                                    burn.addTracks(from: d)
                                    showBurnList()
                                })
                            }
                        }
                    }
                    .clipShape(RoundedRectangle(cornerRadius: 10))
                    .padding(6)
                }
            }
            .frame(minHeight: 260)
            .onDrop(of: [.fileURL], isTargeted: $targeted) { providers in
                for p in providers {
                    _ = p.loadObject(ofClass: URL.self) { url, _ in
                        if let url { DispatchQueue.main.async { c.add([url]) } }
                    }
                }
                return true
            }

            if player.playingID != nil { NowPlaying(player: player) }

            HStack {
                Button("Add Files…") { chooseFiles() }.disabled(c.running)
                Button("Clear") { player.stop(); c.discs.removeAll() }.disabled(c.discs.isEmpty || c.running)
                Button("Uncheck All Discs") { for d in c.discs { d.selected = [] }; c.objectWillChange.send() }
                    .disabled(c.selectedCount == 0 || c.running)
                    .help("Untick every song on every disc in the list")
                Spacer()
                if c.running { ProgressView().controlSize(.small) }
                if let start = c.started { ElapsedLabel(start: start) }
                Text("\(c.selectedCount) song(s) selected").foregroundStyle(.secondary)
                Button((c.merge ? "Export as One " : "Export ") + (c.wav ? "WAV" : "MP3")) {
                    player.stop()
                    if c.merge { namingMerge = true } else { c.convert() }   // merging: confirm the file name(s) first
                }
                    .buttonStyle(.borderedProminent)   // no Return shortcut: George pressed Return while renaming and exported by accident
                    .disabled(c.selectedCount == 0 || c.running)
            }

            ScrollViewReader { proxy in
                ScrollView {
                    Text(c.log.isEmpty ? "Export progress will appear here." : c.log)
                        .font(.system(.caption, design: .monospaced))
                        .foregroundStyle(c.log.isEmpty ? .secondary : .primary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                        .padding(8)
                    Color.clear.frame(height: 1).id("end")
                }
                .background(RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.06)))   // light tint of the window background, so it follows the chosen colour
                .onChange(of: c.log) { _ in proxy.scrollTo("end") }
            }
            .frame(minHeight: 110)
        }
        .padding(18)
        .sheet(isPresented: $namingMerge) { MergeNamesSheet(c: c) }
    }

    func chooseOutput() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Use Folder"
        if panel.runModal() == .OK { c.setOutput(panel.url) }
    }

    func chooseFiles() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = true
        panel.allowedContentTypes = ["nrg", "cue"].compactMap { UTType(filenameExtension: $0) } + [.folder]
        if panel.runModal() == .OK { c.add(panel.urls) }
    }
}

struct DiscSection: View {
    @ObservedObject var disc: Disc
    @ObservedObject var player: Previewer
    let disabled: Bool
    let merge: Bool
    let wav: Bool
    let onRemove: () -> Void
    let onAddToBurn: () -> Void
    @State private var lookingUp = false
    @State private var syncSetup = false

    func pid(_ t: TrackInfo) -> String { disc.url.path + "#\(t.number)" }

    func saveNames() {
        let text = disc.namesText()
        do {
            try text.write(to: disc.namesFile, atomically: true, encoding: .utf8)
            disc.namesNote = "Names saved to \(disc.namesFile.lastPathComponent); they'll load automatically next time"
            Task { await NamesSync.push(disc, text: text) }
        } catch {
            // the image's folder may be read-only (e.g. on a CD): save somewhere else instead
            let panel = NSSavePanel()
            panel.nameFieldStringValue = disc.namesFile.lastPathComponent
            panel.allowedContentTypes = [.plainText]
            panel.message = "Couldn't save next to the image. Choose where to save the names file."
            if panel.runModal() == .OK, let url = panel.url {
                do {
                    try text.write(to: url, atomically: true, encoding: .utf8); disc.namesNote = "Names saved to \(url.lastPathComponent)"
                    Task { await NamesSync.push(disc, text: text) }
                }
                catch { disc.namesNote = "Couldn't save the names: \(error.localizedDescription)" }
            }
        }
    }

    func loadNames() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.plainText, .text]
        panel.directoryURL = disc.url.deletingLastPathComponent()
        panel.message = "Choose a text file of song names"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        guard let text = (try? String(contentsOf: url, encoding: .utf8)) ?? (try? String(contentsOf: url, encoding: .isoLatin1)) else {
            disc.namesNote = "Couldn't read \(url.lastPathComponent)"; return
        }
        let n = disc.applyNames(text)
        disc.namesNote = "\(n) of \(disc.tracks.count) name(s) loaded from \(url.lastPathComponent)"
    }

    func playAll(from index: Int) {
        let url = disc.dataURL, swap = disc.swap
        player.playAll(disc.tracks.map { t in
            Previewer.Item(id: pid(t), title: disc.name(t), subtitle: disc.album,
                           source: .data { Previewer.pcm(url: url, track: t, swap: swap).map(Previewer.wav) })
        }, from: index)
    }

    @AppStorage("backgroundColor") private var backgroundHex = ""

    /// The pinned header needs a solid background, or the songs would show through it as they scroll past.
    var headerBackground: Color { Color(hex: backgroundHex) ?? Color(nsColor: .windowBackgroundColor) }

    var ready: Bool { disc.expanded && !disc.loading && disc.error == nil }

    var body: some View {
        Section {
            if ready {
                ForEach(disc.tracks) { t in
                    HStack(spacing: 10) {
                        Toggle("", isOn: Binding(
                            get: { disc.selected.contains(t.number) },
                            set: { on in if on { disc.selected.insert(t.number) } else { disc.selected.remove(t.number) } }
                        )).labelsHidden().disabled(disabled)
                        Button {
                            let url = disc.dataURL, swap = disc.swap
                            player.toggle(id: pid(t), title: disc.name(t), subtitle: disc.album,
                                          source: .data { Previewer.pcm(url: url, track: t, swap: swap).map(Previewer.wav) })
                        } label: {
                            Image(systemName: player.isPlaying(pid(t)) ? "stop.circle.fill" : "play.circle.fill")
                                .font(.title2)
                                .foregroundStyle(player.isPlaying(pid(t)) ? Color.red : Color.accentColor)
                        }
                        .buttonStyle(.borderless).help("Preview")
                        Text(String(format: "%02d", t.number)).monospacedDigit().foregroundStyle(.secondary)
                        TextField("Song name", text: Binding(
                            get: { disc.names[t.number] ?? t.title },
                            set: { disc.names[t.number] = $0 }))
                            .textFieldStyle(.plain)
                            .disabled(disabled)
                            .help("Click to rename this song")
                        Text(timeString(t.seconds)).monospacedDigit().foregroundStyle(.secondary)
                    }
                    .opacity(disc.selected.contains(t.number) ? 1 : 0.5)
                    .padding(.horizontal, 10).padding(.vertical, 4)
                    .background(player.isPlaying(pid(t)) ? Color.accentColor.opacity(0.18) : Color.clear)
                    Divider().opacity(0.5)
                }
            }
        } header: {
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Button { disc.expanded.toggle() } label: {
                        Image(systemName: disc.expanded ? "chevron.down" : "chevron.right")
                    }.buttonStyle(.borderless)
                    Image(systemName: "opticaldisc")
                    VStack(alignment: .leading, spacing: 2) {
                        Text(disc.url.lastPathComponent).font(.headline)
                        if disc.loading { Text("Reading…").font(.caption).foregroundStyle(.secondary) }
                        else if let e = disc.error { Text(e).font(.caption).foregroundStyle(.red) }
                        else {
                            Text("\(disc.tracks.count) songs · \(timeString(disc.tracks.reduce(0) { $0 + $1.seconds }))"
                                 + (disc.note.map { " · " + $0 } ?? ""))
                                .font(.caption).foregroundStyle(disc.note == nil ? Color.secondary : Color.orange)
                        }
                    }
                    if disc.loading { ProgressView().controlSize(.small) }
                    Spacer()
                    if !disc.loading && disc.error == nil {
                        Button { playAll(from: 0) } label: { Label("Play All", systemImage: "play.fill") }
                            .help("Play every song in order, to check the tracks")
                        Button("Check All") { disc.selected = Set(disc.tracks.map(\.number)) }.disabled(disabled)
                            .help("Tick every song on this disc")
                        Button("Uncheck All") { disc.selected = [] }.disabled(disabled)
                            .help("Untick every song on this disc, then tick just the ones you want")
                        Button("Add to Burn List") { onAddToBurn() }
                            .disabled(disc.selected.isEmpty)
                            .help("Add the checked songs to the Burn CD tab")
                    }
                    Button { onRemove() } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.borderless).disabled(disabled).help("Remove")
                }
                if ready {
                    HStack(spacing: 8) {
                        Text("Album").foregroundStyle(.secondary)
                        TextField("Album", text: $disc.album).textFieldStyle(.roundedBorder)
                        Text("Artist").foregroundStyle(.secondary)
                        TextField("Artist", text: $disc.artist).textFieldStyle(.roundedBorder)
                        Button("Look Up Names…") { lookingUp = true }
                            .disabled(disabled)
                            .help("Find the album and song names on MusicBrainz")
                        Button("Save Names") { saveNames() }
                            .disabled(disabled)
                            .help("Save the album, artist and song names to \(disc.namesFile.lastPathComponent) next to the image; they load automatically next time")
                        Button("Load Names…") { loadNames() }
                            .disabled(disabled)
                            .help("Read song names from a text file: one song per line, e.g. \"1. Amazing Grace\"")
                        Button("Get from GitHub") { Task { await NamesSync.pull(disc, automatic: false) } }
                            .disabled(disabled || !NamesSync.on)
                            .help("Load the names for this disc from the \"names\" folder on GitHub (e.g. after editing them on github.com)")
                        Button("GitHub…") { syncSetup = true }
                            .help("Turn GitHub name syncing on or off, and add the access key that lets Save Names upload")
                    }
                    .disabled(disabled)
                    if let note = disc.namesNote {
                        Text(note).font(.caption).foregroundStyle(.secondary)
                    }
                    if merge {
                        HStack(spacing: 8) {
                            Image(systemName: "rectangle.stack.fill").foregroundStyle(.secondary)
                            Text("Merged file name").foregroundStyle(.secondary)
                            TextField(disc.firstSongName, text: $disc.mergeName)
                                .textFieldStyle(.roundedBorder)
                                .help("Name of the single file this disc's ticked songs are merged into. Leave empty to use the first ticked song's name. You can change it again when you export.")
                            Text(wav ? ".wav" : ".mp3").foregroundStyle(.secondary)
                        }
                        .disabled(disabled)
                    }
                }
            }
            .padding(.horizontal, 10).padding(.vertical, 8)
            .background(headerBackground)
            .overlay(alignment: .bottom) { Divider() }
            .sheet(isPresented: $lookingUp) { LookupSheet(disc: disc) }
            .sheet(isPresented: $syncSetup) { NamesSyncSheet() }
        }
    }
}

struct NowPlaying: View {
    @ObservedObject var player: Previewer

    var body: some View {
        HStack(spacing: 12) {
            Button { player.stop() } label: { Image(systemName: "stop.fill") }.help("Stop")
            if !player.queue.isEmpty {
                Button { player.previous() } label: { Image(systemName: "backward.end.fill") }
                    .help("Previous song (or back to the start of this one)")
                Button { player.next() } label: { Image(systemName: "forward.end.fill") }
                    .disabled(!player.hasNext).help("Next song")
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(player.title).bold().lineLimit(1)
                Text(player.queue.isEmpty ? player.subtitle : "Song \(player.queueIndex + 1) of \(player.queue.count) · \(player.subtitle)")
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            .frame(width: 200, alignment: .leading)
            if player.loading {
                ProgressView().controlSize(.small)
                Text("Loading…").foregroundStyle(.secondary)
                Spacer()
            } else {
                Text(timeString(player.position)).monospacedDigit().font(.caption)
                Slider(value: Binding(get: { player.position }, set: { player.seek($0) }), in: 0...max(player.duration, 1))
                Text(timeString(player.duration)).monospacedDigit().font(.caption)
                Button("Last 10 s") { player.lastTen() }
                    .help("Jump to the last 10 seconds, to check where this song ends (and, with Play All, how the next one starts)")
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.accentColor.opacity(0.10)))
    }
}

/// Shown when "Export as One" is clicked: the name of the file each disc will create, ready to change.
struct MergeNamesSheet: View {
    @ObservedObject var c: Converter
    @Environment(\.dismiss) private var dismiss

    var jobs: [Disc] { c.discs.filter { !$0.loading && $0.error == nil && !$0.selected.isEmpty } }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Name the Merged File\(jobs.count == 1 ? "" : "s")").font(.title2).bold()
            Text("Each disc's ticked songs are joined into one \(c.wav ? "WAV" : "MP3") file with this name.")
                .foregroundStyle(.secondary)
            ForEach(jobs) { d in MergeNameRow(disc: d, ext: c.wav ? ".wav" : ".mp3") }
            HStack {
                Text("Saved in: \(c.outputFolder?.path ?? "the same folder as each disc image")")
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button(jobs.count == 1 ? "Create File" : "Create \(jobs.count) Files") { dismiss(); c.convert() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(jobs.contains { $0.mergeName.trimmingCharacters(in: .whitespaces).isEmpty })
            }
        }
        .padding(20)
        .frame(width: 640)
        .onAppear {
            // pre-fill: whatever was typed under the disc, otherwise the first ticked song's name
            for d in jobs where d.mergeName.trimmingCharacters(in: .whitespaces).isEmpty { d.mergeName = d.firstSongName }
        }
    }
}

struct MergeNameRow: View {
    @ObservedObject var disc: Disc
    let ext: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("\(disc.url.lastPathComponent) · \(disc.selected.count) song(s)").font(.caption).foregroundStyle(.secondary)
            HStack {
                TextField("File name", text: $disc.mergeName).textFieldStyle(.roundedBorder)
                Text(ext).foregroundStyle(.secondary)
                Button("Song Name") { disc.mergeName = disc.firstSongName }
                    .help("Use the first ticked song's name: \(disc.firstSongName)")
                Button("Album Name") { disc.mergeName = disc.album }
                    .help("Use the album name: \(disc.album)")
            }
        }
    }
}
