import SwiftUI
import AppKit

/// Copies an audio CD to a CUE/BIN image. macOS mounts audio CDs with one AIFF file per song
/// (the disc's raw audio, gaps included) plus a hidden .TOC.plist with the disc layout.
final class CDCopier: ObservableObject {
    struct CDTrack {
        let number: Int
        let file: URL
        let dataOffset: UInt64     // where the sound data starts inside the AIFF
        let bytes: Int64
        let littleEndian: Bool     // macOS's CD files are AIFF-C 'sowt' (little-endian); plain AIFF is big-endian
        var lba: Int64?            // first sector on the disc, from the table of contents
    }

    @Published var volume: URL?
    @Published var disc: Disc?          // reuses the Convert tab's model so names can be edited and looked up
    @Published var cdTracks: [CDTrack] = []
    @Published var outputFolder: URL? = UserDefaults.standard.url(forKey: "archiveFolder")
    @Published var running = false
    @Published var progress: Double = 0
    @Published var status = ""
    @Published var savedCue: URL?
    @Published var started: Date?       // when the current copy began, for the elapsed / remaining display
    @Published var bytesDone: Int64 = 0
    @Published var bytesTotal: Int64 = 0
    @Published var method = ""          // "direct" or "via Finder files"
    private var cancel = false
    private var observers: [NSObjectProtocol] = []

