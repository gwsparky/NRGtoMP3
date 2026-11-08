import SwiftUI
import AppKit
import UniformTypeIdentifiers

/// Song Words tab: listens to a song with Whisper (free speech-to-text that runs on this Mac, nothing is uploaded)
/// and writes down the words, which can be corrected and saved as a text file.
enum WordsSide: String, CaseIterable, Identifiable {
    case auto = "Automatic", both = "Both sides", left = "Left side", right = "Right side"
    var id: String { rawValue }
}

enum Whisper {
    static let modelName = "ggml-small.en.bin"
    static let modelURL = URL(string: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin")!

    /// whisper-cli from Homebrew ("brew install whisper-cpp").
    static var tool: URL? {
        ["/opt/homebrew/bin/whisper-cli", "/usr/local/bin/whisper-cli"].map(URL.init(fileURLWithPath:))
            .first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    static var modelFolder: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("ChurchSound")
    }

    /// The speech model (about 490 MB), downloaded the first time.
    static var model: URL? {
        let home = FileManager.default.homeDirectoryForCurrentUser
        return [modelFolder.appendingPathComponent(modelName), home.appendingPathComponent(".whisper/\(modelName)")]
            .first { FileManager.default.fileExists(atPath: $0.path) }
    }

    /// CD audio (44.1 kHz, 16-bit stereo) → the 16 kHz mono WAV Whisper wants, from one side or both.
    static func wav16k(_ pcm: Data, side: WordsSide) -> Data {
        let frames = pcm.count / 4
        var mono = [Float](repeating: 0, count: frames)
        pcm.withUnsafeBytes { (p: UnsafeRawBufferPointer) in
            let s = p.bindMemory(to: Int16.self)
            for i in 0..<frames {
                let l = Float(Int16(littleEndian: s[2 * i])), r = Float(Int16(littleEndian: s[2 * i + 1]))
                switch side {
                case .left: mono[i] = l
                case .right: mono[i] = r
                default: mono[i] = (l + r) / 2
                }
            }
        }
        // Average over each output sample's span (a simple low-pass), then pick 16 000 samples a second.
        let outCount = Int(Double(frames) * 16000 / 44100)
        var out = Data(capacity: 44 + outCount * 2)
        func u32(_ v: UInt32) { var x = v.littleEndian; out.append(Data(bytes: &x, count: 4)) }
        func u16(_ v: UInt16) { var x = v.littleEndian; out.append(Data(bytes: &x, count: 2)) }
        out.append("RIFF".data(using: .ascii)!); u32(UInt32(36 + outCount * 2)); out.append("WAVEfmt ".data(using: .ascii)!)
        u32(16); u16(1); u16(1); u32(16000); u32(32000); u16(2); u16(16)
        out.append("data".data(using: .ascii)!); u32(UInt32(outCount * 2))
        var samples = [Int16](repeating: 0, count: outCount)
        for j in 0..<outCount {
            let a = Int(Double(j) * 44100 / 16000), b = min(frames, max(a + 1, Int(Double(j + 1) * 44100 / 16000)))
            var sum: Float = 0
            for k in a..<b { sum += mono[k] }
            samples[j] = Int16(max(-32768, min(32767, sum / Float(b - a))))
        }
        samples.withUnsafeBufferPointer { out.append(Data(buffer: $0)) }
        return out
    }

    /// True when the two sides carry different things (e.g. band on one side, singers on the other).
    static func sidesDiffer(_ pcm: Data) -> Bool {
        var ll = 0.0, rr = 0.0, lr = 0.0
        pcm.withUnsafeBytes { (p: UnsafeRawBufferPointer) in
            let s = p.bindMemory(to: Int16.self)
            var i = 0
            while i + 1 < s.count {
                let l = Double(Int16(littleEndian: s[i])), r = Double(Int16(littleEndian: s[i + 1]))
                ll += l * l; rr += r * r; lr += l * r
                i += 2 * 37   // every 37th sample is plenty
            }
        }
        guard ll > 0, rr > 0 else { return ll != rr }
        return lr / (ll * rr).squareRoot() < 0.6
    }

    /// Runs Whisper on a 16 kHz WAV and returns the words, one line per sung phrase.
    static func transcribe(_ wav: Data, tool: URL, model: URL) throws -> String {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("words-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let input = dir.appendingPathComponent("song.wav"), outBase = dir.appendingPathComponent("song")
        try wav.write(to: input)
        let p = Process()
        p.executableURL = tool
        p.arguments = ["-m", model.path, "-f", input.path, "-otxt", "-of", outBase.path, "-np"]
        let err = Pipe()
        p.standardOutput = FileHandle.nullDevice
        p.standardError = err
        try p.run()
        let errData = err.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        guard p.terminationStatus == 0, let text = try? String(contentsOf: outBase.appendingPathExtension("txt"), encoding: .utf8) else {
            let msg = String(data: errData, encoding: .utf8)?.split(separator: "\n").last.map(String.init) ?? ""
            throw NSError(domain: "Words", code: 1, userInfo: [NSLocalizedDescriptionKey: "Whisper stopped with a problem. \(msg)"])
        }
        return clean(text)
    }

    /// Trims the lines and drops Whisper's sound labels like "(upbeat music)" or "[MUSIC]".
    static func clean(_ text: String) -> String {
        text.split(separator: "\n", omittingEmptySubsequences: false)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { line in
                let t = line.trimmingCharacters(in: CharacterSet(charactersIn: "()[]♪ "))
                let isLabel = (line.hasPrefix("(") && line.hasSuffix(")")) || (line.hasPrefix("[") && line.hasSuffix("]"))
                return !t.isEmpty && !isLabel
            }
            .joined(separator: "\n")
    }

    static func wordCount(_ s: String) -> Int { s.split(whereSeparator: { $0.isWhitespace }).count }
}

final class WordsTabState: NSObject, ObservableObject, URLSessionDownloadDelegate {
    struct ImageChoice { let url: URL; let result: ListResult }

    @Published var item: BurnItem?
    @Published var folder: URL?                   // where the song came from, offered when saving
    @Published var choice: ImageChoice?
    @Published var cd: AudioCD?
    @Published var side = WordsSide.auto
    @Published var words = ""
    @Published var working = false
    @Published var started: Date?
    @Published var download: Double?              // 0...1 while the speech model downloads
    @Published var status = ""
    @Published var hasTool = Whisper.tool != nil
    private var pcm = Data()
    private var downloadDone: ((Bool) -> Void)?

    func close() { item = nil; choice = nil; cd = nil; words = ""; status = ""; pcm = Data() }

    func open(_ url: URL) {
        close()
        let ext = url.pathExtension.lowercased()
        if audioExtensions.contains(ext) {
            folder = url.deletingLastPathComponent()
            load(BurnItem(title: url.deletingPathExtension().lastPathComponent, source: .file(url), seconds: 0))
        } else if ["nrg", "cue"].contains(ext) {
            working = true
            status = "Reading \(url.lastPathComponent)…"
            listImage(url) { r in
                self.working = false
                guard let r, r.error == nil, let tracks = r.tracks, !tracks.isEmpty else {
                    self.status = "Couldn't read \(url.lastPathComponent): \(r?.error ?? "unknown problem")"; return
                }
                self.status = ""
                let c = ImageChoice(url: url, result: r)
                if tracks.count == 1 { self.pick(tracks[0], from: c) } else { self.choice = c }
            }
        } else {
            status = "\(url.lastPathComponent) isn't a song or disc image this app can open."
        }
    }

    func pick(_ t: TrackInfo, from c: ImageChoice) {
        choice = nil
        folder = c.url.deletingLastPathComponent()
        load(BurnItem(title: t.title, source: .nrg(URL(fileURLWithPath: c.result.dataPath ?? c.url.path), t, swap: c.result.swap ?? false), seconds: t.seconds))
    }

    func openCD() {
        close()
        guard let found = AudioCD.inserted().first else { status = "No audio CD found. Put one in the drive, wait for it to appear, then try again."; return }
        cd = found
    }

    func pickCD(_ f: URL, from c: AudioCD) {
        cd = nil
        folder = nil
        let n = AudioCD.number(f)
        load(BurnItem(title: c.names[n] ?? String(format: "Track %02d", n), source: .file(f), seconds: 0))
    }

    func load(_ item: BurnItem) {
        working = true
        status = "Reading the song…"
        DispatchQueue.global().async {
            do {
                let pcm = try BurnList.pcm(for: item)
                DispatchQueue.main.async { self.pcm = pcm; self.item = item; self.working = false; self.status = "" }
            } catch {
                DispatchQueue.main.async { self.working = false; self.status = "Couldn't read the song: \(error.localizedDescription)" }
            }
        }
    }

    /// Listens to the song and fills in the words.
    func getWords() {
        hasTool = Whisper.tool != nil
        guard let tool = Whisper.tool else { status = "Whisper isn't installed yet (see below)."; return }
        guard let model = Whisper.model else {
            downloadModel { ok in if ok { self.getWords() } }
            return
        }
        working = true
        started = Date()
        let pcm = self.pcm, side = self.side
        DispatchQueue.global().async {
            do {
                var text: String
                var note = ""
                if side == .auto && Whisper.sidesDiffer(pcm) {
                    // Sides differ (e.g. band left, singers right): listen to each side and keep the one with the most words.
                    self.say("The two sides are different; listening to the left side…")
                    let l = try Whisper.transcribe(Whisper.wav16k(pcm, side: .left), tool: tool, model: model)
                    self.say("Listening to the right side…")
                    let r = try Whisper.transcribe(Whisper.wav16k(pcm, side: .right), tool: tool, model: model)
                    let useLeft = Whisper.wordCount(l) > Whisper.wordCount(r)
                    text = useLeft ? l : r
                    note = " (from the \(useLeft ? "left" : "right") side, where the singing is)"
                } else {
                    self.say("Listening to the song…")
                    text = try Whisper.transcribe(Whisper.wav16k(pcm, side: side == .auto ? .both : side), tool: tool, model: model)
                }
                let n = Whisper.wordCount(text)
                DispatchQueue.main.async {
                    self.words = text
                    self.working = false
                    self.status = n == 0 ? "No words heard. Try another side under Listen to." :
                        "Heard \(n) words\(note). Read them through and fix any mistakes, then save."
                }
            } catch {
                DispatchQueue.main.async { self.working = false; self.status = error.localizedDescription }
            }
        }
    }

    private func say(_ s: String) { DispatchQueue.main.async { self.status = s } }

    var suggestedName: String { String(((item?.title ?? "Song") + " - Words").filter { !"<>:\"/\\|?*".contains($0) }) + ".txt" }

    func save() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.plainText]
        panel.nameFieldStringValue = suggestedName
        if let folder { panel.directoryURL = folder }
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try (words.hasSuffix("\n") ? words : words + "\n").write(to: url, atomically: true, encoding: .utf8)
            status = "Saved \(url.lastPathComponent)"
        } catch { status = "Couldn't save: \(error.localizedDescription)" }
    }

    // MARK: speech model download (first use only)

    func downloadModel(then done: @escaping (Bool) -> Void) {
        guard download == nil else { return }
        downloadDone = done
        download = 0
        status = "Downloading the speech model (about 490 MB, only needed once)…"
        let session = URLSession(configuration: .default, delegate: self, delegateQueue: .main)
        session.downloadTask(with: Whisper.modelURL).resume()
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64, totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        if totalBytesExpectedToWrite > 0 { download = Double(totalBytesWritten) / Double(totalBytesExpectedToWrite) }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        let dest = Whisper.modelFolder.appendingPathComponent(Whisper.modelName)
        do {
            guard (downloadTask.response as? HTTPURLResponse)?.statusCode == 200 else { throw NSError(domain: "Words", code: 2, userInfo: [NSLocalizedDescriptionKey: "the server said no"]) }
            try FileManager.default.createDirectory(at: Whisper.modelFolder, withIntermediateDirectories: true)
            try? FileManager.default.removeItem(at: dest)
            try FileManager.default.moveItem(at: location, to: dest)
        } catch {
            status = "Couldn't download the speech model: \(error.localizedDescription)"
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        download = nil
        session.finishTasksAndInvalidate()
        let ok = error == nil && Whisper.model != nil
        if let error { status = "Couldn't download the speech model: \(error.localizedDescription)" }
        let done = downloadDone; downloadDone = nil
        done?(ok)
    }
}

