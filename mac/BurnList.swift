import SwiftUI
import AVFoundation
import UniformTypeIdentifiers
import AppKit
import DiscRecording
import DiscRecordingUI

// MARK: - Model

struct BurnItem: Identifiable {
    enum Source {
        case file(URL)                                   // WAV, AIFF, MP3, M4A... anything Core Audio reads
        case nrg(URL, TrackInfo, swap: Bool)             // a song straight out of a Nero image
        case raw(URL, offset: Int64, length: Int64)      // part of a CD-audio PCM file (made by the Split Track Creator)
    }
    let id = UUID()
    var title: String
    var source: Source
    var seconds: Double
    var gap: Double?                                     // seconds of silence before this song; nil = disc default
    var note: String?                                    // where a split part came from, shown under its name
}

// MARK: - Playlists

/// A saved burn list (".nrgplaylist", JSON). Songs point at their audio files or disc images; songs that only
/// existed inside the app (split parts, key changes) are saved as WAV files in a folder beside the playlist.
struct PlaylistFile: Codable {
    struct Song: Codable {
        var title: String
        var gap: Double?
        var note: String?
        var file: String?            // an audio file
        var image: String?           // or a song inside a disc image (.nrg / the .bin of a .cue)
        var track: TrackInfo?
        var swap: Bool?
    }
    var version = 1
    var title: String
    var defaultGap: Double
    var songs: [Song]
}

let gapChoices: [Double] = [0, 0.5, 1, 2, 3, 4, 5]
func gapLabel(_ g: Double) -> String { g == 0 ? "None (seamless)" : (g == g.rounded() ? "\(Int(g)) s" : "\(g) s") }

final class BurnList: NSObject, ObservableObject {
    @Published var items: [BurnItem] = []
    @Published var defaultGap: Double = (UserDefaults.standard.object(forKey: "defaultGap") as? Double) ?? 2 {
        didSet { UserDefaults.standard.set(defaultGap, forKey: "defaultGap") }
    }
    @Published var discTitle = ""
    @Published var discArtist = ""

    /// The list's track layout as it would be burned (2 s lead-in, then gaps and songs), for MusicBrainz.
    /// Marked inexact so MusicBrainz also finds near matches: song lengths from MP3s etc. can be a little off.
    var toc: TOC? {
        guard !items.isEmpty else { return nil }
        var pos = 150, offsets: [Int] = []
        for (i, item) in items.enumerated() {
            pos += Int((gap(at: i) * 75).rounded())
            offsets.append(pos)
            pos += Int((item.seconds * 75).rounded())
        }
        return TOC(exact: false, first: 1, last: items.count, leadout: pos, offsets: offsets)
    }
    @Published var running = false
    @Published var started: Date?
    @Published var status = ""
    @Published var log = ""
    @Published var drive: String?        // name of the attached burner, nil if none
    private var burnTemp: URL?           // songs prepared for the current burn; removed when it ends