    init() {
        let nc = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didMountNotification, NSWorkspace.didUnmountNotification] {
            observers.append(nc.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in self?.scan() })
        }
        scan()
    }

    func setOutput(_ url: URL?) {
        outputFolder = url
        UserDefaults.standard.set(url, forKey: "archiveFolder")
    }

    /// Finds a mounted audio CD (a volume with a .TOC.plist) and reads its songs.
    func scan() {
        guard !running else { return }
        let vols = FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: nil, options: [.skipHiddenVolumes]) ?? []
        guard let vol = vols.first(where: { FileManager.default.fileExists(atPath: $0.appendingPathComponent(".TOC.plist").path) }) else {
            volume = nil; disc = nil; cdTracks = []; return
        }
        if vol == volume { return }
        volume = vol; savedCue = nil; status = ""
        load(vol)
    }

    func load(_ vol: URL) {
        let files = ((try? FileManager.default.contentsOfDirectory(at: vol, includingPropertiesForKeys: nil)) ?? [])
            .filter { ["aiff", "aif"].contains($0.pathExtension.lowercased()) }
        var tracks: [CDTrack] = []
        var names: [Int: String] = [:]
        for f in files {
            let base = f.deletingPathExtension().lastPathComponent
            guard let n = Int(base.prefix { $0.isNumber }), let (off, len, le) = Self.soundData(f) else { continue }
            tracks.append(CDTrack(number: n, file: f, dataOffset: off, bytes: len, littleEndian: le))
            // "3 Amazing Grace.aiff" when the disc has CD-Text; "3 Audio Track.aiff" when it doesn't
            let title = base.drop { $0.isNumber }.trimmingCharacters(in: .whitespaces)
            names[n] = title.isEmpty || title == "Audio Track" ? String(format: "Track %02d", n) : title
        }
        tracks.sort { $0.number < $1.number }
        let toc = Self.readTOC(vol, audioTracks: tracks.map(\.number))
        if let toc {   // disc position of each song, for reading the disc directly
            for i in tracks.indices { tracks[i].lba = Int64(toc.offsets[i] - 150) }
        }
        cdTracks = tracks

        let d = Disc(url: vol)
        d.loading = false
        d.album = vol.lastPathComponent == "Audio CD" ? "" : vol.lastPathComponent
        var start: Int64 = 0
        d.tracks = tracks.map { t in
            defer { start += t.bytes }
            return TrackInfo(number: t.number, title: names[t.number]!, performer: nil, seconds: Double(t.bytes) / 176400,
                             start: start, sectors: t.bytes / 2352, sectorSize: 2352, gap: nil)
        }
        d.names = names
        d.selected = Set(d.tracks.map(\.number))
        d.toc = toc
        disc = d
        if tracks.isEmpty { status = "This disc has no music tracks macOS can read." }
    }

    /// The disc layout from .TOC.plist, in MusicBrainz terms (frames, first song at 150).
    static func readTOC(_ vol: URL, audioTracks: [Int]) -> TOC? {
        guard let data = try? Data(contentsOf: vol.appendingPathComponent(".TOC.plist")),
              let plist = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              let session = (plist["Sessions"] as? [[String: Any]])?.first,
              let leadout = session["Leadout Block"] as? Int,
              let items = session["Track Array"] as? [[String: Any]] else { return nil }
        let starts = items.filter { ($0["Data"] as? Bool) != true }
            .compactMap { i -> (Int, Int)? in
                guard let p = i["Point"] as? Int, let s = i["Start Block"] as? Int else { return nil }
                return (p, s)
            }
            .sorted { $0.0 < $1.0 }
        guard !starts.isEmpty, starts.count == audioTracks.count else { return nil }
        // macOS's Start Block values already count the 2 s lead-in (the first song starts at 150)
        return TOC(exact: true, first: starts.first!.0, last: starts.last!.0, leadout: leadout,
                   offsets: starts.map { $0.1 })
    }

    /// Finds the SSND chunk of an AIFF/AIFF-C: where the samples start, how many bytes there are,
    /// and whether they're little-endian (AIFF-C with 'sowt' compression, which is what macOS's CD files use).
    static func soundData(_ url: URL) -> (UInt64, Int64, Bool)? {
        guard let fh = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? fh.close() }
        guard let head = try? fh.read(upToCount: 12), head.count == 12,
              String(data: head.prefix(4), encoding: .ascii) == "FORM" else { return nil }
        let aifc = String(data: head[8..<12], encoding: .ascii) == "AIFC"
        var littleEndian = false
        var pos: UInt64 = 12
        while true {
            try? fh.seek(toOffset: pos)
            guard let ch = try? fh.read(upToCount: 8), ch.count == 8 else { return nil }
            let id = String(data: ch.prefix(4), encoding: .ascii) ?? ""
            let size = UInt64(ch[4]) << 24 | UInt64(ch[5]) << 16 | UInt64(ch[6]) << 8 | UInt64(ch[7])
            if id == "COMM", aifc, size >= 22,
               let comm = try? fh.read(upToCount: 22), comm.count == 22 {
                littleEndian = String(data: comm[18..<22], encoding: .ascii) == "sowt"
            }
            if id == "SSND" {
                try? fh.seek(toOffset: pos + 8)
                guard let o = try? fh.read(upToCount: 8), o.count == 8 else { return nil }
                let offset = UInt64(o[0]) << 24 | UInt64(o[1]) << 16 | UInt64(o[2]) << 8 | UInt64(o[3])
                return (pos + 16 + offset, Int64(size) - 8 - Int64(offset), littleEndian)
            }
            pos += 8 + size + (size & 1)
        }
    }

    func stop() { cancel = true }

    /// The raw device behind a mounted audio CD, e.g. /dev/rdisk4.
    static func rawDevice(_ vol: URL) -> String? {
        var st = statfs()
        guard statfs(vol.path, &st) == 0 else { return nil }
        let from = withUnsafeBytes(of: st.f_mntfromname) { String(cString: $0.bindMemory(to: CChar.self).baseAddress!) }
        return from.hasPrefix("/dev/disk") ? from.replacingOccurrences(of: "/dev/disk", with: "/dev/rdisk") : nil
    }

    /// Writes <name>.bin (raw CD audio, little-endian) and <name>.cue (song starts and names).
    func save() {
        guard let disc, let folder = outputFolder, !cdTracks.isEmpty else { return }
        let base = disc.album.trimmingCharacters(in: .whitespaces).isEmpty ? "Audio CD" : disc.album
        let safe = base.map { "/:".contains($0) ? "-" : $0 }.reduce("") { $0 + String($1) }
        var name = safe
        var k = 2
        while FileManager.default.fileExists(atPath: folder.appendingPathComponent(name + ".cue").path) { name = "\(safe) (\(k))"; k += 1 }
        let cue = folder.appendingPathComponent(name + ".cue"), bin = folder.appendingPathComponent(name + ".bin")
        let tracks = cdTracks
        let titles = disc.tracks.map { numberedName($0.number, disc.name($0)) }
        let album = disc.album, artist = disc.artist
        let total = tracks.reduce(Int64(0)) { $0 + $1.bytes }
        running = true; cancel = false; progress = 0; savedCue = nil
        started = Date(); bytesDone = 0; bytesTotal = total
        // Reading the disc directly is 2-5x faster than going through the AIFF files macOS shows
        let rawFD: Int32 = tracks.allSatisfy({ $0.lba != nil }) ? (Self.rawDevice(volume!).map { cd_open($0) } ?? -1) : -1
        method = rawFD >= 0 ? "reading the disc directly" : "reading through macOS's CD files"

        DispatchQueue.global().async {
            func q(_ s: String) -> String { "\"" + s.replacingOccurrences(of: "\"", with: "'") + "\"" }
            func msf(_ bytes: Int64) -> String {
                let f = bytes / 2352
                return String(format: "%02d:%02d:%02d", f / 4500, (f / 75) % 60, f % 75)
            }
            var cueText = ""
            if !artist.isEmpty { cueText += "PERFORMER \(q(artist))\n" }
            if !album.isEmpty { cueText += "TITLE \(q(album))\n" }
            cueText += "FILE \(q(bin.lastPathComponent)) BINARY\n"
            var failure: String?
            do {
                FileManager.default.createFile(atPath: bin.path, contents: nil)
                let out = try FileHandle(forWritingTo: bin)
                defer { try? out.close() }
                var done: Int64 = 0
                for (i, t) in tracks.enumerated() {
                    cueText += String(format: "  TRACK %02d AUDIO\n", i + 1) + "    TITLE \(q(titles[i]))\n"
                    if !artist.isEmpty { cueText += "    PERFORMER \(q(artist))\n" }
                    cueText += "    INDEX 01 \(msf(done))\n"
                    DispatchQueue.main.async { self.status = "Copying song \(i + 1) of \(tracks.count)…" }
                    let src = try FileHandle(forReadingFrom: t.file)
                    defer { try? src.close() }
                    var left = t.bytes
                    while left > 0 {
                        if self.cancel { throw NSError(domain: "Copy", code: 0, userInfo: [NSLocalizedDescriptionKey: "Stopped."]) }
                        let want = Int(min(left, 2352 * 75))
                        let pos = t.bytes - left
                        var chunk: Data?
                        // 1) straight from the disc (already little-endian)
                        if rawFD >= 0, let lba = t.lba {
                            var buf = Data(count: want)
                            for _ in 0..<2 {
                                let err = buf.withUnsafeMutableBytes { cd_read_audio(rawFD, UInt64(lba + pos / 2352), UInt32(want / 2352), $0.baseAddress) }
                                if err == 0 { chunk = buf; break }
                            }
                        }
                        // 2) through the AIFF file macOS shows (big-endian), with a few tries for worn discs
                        if chunk == nil {
                            for attempt in 0..<3 {
                                try? src.seek(toOffset: t.dataOffset + UInt64(pos))
                                if var c = try? src.read(upToCount: want), c.count == want {
                                    if !t.littleEndian {             // CD images are little-endian
                                        c.withUnsafeMutableBytes { b in
                                            var j = 0
                                            while j + 1 < b.count { b.swapAt(j, j + 1); j += 2 }
                                        }
                                    }
                                    chunk = c; break
                                }
                                Thread.sleep(forTimeInterval: Double(attempt + 1) * 0.5)
                            }
                        }
                        guard let c = chunk else {
                            let at = timeString(Double(t.bytes - left) / 176400)
                            throw NSError(domain: "Copy", code: 1, userInfo: [NSLocalizedDescriptionKey:
                                "The drive couldn't read song \(i + 1) at \(at). The disc may be scratched; try cleaning it and copying again."])
                        }
                        out.write(c)
                        left -= Int64(want); done += Int64(want)
                        let p = Double(done) / Double(total), d = done
                        DispatchQueue.main.async { self.progress = p; self.bytesDone = d }
                    }
                }
                try cueText.write(to: cue, atomically: true, encoding: .utf8)
            } catch { failure = error.localizedDescription }

            if rawFD >= 0 { close(rawFD) }
            DispatchQueue.main.async {
                self.running = false
                let took = timeString(Date().timeIntervalSince(self.started ?? Date()))
                self.started = nil
                if let failure {
                    try? FileManager.default.removeItem(at: bin)
                    try? FileManager.default.removeItem(at: cue)
                    self.status = failure
                } else {
                    self.savedCue = cue
                    self.status = "Saved \(cue.lastPathComponent) and \(bin.lastPathComponent) in \(took)."
                }
            }
        }
    }

    func eject() {
        guard let v = volume else { return }
        try? NSWorkspace.shared.unmountAndEjectDevice(at: v)
    }
}