struct WordsTab: View {
    @ObservedObject var state: WordsTabState
    @ObservedObject var player: Previewer
    @State private var targeted = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Image(systemName: "text.quote").font(.title2)
                Text(state.item?.title ?? "Write down the words of a song").font(.headline)
                Spacer()
                Button("Open Song…") { chooseSong() }.disabled(state.working)
                Button("Open CD") { state.openCD() }.disabled(state.working)
                if state.item != nil || state.choice != nil || state.cd != nil { Button("Close") { state.close() }.disabled(state.working) }
            }

            if !state.hasTool { installNote }

            if state.item != nil {
                HStack {
                    Picker("Listen to:", selection: $state.side) {
                        ForEach(WordsSide.allCases) { Text($0.rawValue).tag($0) }
                    }
                    .frame(width: 260).disabled(state.working)
                    .help("Automatic: if the singers are on one side only (as on split tracks), it finds that side")
                    Button(state.words.isEmpty ? "Get Words" : "Get Words Again") { state.getWords() }
                        .keyboardShortcut(.defaultAction)
                        .disabled(state.working || state.download != nil)
                    Spacer()
                    Button("Copy") { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(state.words, forType: .string) }
                        .disabled(state.words.isEmpty)
                    Button("Save Words…") { state.save() }.disabled(state.words.isEmpty || state.working)
                }
                if let d = state.download { ProgressView(value: d) }
                HStack {
                    if state.working { ProgressView().controlSize(.small) }
                    if state.working, let s = state.started { ElapsedLabel(start: s) }
                    Text(state.status).font(.callout)
                }
                TextEditor(text: $state.words)
                    .font(.system(size: 14))
                    .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.secondary.opacity(0.4)))
                Text("The words are worked out by a computer, so check them. Most worship songs are copyrighted: showing them on a screen or in print usually needs a licence such as CCLI.")
                    .font(.caption).foregroundStyle(.secondary)
            } else if let c = state.choice {
                trackPicker(c)
            } else if let c = state.cd {
                cdPicker(c)
            } else if state.working {
                HStack { ProgressView().controlSize(.small); Text(state.status) }.frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                dropZone
                if !state.status.isEmpty { Text(state.status).font(.callout) }
            }
        }
        .padding(18)
    }

    var installNote: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("This tab needs Whisper, a free speech-to-text program. To install it, open Terminal and type:").font(.callout)
            HStack {
                Text("brew install whisper-cpp").font(.system(.body, design: .monospaced)).textSelection(.enabled)
                Button("Copy") { NSPasteboard.general.clearContents(); NSPasteboard.general.setString("brew install whisper-cpp", forType: .string) }
                Button("Check Again") { state.hasTool = Whisper.tool != nil }
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.orange.opacity(0.12)))
    }

    var dropZone: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(style: StrokeStyle(lineWidth: 2, dash: [8]))
                .foregroundStyle(targeted ? Color.accentColor : .secondary)
            VStack(spacing: 8) {
                Image(systemName: "text.quote").font(.system(size: 40)).foregroundStyle(.secondary)
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

    func trackPicker(_ c: WordsTabState.ImageChoice) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Songs on \(c.url.lastPathComponent): choose one.").font(.callout)
            List(c.result.tracks ?? []) { t in
                HStack {
                    Text(String(format: "%02d", t.number)).monospacedDigit().foregroundStyle(.secondary)
                    Text(t.title)
                    Spacer()
                    Text(timeString(t.seconds)).monospacedDigit().foregroundStyle(.secondary)
                    Button("Choose") { state.pick(t, from: c) }
                }
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
        }
    }

    func chooseSong() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [.audio] + ["nrg", "cue"].compactMap { UTType(filenameExtension: $0) }
        if panel.runModal() == .OK, let url = panel.url { state.open(url) }
    }
}