    override init() {
        super.init()
        // DiscRecording posts its events on its own notification center
        let center = DRNotificationCenter.currentRunLoop()
        center?.addObserver(self, selector: #selector(deviceChanged), name: NSNotification.Name.DRDeviceAppeared.rawValue, object: nil)
        center?.addObserver(self, selector: #selector(deviceChanged), name: NSNotification.Name.DRDeviceDisappeared.rawValue, object: nil)
        checkDrive()
    }

    @objc private func deviceChanged(_ n: Notification) { DispatchQueue.main.async { self.checkDrive() } }

    @objc private func burnStatusChanged(_ n: Notification) {
        guard let st = n.userInfo else { return }
        let state = st[DRStatusStateKey] as? String
        DispatchQueue.main.async {
            if let p = st[DRStatusPercentCompleteKey] as? Double, p >= 0 { self.status = "Burning… \(Int(p * 100))%" }
            guard state == DRStatusStateDone || state == DRStatusStateFailed else { return }
            let took = timeString(Date().timeIntervalSince(self.started ?? Date()))
            self.started = nil
            if state == DRStatusStateDone { self.status = "Burn complete in \(took)." }
            else {
                let err = (st[DRErrorStatusKey] as? [String: Any])?[DRErrorStatusErrorStringKey] as? String
                self.status = "Burn failed" + (err.map { ": \($0)" } ?? ".")
            }
            self.running = false
            DRNotificationCenter.currentRunLoop()?.removeObserver(self, name: NSNotification.Name.DRBurnStatusChanged.rawValue, object: n.object)
            if let t = self.burnTemp { try? FileManager.default.removeItem(at: t); self.burnTemp = nil }
        }
    }

    static let capacity: Double = 80 * 60
    static let leadIn: Double = 2        // the first song always gets a 2 s pregap (CD standard)

    func gap(at i: Int) -> Double { i == 0 ? 0 : (items[i].gap ?? defaultGap) }
    var totalSeconds: Double {
        Self.leadIn + items.indices.reduce(0) { $0 + gap(at: $1) + items[$1].seconds }
    }

    func addFiles(_ urls: [URL]) {
        let exts = ["wav", "wave", "aif", "aiff", "aifc", "mp3", "m4a", "caf", "flac"]
        var files: [URL] = []
        for u in urls {
            var isDir: ObjCBool = false
            FileManager.default.fileExists(atPath: u.path, isDirectory: &isDir)
            if isDir.boolValue {
                files += ((try? FileManager.default.contentsOfDirectory(at: u, includingPropertiesForKeys: nil)) ?? [])
                    .filter { exts.contains($0.pathExtension.lowercased()) }
                    .sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
            } else if exts.contains(u.pathExtension.lowercased()) { files.append(u) }
        }
        for f in files {
            guard let af = try? AVAudioFile(forReading: f) else {
                log += "Can't read \(f.lastPathComponent) as audio; skipped.\n"; continue
            }
            let secs = Double(af.length) / af.fileFormat.sampleRate
            items.append(BurnItem(title: f.deletingPathExtension().lastPathComponent, source: .file(f), seconds: secs))
        }
    }

    func addTracks(from d: Disc) {
        for t in d.tracks where d.selected.contains(t.number) {
            items.append(BurnItem(title: d.name(t), source: .nrg(d.dataURL, t, swap: d.swap), seconds: t.seconds))
        }
    }

    /// Names for songs just added from a disc image: its names file on this Mac (<image>.txt), then the one on
    /// GitHub (names/<image>.txt) if syncing is on, the same as when the image is opened on the Convert tab.
    @MainActor
    func loadNames(for image: URL, tracks: [TrackInfo], items ids: [UUID]) async {
        let d = Disc(url: image)
        d.tracks = tracks
        var from: [String] = []
        if let text = (try? String(contentsOf: d.namesFile, encoding: .utf8)) ?? (try? String(contentsOf: d.namesFile, encoding: .isoLatin1)),
           d.applyNames(text) > 0 { from.append(d.namesFile.lastPathComponent) }
        if NamesSync.on {
            do {
                if let remote = try await NamesSync.download(image), d.applyNames(remote.text) > 0 { from.append("GitHub") }
            } catch {
                log += "GitHub names for \(image.lastPathComponent): \(NamesSync.describe(error))\n"
            }
        }
        guard !from.isEmpty else { return }
        for (id, t) in zip(ids, tracks) {
            if let k = items.firstIndex(where: { $0.id == id }) { items[k].title = d.name(t) }
        }
        let onlyThisImage = items.count == ids.count     // then its names file decides the disc's title and artist
        if !d.album.isEmpty && (discTitle.isEmpty || onlyThisImage) { discTitle = d.album }
        if !d.artist.isEmpty && (discArtist.isEmpty || onlyThisImage) { discArtist = d.artist }
        status += " Names loaded from " + from.joined(separator: " and ") + "."
    }

    /// Loads every song of a disc image with its original gaps, so the burned CD matches the one it came from.
    func addImage(_ url: URL) {
        guard let tool = Bundle.main.url(forResource: "NrgToMp3", withExtension: nil) else { return }
        status = "Reading \(url.lastPathComponent)…"
        DispatchQueue.global().async {
            let p = Process()
            p.executableURL = tool
            p.arguments = ["--list", url.path]
            let pipe = Pipe()
            p.standardOutput = pipe
            p.standardError = Pipe()
            try? p.run()
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            p.waitUntilExit()
            let r = try? JSONDecoder().decode(ListResult.self, from: data)
            DispatchQueue.main.async {
                guard let r, r.error == nil, let tracks = r.tracks, !tracks.isEmpty else {
                    self.status = "Couldn't read \(url.lastPathComponent): \(r?.error ?? "unknown problem")"
                    return
                }
                let audio = URL(fileURLWithPath: r.dataPath ?? url.path)
                let added = tracks.map { BurnItem(title: $0.title, source: .nrg(audio, $0, swap: r.swap ?? false),
                                                  seconds: $0.seconds, gap: $0.gap ?? 0) }
                self.items += added
                if self.discTitle.isEmpty { self.discTitle = r.album ?? "" }
                self.status = "Added \(tracks.count) songs from \(url.lastPathComponent) with their original gaps."
                    + (r.note != nil ? " (Song breaks in this file were guessed from silences.)" : "")
                Task { await self.loadNames(for: url, tracks: tracks, items: added.map(\.id)) }
            }
        }
    }

    func checkDrive() {
        let writers = (DRDevice.devices() as? [DRDevice] ?? []).filter { $0.writesCD() }
        drive = writers.first.map { d in
            let info = d.info() ?? [:]
            return [info[DRDeviceVendorNameKey] as? String, info[DRDeviceProductNameKey] as? String]
                .compactMap { $0?.trimmingCharacters(in: .whitespaces) }.joined(separator: " ")
        }
    }

    // MARK: Disc image

    /// Writes a CUE sheet + raw BIN. Each song after the first gets exactly its gap of silence as
    /// INDEX 00 (pregap), so a gap of 0 plays straight through with no pause.
    func writeImage(cue: URL, progress: @escaping (String) -> Void) throws {
        let bin = cue.deletingPathExtension().appendingPathExtension("bin")
        FileManager.default.createFile(atPath: bin.path, contents: nil)
        let fh = try FileHandle(forWritingTo: bin)
        defer { try? fh.close() }

        func msf(_ sectors: Int64) -> String {
            String(format: "%02d:%02d:%02d", sectors / (75 * 60), (sectors / 75) % 60, sectors % 75)
        }
        func quoted(_ s: String) -> String { "\"" + s.replacingOccurrences(of: "\"", with: "'") + "\"" }

        var cueText = ""
        if !discArtist.isEmpty { cueText += "PERFORMER \(quoted(discArtist))\n" }
        if !discTitle.isEmpty { cueText += "TITLE \(quoted(discTitle))\n" }
        cueText += "FILE \(quoted(bin.lastPathComponent)) BINARY\n"
        var pos: Int64 = 0      // in 2352-byte sectors
        for (i, item) in items.enumerated() {
            progress("Preparing \(i + 1) of \(items.count): \(item.title)")
            cueText += String(format: "  TRACK %02d AUDIO\n", i + 1)
            cueText += "    TITLE \(quoted(numberedName(i + 1, item.title)))\n"
            let gapSectors = Int64((gap(at: i) * 75).rounded())
            if gapSectors > 0 {
                cueText += "    INDEX 00 \(msf(pos))\n"
                fh.write(Data(count: Int(gapSectors) * 2352))
                pos += gapSectors
            }
            cueText += "    INDEX 01 \(msf(pos))\n"
            pos += try Self.writeSong(item, to: fh)
        }
        try cueText.write(to: cue, atomically: true, encoding: .utf8)
    }

    /// Appends one song as CD audio, padded to a whole sector. Returns the number of sectors written.
    static func writeSong(_ item: BurnItem, to fh: FileHandle) throws -> Int64 {
        var bytes: Int64
        switch item.source {
        case .nrg(let url, let t, let swap):
            guard let pcm = Previewer.pcm(url: url, track: t, swap: swap) else {
                throw NSError(domain: "Burn", code: 1, userInfo: [NSLocalizedDescriptionKey: "Couldn't read \(item.title) from \(url.lastPathComponent)."])
            }
            fh.write(pcm); bytes = Int64(pcm.count)
        case .file(let url):
            bytes = try appendCDAudio(from: url, to: fh)
        case .raw(let url, let offset, let length):
            guard let pcm = Self.readRaw(url, offset: offset, length: length) else {
                throw NSError(domain: "Burn", code: 1, userInfo: [NSLocalizedDescriptionKey: "Couldn't read \(item.title)."])
            }
            fh.write(pcm); bytes = Int64(pcm.count)
        }
        let pad = (2352 - bytes % 2352) % 2352     // songs must end on a whole sector
        if pad > 0 { fh.write(Data(count: Int(pad))) }
        return (bytes + pad) / 2352
    }

    static func readRaw(_ url: URL, offset: Int64, length: Int64) -> Data? {
        guard let fh = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? fh.close() }
        try? fh.seek(toOffset: UInt64(offset))
        return try? fh.read(upToCount: Int(length))
    }

    /// A song's audio as CD-format PCM (44.1 kHz / 16-bit / stereo, little-endian), whatever its source.
    static func pcm(for item: BurnItem) throws -> Data {
        switch item.source {
        case .nrg(let url, let t, let swap):
            if let d = Previewer.pcm(url: url, track: t, swap: swap) { return d }
        case .raw(let url, let offset, let length):
            if let d = readRaw(url, offset: offset, length: length) { return d }
        case .file(let url):
            let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("decode-\(UUID().uuidString).pcm")
            FileManager.default.createFile(atPath: tmp.path, contents: nil)
            defer { try? FileManager.default.removeItem(at: tmp) }
            let fh = try FileHandle(forWritingTo: tmp)
            _ = try appendCDAudio(from: url, to: fh)
            try fh.close()
            return try Data(contentsOf: tmp)
        }
        throw NSError(domain: "Burn", code: 1, userInfo: [NSLocalizedDescriptionKey: "Couldn't read \(item.title)."])
    }

    /// Saves the list as a playlist. Songs that only exist inside the app are written as WAV files
    /// into a "<playlist> files" folder next to it. Returns how many were written that way.
    @discardableResult
    func savePlaylist(to url: URL) throws -> Int {
        let folderName = url.deletingPathExtension().lastPathComponent + " files"
        let filesDir = url.deletingLastPathComponent().appendingPathComponent(folderName)
        var written = 0
        var songs: [PlaylistFile.Song] = []
        for (i, item) in items.enumerated() {
            var song = PlaylistFile.Song(title: item.title, gap: item.gap, note: item.note)
            switch item.source {
            case .file(let u): song.file = u.path
            case .nrg(let u, let t, let swap): song.image = u.path; song.track = t; song.swap = swap
            case .raw(let u, let offset, let length):
                guard let pcm = Self.readRaw(u, offset: offset, length: length) else {
                    throw NSError(domain: "Playlist", code: 1, userInfo: [NSLocalizedDescriptionKey: "Couldn't read “\(item.title)” to save it."])
                }
                try FileManager.default.createDirectory(at: filesDir, withIntermediateDirectories: true)
                let safe = String(item.title.map { "/:\\<>|?*\"".contains($0) ? "_" : $0 }.prefix(80))
                let wav = filesDir.appendingPathComponent(String(format: "%02d - ", i + 1) + safe + ".wav")
                try Previewer.wav(pcm).write(to: wav)
                song.file = wav.path
                written += 1
            }
            songs.append(song)
        }
        let file = PlaylistFile(title: discTitle, defaultGap: defaultGap, songs: songs)
        let enc = JSONEncoder(); enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        try enc.encode(file).write(to: url)
        return written
    }

    /// Loads a playlist (replacing the list, or adding to its end). Returns the titles of songs whose
    /// audio couldn't be found (moved files, or a drive that isn't connected).
    func loadPlaylist(from url: URL, replace: Bool) throws -> [String] {
        let file = try JSONDecoder().decode(PlaylistFile.self, from: Data(contentsOf: url))
        let here = url.deletingLastPathComponent()
        /// The saved path, or (if the playlist and its files were moved together) the same name next to the playlist.
        func find(_ path: String) -> URL? {
            let u = URL(fileURLWithPath: path)
            if FileManager.default.fileExists(atPath: u.path) { return u }
            for candidate in [here.appendingPathComponent(u.lastPathComponent),
                              here.appendingPathComponent(u.deletingLastPathComponent().lastPathComponent).appendingPathComponent(u.lastPathComponent)]
                where FileManager.default.fileExists(atPath: candidate.path) { return candidate }
            return nil
        }
        var loaded: [BurnItem] = [], missing: [String] = []
        for s in file.songs {
            if let f = s.file, let u = find(f) {
                let secs = (try? AVAudioFile(forReading: u)).map { Double($0.length) / $0.fileFormat.sampleRate } ?? 0
                loaded.append(BurnItem(title: s.title, source: .file(u), seconds: secs, gap: s.gap, note: s.note))
            } else if let img = s.image, let t = s.track, let u = find(img) {
                loaded.append(BurnItem(title: s.title, source: .nrg(u, t, swap: s.swap ?? false), seconds: Double(t.sectors) / 75, gap: s.gap, note: s.note))
            } else {
                missing.append(s.title)
            }
        }
        if replace { items = loaded; discTitle = file.title; defaultGap = file.defaultGap }
        else { items += loaded; if discTitle.isEmpty { discTitle = file.title } }
        return missing
    }

    /// The list as player items, for Play All.
    var playItems: [Previewer.Item] {
        items.map { item in
            let source: Previewer.Source
            switch item.source {
            case .file(let u): source = .file(u)
            case .nrg(let u, let t, let swap): source = .data { Previewer.pcm(url: u, track: t, swap: swap).map(Previewer.wav) }
            case .raw(let u, let o, let l): source = .data { BurnList.readRaw(u, offset: o, length: l).map(Previewer.wav) }
            }
            return Previewer.Item(id: item.id.uuidString, title: item.title, subtitle: "Burn list", source: source)
        }
    }

    /// Swaps one song for the parts the Split Track Creator made from it.
    func replace(_ id: UUID, with parts: [BurnItem]) {
        guard let i = items.firstIndex(where: { $0.id == id }) else { return }
        items.replaceSubrange(i...i, with: parts)
    }

    /// Writes each song as its own CD-format WAV for DiscRecording to burn.
    func writeWavs(in dir: URL, progress: @escaping (String) -> Void) throws -> [URL] {
        try items.enumerated().map { i, item in
            progress("Preparing \(i + 1) of \(items.count): \(item.title)")
            let url = dir.appendingPathComponent(String(format: "%02d.wav", i + 1))
            FileManager.default.createFile(atPath: url.path, contents: nil)
            let fh = try FileHandle(forWritingTo: url)
            defer { try? fh.close() }
            fh.write(Data(count: 44))                       // header filled in once the size is known
            let sectors = try Self.writeSong(item, to: fh)
            try fh.seek(toOffset: 0)
            fh.write(Previewer.wavHeader(dataBytes: Int(sectors) * 2352))
            return url
        }
    }

    /// Decodes any audio file to 44.1 kHz / 16-bit / stereo little-endian PCM and appends it. Returns bytes written.
    static func appendCDAudio(from url: URL, to fh: FileHandle) throws -> Int64 {
        let file = try AVAudioFile(forReading: url)
        let inFmt = file.processingFormat
        let outFmt = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 44100, channels: 2, interleaved: true)!
        guard let conv = AVAudioConverter(from: inFmt, to: outFmt) else {
            throw NSError(domain: "Burn", code: 2, userInfo: [NSLocalizedDescriptionKey: "Can't convert \(url.lastPathComponent) to CD audio."])
        }
        if inFmt.channelCount == 1 { conv.channelMap = [0, 0] }   // mono: same sound in both speakers
        let inBuf = AVAudioPCMBuffer(pcmFormat: inFmt, frameCapacity: 32768)!
        var finished = false
        var written: Int64 = 0
        while true {
            let outBuf = AVAudioPCMBuffer(pcmFormat: outFmt, frameCapacity: 32768)!
            var err: NSError?
            let status = conv.convert(to: outBuf, error: &err) { _, outStatus in
                if finished { outStatus.pointee = .endOfStream; return nil }
                do { try file.read(into: inBuf) } catch { finished = true; outStatus.pointee = .endOfStream; return nil }
                if inBuf.frameLength == 0 { finished = true; outStatus.pointee = .endOfStream; return nil }
                outStatus.pointee = .haveData
                return inBuf
            }
            if let err { throw err }
            if outBuf.frameLength > 0 {
                let n = Int(outBuf.frameLength) * 4
                fh.write(Data(bytes: outBuf.int16ChannelData![0], count: n))
                written += Int64(n)
            }
            if status == .endOfStream || status == .error || (finished && outBuf.frameLength == 0) { break }
        }
        return written
    }