struct CopyTab: View {
    @ObservedObject var copier: CDCopier
    @ObservedObject var player: Previewer
    let openInConvert: (URL) -> Void
    @State private var lookingUp = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Save images to:").bold()
                Text(copier.outputFolder?.path ?? "Choose a folder")
                    .foregroundStyle(copier.outputFolder == nil ? .secondary : .primary)
                    .lineLimit(1).truncationMode(.middle)
                Spacer()
                Button("Choose…") { chooseFolder() }.disabled(copier.running)
            }

            if let d = copier.disc {
                DiscEditor(disc: d, copier: copier, player: player, lookingUp: $lookingUp)
            } else {
                VStack(spacing: 10) {
                    Spacer()
                    Image(systemName: "opticaldisc").font(.system(size: 48)).foregroundStyle(.secondary)
                    Text("Insert a music CD to copy it").font(.title3)
                    Text("The whole disc is saved as a CUE/BIN image, an exact copy you can burn again later.")
                        .foregroundStyle(.secondary).multilineTextAlignment(.center)
                    Spacer()
                }
                .frame(maxWidth: .infinity)
            }

            if player.playingID != nil { NowPlaying(player: player) }

            if copier.running {
                ProgressView(value: copier.progress)
                if let start = copier.started { CopyClock(copier: copier, start: start) }
            }
            HStack {
                Text(copier.status).font(.callout).foregroundStyle(copier.status.hasPrefix("Saved") ? Color.green : Color.primary)
                    .textSelection(.enabled)
                Spacer()
                if let cue = copier.savedCue {
                    Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([cue]) }
                    Button("Open in Convert Tab") { openInConvert(cue) }
                }
                if copier.disc != nil {
                    Button("Eject") { player.stop(); copier.eject() }.disabled(copier.running)
                }
                if copier.running {
                    Button("Stop") { copier.stop() }
                } else {
                    Button("Save Image") { player.stop(); copier.save() }
                        .keyboardShortcut(.defaultAction)
                        .disabled(copier.disc == nil || copier.cdTracks.isEmpty || copier.outputFolder == nil)
                        .help(copier.outputFolder == nil ? "Choose a folder to save to first" : "Copy the whole disc to a CUE/BIN image")
                }
            }
        }
        .padding(18)
        .onAppear { copier.scan() }
    }

    func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Use Folder"
        if panel.runModal() == .OK { copier.setOutput(panel.url) }
    }
}