    // MARK: Actions

    func saveImage(to cue: URL) {
        running = true; log = ""
        DispatchQueue.global().async {
            do {
                try self.writeImage(cue: cue) { s in DispatchQueue.main.async { self.status = s } }
                DispatchQueue.main.async {
                    self.status = "Saved \(cue.lastPathComponent) and its .bin."
                    self.running = false
                    NSWorkspace.shared.activateFileViewerSelecting([cue])
                }
            } catch {
                DispatchQueue.main.async { self.status = "Failed: \(error.localizedDescription)"; self.running = false }
            }
        }
    }

    /// Shows the standard macOS burn sheet (drive, speed, test burn, eject), prepares the songs,
    /// then burns them with DiscRecording, setting each song's pregap to its gap.
    func burn() {
        let setup = DRBurnSetupPanel()
        setup.setCanSelectTestBurn(true)
        setup.setCanSelectAppendableMedia(false)
        guard setup.run() == NSApplication.ModalResponse.OK.rawValue, let burnObj = setup.burnObject() else { return }

        running = true; log = ""; started = Date()
        let temp = FileManager.default.temporaryDirectory.appendingPathComponent("nrgburn-\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: temp, withIntermediateDirectories: true)
        let gaps = items.indices.map { gap(at: $0) }
        let titles = items.enumerated().map { numberedName($0.offset + 1, $0.element.title) }, discTitle = discTitle, discArtist = discArtist
        DispatchQueue.global().async {
            let wavs: [URL]
            do { wavs = try self.writeWavs(in: temp) { s in DispatchQueue.main.async { self.status = s } } }
            catch {
                DispatchQueue.main.async { self.status = "Failed: \(error.localizedDescription)"; self.running = false }
                try? FileManager.default.removeItem(at: temp)
                return
            }
            DispatchQueue.main.async {
                var tracks: [DRTrack] = []
                for (i, u) in wavs.enumerated() {
                    guard let t = DRTrack(forAudioFile: u.path) else {
                        self.status = "Failed: macOS couldn't prepare song \(i + 1) for burning."
                        self.running = false
                        try? FileManager.default.removeItem(at: temp)
                        return
                    }
                    // first song: the standard 2 s; after that, exactly the chosen gap (0 = seamless)
                    t.setPreGap(DRMSF(frames: UInt32(i == 0 ? 150 : Int((gaps[i] * 75).rounded()))))
                    tracks.append(t)
                }
                // song names as CD-Text, if the burner can write it
                if let dev = burnObj.device(), ((dev.info()?[DRDeviceWriteCapabilitiesKey] as? [String: Any])?[DRDeviceCanWriteCDTextKey] as? Bool) == true,   // it's among the write capabilities, not at the top
                   let text = DRCDTextBlock(language: "en", encoding: String.Encoding.isoLatin1.rawValue) {
                    var dicts: [[String: Any]] = [[DRCDTextTitleKey: discTitle.isEmpty ? "Audio CD" : discTitle, DRCDTextPerformerKey: discArtist]]
                    dicts += titles.map { [DRCDTextTitleKey: $0, DRCDTextPerformerKey: discArtist] }
                    text.setTrackDictionaries(dicts)
                    var props = burnObj.properties() ?? [:]
                    props[DRCDTextKey] = text
                    burnObj.setProperties(props)
                }
                self.status = "Burning… insert a blank CD-R if asked."
                self.burnTemp = temp
                DRNotificationCenter.currentRunLoop()?.addObserver(self, selector: #selector(self.burnStatusChanged),
                                                                   name: NSNotification.Name.DRBurnStatusChanged.rawValue, object: burnObj)
                DRBurnProgressPanel().begin(for: burnObj, layout: tracks)
            }
        }
    }
}

// MARK: - View

struct BurnTab: View {
    @ObservedObject var burn: BurnList
    @ObservedObject var player: Previewer
    @State private var targeted = false
    @State private var selection = Set<UUID>()
    @State private var splitting: BurnItem?
    @State private var lookingUp = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Gap between songs:").bold()
                Picker("", selection: $burn.defaultGap) {
                    ForEach(gapChoices, id: \.self) { Text(gapLabel($0)).tag($0) }
                }
                .labelsHidden().frame(width: 160).disabled(burn.running)
                Text("Use “None” for songs split from one recording.").font(.caption).foregroundStyle(.secondary)
                Spacer()
            }
            HStack {
                Text("Disc title:").bold()
                TextField("Optional (saved as CD-Text where supported)", text: $burn.discTitle).disabled(burn.running)
                Button("Look Up Names…") { lookingUp = true }
                    .disabled(burn.items.isEmpty || burn.running)
                    .help("Find the album and song names on MusicBrainz and fill them in")
                Divider().frame(height: 18)
                Button { player.playAll(burn.playItems) } label: { Label("Play All", systemImage: "play.fill") }
                    .disabled(burn.items.isEmpty)
                    .help("Listen to the list in order")
                Button("Open Playlist…") { openPlaylist() }.disabled(burn.running)
                    .help("Load a saved playlist")
                Button("Save Playlist…") { savePlaylist() }.disabled(burn.items.isEmpty || burn.running)
                    .help("Save this list (songs, order, names and gaps) to open again later")
            }

            ZStack {
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(style: StrokeStyle(lineWidth: 2, dash: [8]))
                    .foregroundStyle(targeted ? Color.accentColor : .secondary)
                    .background(RoundedRectangle(cornerRadius: 12).fill(targeted ? Color.accentColor.opacity(0.08) : .clear))
                if burn.items.isEmpty {
                    VStack(spacing: 8) {
                        Image(systemName: "music.note.list").font(.system(size: 40)).foregroundStyle(.secondary)
                        Text("Drag WAV (or AIFF / MP3 / M4A) files here")
                        Text("or use “Add to Burn List” on the Convert tab").font(.caption).foregroundStyle(.secondary)
                        Button("Add Files…") { chooseFiles() }
                    }
                } else {
                    List(selection: $selection) {
                        ForEach(Array(burn.items.enumerated()), id: \.element.id) { i, item in
                            row(i, item)
                        }
                        .onMove { burn.items.move(fromOffsets: $0, toOffset: $1) }
                        .onDelete { burn.items.remove(atOffsets: $0) }
                    }
                    .scrollContentBackground(.hidden)
                    .padding(6)
                    .onDeleteCommand { burn.items.removeAll { selection.contains($0.id) }; selection = [] }
                }
            }
            .frame(minHeight: 260)
            .onDrop(of: [.fileURL], isTargeted: $targeted) { providers in
                for p in providers {
                    _ = p.loadObject(ofClass: URL.self) { url, _ in
                        if let url { DispatchQueue.main.async { burn.addFiles([url]) } }
                    }
                }
                return true
            }