/// Album/artist/song names for the inserted CD, with preview and MusicBrainz lookup.
struct DiscEditor: View {
    @ObservedObject var disc: Disc
    @ObservedObject var copier: CDCopier
    @ObservedObject var player: Previewer
    @Binding var lookingUp: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Image(systemName: "opticaldisc.fill").font(.title2)
                Text("\(disc.tracks.count) songs · \(timeString(disc.tracks.reduce(0) { $0 + $1.seconds }))").foregroundStyle(.secondary)
                Spacer()
                Button("Look Up Names…") { lookingUp = true }.disabled(copier.running)
            }
            HStack(spacing: 8) {
                Text("Album").foregroundStyle(.secondary)
                TextField("Album (also the image's file name)", text: $disc.album).textFieldStyle(.roundedBorder)
                Text("Artist").foregroundStyle(.secondary)
                TextField("Artist", text: $disc.artist).textFieldStyle(.roundedBorder)
            }
            .disabled(copier.running)
            List(disc.tracks) { t in
                HStack(spacing: 10) {
                    let pid = "cd#\(t.number)"
                    Button {
                        if let f = copier.cdTracks.first(where: { $0.number == t.number })?.file {
                            player.toggle(id: pid, title: disc.name(t), subtitle: disc.album, source: .file(f))
                        }
                    } label: {
                        Image(systemName: player.isPlaying(pid) ? "stop.circle.fill" : "play.circle.fill")
                            .font(.title2).foregroundStyle(player.isPlaying(pid) ? Color.red : Color.accentColor)
                    }
                    .buttonStyle(.borderless)
                    Text(String(format: "%02d", t.number)).monospacedDigit().foregroundStyle(.secondary)
                    TextField("Song name", text: Binding(get: { disc.names[t.number] ?? t.title }, set: { disc.names[t.number] = $0 }))
                        .textFieldStyle(.plain).disabled(copier.running)
                    Text(timeString(t.seconds)).monospacedDigit().foregroundStyle(.secondary)
                }
            }
            .scrollContentBackground(.hidden)
        }
        .sheet(isPresented: $lookingUp) { LookupSheet(disc: disc) }
    }
}

/// Elapsed time, read speed and an estimate of the time left, updated every second.
struct CopyClock: View {
    @ObservedObject var copier: CDCopier
    let start: Date

    var body: some View {
        TimelineView(.periodic(from: start, by: 1)) { ctx in
            let elapsed = ctx.date.timeIntervalSince(start)
            let rate = elapsed > 3 ? Double(copier.bytesDone) / elapsed : 0          // bytes per second
            let left = rate > 0 ? Double(copier.bytesTotal - copier.bytesDone) / rate : nil
            HStack(spacing: 14) {
                Label("Elapsed \(timeString(elapsed))", systemImage: "clock")
                if let left { Text("About \(timeString(left)) left") } else { Text("Estimating time left…") }
                if rate > 0 { Text(String(format: "%.1fx speed", rate / 176_400)) }
                Text(copier.method).foregroundStyle(.secondary)
                Spacer()
                Text("\(Int(copier.progress * 100))%").monospacedDigit()
            }
            .font(.caption).monospacedDigit()
        }
    }
}

/// Time since `start`, ticking every second.
struct ElapsedLabel: View {
    let start: Date
    var body: some View {
        TimelineView(.periodic(from: start, by: 1)) { ctx in
            Label("Elapsed \(timeString(ctx.date.timeIntervalSince(start)))", systemImage: "clock")
                .font(.caption).monospacedDigit().foregroundStyle(.secondary)
        }
    }
}