            capacityBar

            if player.playingID != nil { NowPlaying(player: player) }

            HStack {
                Button("Add Files…") { chooseFiles() }.disabled(burn.running)
                Button("Add Disc Image…") { chooseImage() }.disabled(burn.running)
                    .help("Add every song from a .cue or .nrg image with its original gaps, to burn a copy of that CD")
                Button("Clear") { player.stop(); burn.items.removeAll() }.disabled(burn.items.isEmpty || burn.running)
                Spacer()
                Button("Save Disc Image…") { saveImage() }
                    .disabled(burn.items.isEmpty || burn.running)
                    .help("Save a CUE/BIN image you can burn later")
                Button("Burn CD…") { player.stop(); burn.burn() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(burn.items.isEmpty || burn.running || burn.drive == nil || burn.totalSeconds > BurnList.capacity || burn.items.count > 99)
                    .help("Choose the burner, speed and test-burn options, then burn")
            }

            HStack(spacing: 8) {
                if burn.running { ProgressView().controlSize(.small) }
                if burn.running, let start = burn.started { ElapsedLabel(start: start) }
                Image(systemName: burn.drive == nil ? "exclamationmark.triangle" : "opticaldiscdrive")
                    .foregroundStyle(burn.drive == nil ? .orange : .secondary)
                Text(burn.drive.map { "Burner: \($0)" } ?? "No CD burner found. Connect one, or use Save Disc Image to burn later.")
                    .font(.caption).foregroundStyle(.secondary)
                Button("Check Again") { burn.checkDrive() }.controlSize(.small).disabled(burn.running)
                Spacer()
            }
            if !burn.status.isEmpty {
                Text(burn.status).font(.callout).textSelection(.enabled)
            }
            if !burn.log.isEmpty {
                ScrollView {
                    Text(burn.log).font(.system(.caption, design: .monospaced))
                        .frame(maxWidth: .infinity, alignment: .leading).textSelection(.enabled).padding(6)
                }
                .frame(maxHeight: 90)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.06)))   // light tint of the window background, so it follows the chosen colour
            }
        }
        .padding(18)
        .onAppear { burn.checkDrive() }
        .sheet(isPresented: $lookingUp) {
            LookupSheet(heading: "Burn list", count: burn.items.count, toc: burn.toc,
                        startAlbum: burn.discTitle, startArtist: burn.discArtist) { p in
                if !p.album.isEmpty { burn.discTitle = p.album }
                burn.discArtist = p.artist
                for i in burn.items.indices where i < p.titles.count && !p.titles[i].isEmpty {
                    burn.items[i].title = p.titles[i]
                }
            }
        }
        .sheet(item: $splitting) { item in
            SplitEditor(item: item) { parts in burn.replace(item.id, with: parts) }
        }
    }

    func row(_ i: Int, _ item: BurnItem) -> some View {
        HStack(spacing: 10) {
            Text(String(format: "%02d", i + 1)).monospacedDigit().foregroundStyle(.secondary)
            Button {
                switch item.source {
                case .file(let url):
                    player.toggle(id: item.id.uuidString, title: item.title, subtitle: "Burn list", source: .file(url))
                case .nrg(let url, let t, let swap):
                    player.toggle(id: item.id.uuidString, title: item.title, subtitle: url.lastPathComponent,
                                  source: .data { Previewer.pcm(url: url, track: t, swap: swap).map(Previewer.wav) })
                case .raw(let url, let offset, let length):
                    player.toggle(id: item.id.uuidString, title: item.title, subtitle: item.note ?? "Split part",
                                  source: .data { BurnList.readRaw(url, offset: offset, length: length).map(Previewer.wav) })
                }
            } label: {
                Image(systemName: player.isPlaying(item.id.uuidString) ? "stop.circle.fill" : "play.circle.fill")
                    .font(.title2)
                    .foregroundStyle(player.isPlaying(item.id.uuidString) ? Color.red : Color.accentColor)
            }
            .buttonStyle(.borderless)
            VStack(alignment: .leading, spacing: 1) {
                TextField("Song name", text: Binding(
                    get: { burn.items.first { $0.id == item.id }?.title ?? item.title },
                    set: { v in if let k = burn.items.firstIndex(where: { $0.id == item.id }) { burn.items[k].title = v } }))
                    .textFieldStyle(.plain).disabled(burn.running)
                    .help("Click to rename; burned as CD-Text")
                Text(sourceLabel(item)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer()
            if i == 0 {
                Text("2 s lead-in").font(.caption).foregroundStyle(.secondary)
                    .help("Every CD starts with a 2-second pause before the first song; that's part of the CD standard.")
                    .frame(width: 150, alignment: .trailing)
            } else {
                Picker("Gap before", selection: Binding(
                    get: { burn.items[i].gap ?? -1 },
                    set: { burn.items[i].gap = $0 < 0 ? nil : $0 })) {
                    Text("Gap: default (\(gapLabel(burn.defaultGap)))").tag(-1.0)
                    Divider()
                    ForEach(gapChoices, id: \.self) { Text("Gap: \(gapLabel($0))").tag($0) }
                }
                .labelsHidden().frame(width: 190).disabled(burn.running)
                .help("Silence before this song")
            }
            Text(timeString(item.seconds)).monospacedDigit().foregroundStyle(.secondary).frame(width: 44, alignment: .trailing)
            Button { player.stop(); splitting = item } label: { Image(systemName: "scissors") }
                .buttonStyle(.borderless).disabled(burn.running)
                .help("Split Track Creator: break this song into several tracks at points you choose")
            Button { burn.items.removeAll { $0.id == item.id } } label: { Image(systemName: "xmark.circle.fill") }
                .buttonStyle(.borderless).disabled(burn.running)
        }
        .padding(.vertical, 2)
    }

    func sourceLabel(_ item: BurnItem) -> String {
        if let n = item.note { return n }
        switch item.source {
        case .file(let u): return u.lastPathComponent
        case .nrg(let u, let t, _): return "\(u.lastPathComponent), track \(t.number)"
        case .raw: return "Split part"
        }
    }

    var capacityBar: some View {
        let total = burn.totalSeconds, over = total > BurnList.capacity
        return VStack(alignment: .leading, spacing: 4) {
            ProgressView(value: min(total, BurnList.capacity), total: BurnList.capacity)
                .tint(over ? .red : total > 74 * 60 ? .orange : .accentColor)
            HStack {
                Text("\(burn.items.count) song(s) · \(timeString(total)) of 80:00 (gaps included)")
                Spacer()
                if over { Text("Too long for one CD").foregroundStyle(.red).bold() }
                else if total > 74 * 60 { Text("Needs an 80-minute CD-R").foregroundStyle(.orange) }
                if burn.items.count > 99 { Text("A CD holds at most 99 songs").foregroundStyle(.red).bold() }
            }
            .font(.caption).foregroundStyle(.secondary)
        }
    }

    func chooseFiles() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = true
        panel.allowedContentTypes = [.audio, .folder]
        if panel.runModal() == .OK { burn.addFiles(panel.urls) }
    }

    static let playlistType = UTType(filenameExtension: "nrgplaylist") ?? .json

    func savePlaylist() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = (burn.discTitle.isEmpty ? "My Playlist" : burn.discTitle) + ".nrgplaylist"
        panel.allowedContentTypes = [Self.playlistType]
        panel.message = "Songs made inside the app (split parts, key changes) are saved as WAV files in a folder next to the playlist."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let n = try burn.savePlaylist(to: url)
            burn.status = "Saved playlist \(url.lastPathComponent)" + (n > 0 ? " (\(n) song(s) saved as WAV in “\(url.deletingPathExtension().lastPathComponent) files”)" : "") + "."
        } catch { burn.status = "Couldn't save the playlist: \(error.localizedDescription)" }
    }

    func openPlaylist() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [Self.playlistType, .json]
        panel.message = "Choose a saved playlist"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        var replace = true
        if !burn.items.isEmpty {
            let a = NSAlert()
            a.messageText = "Replace the current list?"
            a.informativeText = "The list already has \(burn.items.count) song(s). Replace them with the playlist, or add the playlist's songs to the end?"
            a.addButton(withTitle: "Replace")
            a.addButton(withTitle: "Add to End")
            a.addButton(withTitle: "Cancel")
            switch a.runModal() {
            case .alertFirstButtonReturn: replace = true
            case .alertSecondButtonReturn: replace = false
            default: return
            }
        }
        player.stop()
        do {
            let missing = try burn.loadPlaylist(from: url, replace: replace)
            burn.status = "Opened \(url.lastPathComponent)."
                + (missing.isEmpty ? "" : " Couldn't find \(missing.count) song(s) (moved, or on a drive that isn't connected): " + missing.prefix(5).joined(separator: ", ") + (missing.count > 5 ? "…" : ""))
        } catch { burn.status = "Couldn't open the playlist: \(error.localizedDescription)" }
    }

    func chooseImage() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = ["cue", "nrg"].compactMap { UTType(filenameExtension: $0) }
        if panel.runModal() == .OK, let url = panel.url { burn.addImage(url) }
    }

    func saveImage() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = (burn.discTitle.isEmpty ? "Custom CD" : burn.discTitle) + ".cue"
        if let cue = UTType(filenameExtension: "cue") { panel.allowedContentTypes = [cue] }
        if panel.runModal() == .OK, let url = panel.url { burn.saveImage(to: url) }
    }
}
