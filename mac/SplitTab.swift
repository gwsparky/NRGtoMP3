import SwiftUI
import UniformTypeIdentifiers
import AppKit

/// Asks the converter for a disc image's song list (no converting).
func listImage(_ url: URL, completion: @escaping (ListResult?) -> Void) {
    guard let tool = Bundle.main.url(forResource: "NrgToMp3", withExtension: nil) else { completion(nil); return }
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
        DispatchQueue.main.async { completion(r) }
    }
}

/// Saves songs as MP3 or WAV files with names and tags, in a folder named after `album` inside `folder`.
/// The songs are written as a temporary CUE/BIN disc image, which the bundled converter turns into files.
/// Callbacks arrive on the main thread.
func exportSongs(_ songs: [BurnItem], album: String, to folder: URL, wav: Bool,
                 onStatus: @escaping (String) -> Void, onLog: @escaping (String) -> Void, done: @escaping (Bool) -> Void) {
    guard let tool = Bundle.main.url(forResource: "NrgToMp3", withExtension: nil) else { done(false); return }
    let safe = String(album.map { "/:".contains($0) ? "-" : $0 })
    let temp = FileManager.default.temporaryDirectory.appendingPathComponent("export-\(UUID().uuidString)")
    DispatchQueue.global().async {
        defer { try? FileManager.default.removeItem(at: temp) }
        do {
            try FileManager.default.createDirectory(at: temp, withIntermediateDirectories: true)
            let cue = temp.appendingPathComponent(safe + ".cue"), bin = temp.appendingPathComponent(safe + ".bin")
            FileManager.default.createFile(atPath: bin.path, contents: nil)
            let fh = try FileHandle(forWritingTo: bin)
            var text = "TITLE \"\(album.replacingOccurrences(of: "\"", with: "'"))\"\nFILE \"\(bin.lastPathComponent)\" BINARY\n"
            var pos: Int64 = 0
            for (i, p) in songs.enumerated() {
                text += String(format: "  TRACK %02d AUDIO\n    INDEX 01 %02d:%02d:%02d\n", i + 1, pos / 4500, (pos / 75) % 60, pos % 75)
                pos += try BurnList.writeSong(p, to: fh)
            }
            try fh.close()
            try text.write(to: cue, atomically: true, encoding: .utf8)
            let numbered = UserDefaults.standard.bool(forKey: "numberNames") && songs.count > 1
            // "numbered" tells the converter the names need no "01 - " in front: true when they already have
            // one, and for a single song, which needs no number at all
            let meta: [String: Any] = ["album": album, "numbered": numbered || songs.count == 1,
                "titles": Dictionary(uniqueKeysWithValues: songs.enumerated().map {
                    (String($0.offset + 1), numbered ? numberedName($0.offset + 1, $0.element.title) : $0.element.title) })]
            let metaURL = temp.appendingPathComponent("names.json")
            try JSONSerialization.data(withJSONObject: meta).write(to: metaURL)

            DispatchQueue.main.async { onStatus("Saving \(songs.count) file(s) as \(wav ? "WAV" : "MP3")…") }
            let p = Process()
            p.executableURL = tool
            p.arguments = (wav ? ["--wav"] : []) + ["-o", folder.path, "--meta", metaURL.path, cue.path]
            let pipe = Pipe()
            p.standardOutput = pipe
            p.standardError = pipe
            pipe.fileHandleForReading.readabilityHandler = { h in
                let d = h.availableData
                guard !d.isEmpty, let s = String(data: d, encoding: .utf8) else { return }
                DispatchQueue.main.async { onLog(s) }
            }
            try p.run()
            p.waitUntilExit()
            pipe.fileHandleForReading.readabilityHandler = nil
            let ok = p.terminationStatus == 0
            DispatchQueue.main.async {
                if ok { NSWorkspace.shared.open(folder) }
                done(ok)
            }
        } catch {
            DispatchQueue.main.async { onStatus("Failed: \(error.localizedDescription)"); done(false) }
        }
    }
}

let audioExtensions = ["wav", "wave", "aif", "aiff", "aifc", "mp3", "m4a", "caf", "flac"]

final class SplitTabState: ObservableObject {
    struct ImageChoice { let url: URL; let result: ListResult }

    @Published var model: SplitModel?
    @Published var choice: ImageChoice?         // a disc image was opened: which song to split?
    @Published var reading = false
    @Published var running = false
    @Published var started: Date?
    @Published var status = ""
    @Published var log = ""
    @Published var outputFolder: URL? = UserDefaults.standard.url(forKey: "splitFolder") ?? UserDefaults.standard.url(forKey: "outputFolder")

    func setOutput(_ url: URL?) {
        outputFolder = url
        UserDefaults.standard.set(url, forKey: "splitFolder")
    }

    func open(_ url: URL) {
        model?.stop()
        model = nil; choice = nil; status = ""; log = ""
        let ext = url.pathExtension.lowercased()
        if audioExtensions.contains(ext) {
            model = SplitModel(item: BurnItem(title: url.deletingPathExtension().lastPathComponent, source: .file(url), seconds: 0))
        } else if ["nrg", "cue"].contains(ext) {
            reading = true
            status = "Reading \(url.lastPathComponent)…"
            listImage(url) { r in
                self.reading = false
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
        let audio = URL(fileURLWithPath: c.result.dataPath ?? c.url.path)
        choice = nil
        model = SplitModel(item: BurnItem(title: t.title, source: .nrg(audio, t, swap: c.result.swap ?? false), seconds: t.seconds))
    }

    func close() { model?.stop(); model = nil; choice = nil; status = ""; log = "" }

    func export(wav: Bool) {
        guard let m = model, let folder = outputFolder else { return }
        let parts: [BurnItem]
        do { parts = try m.makeParts() } catch { status = error.localizedDescription; return }
        m.pause()
        running = true; started = Date(); log = ""; status = "Preparing the parts…"
        exportSongs(parts, album: m.item.title, to: folder, wav: wav,
                    onStatus: { self.status = $0 }, onLog: { self.log += $0 }) { ok in
            let took = timeString(Date().timeIntervalSince(self.started ?? Date()))
            self.running = false; self.started = nil
            self.status = ok ? "Saved \(parts.count) part(s) in \(took)." : (self.status.hasPrefix("Failed") ? self.status : "Export failed (see details below).")
        }
    }
}

struct SplitTab: View {
    @ObservedObject var state: SplitTabState
    @ObservedObject var burn: BurnList
    @ObservedObject var player: Previewer
    let showBurnList: () -> Void
    @State private var targeted = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let m = state.model {
                OpenSong(m: m, state: state, burn: burn, showBurnList: showBurnList, chooseSong: chooseSong, chooseFolder: chooseFolder)
            } else {
                HStack {
                    Image(systemName: "scissors").font(.title2)
                    Text("Split a song into separate tracks").font(.headline)
                    Spacer()
                    Button("Open Song…") { chooseSong() }
                    if state.choice != nil { Button("Close") { state.close() } }
                }
                Group {
                    if let c = state.choice { trackPicker(c) } else { dropZone }
                }
                .frame(maxHeight: .infinity)
                if !state.status.isEmpty {
                    HStack { if state.reading { ProgressView().controlSize(.small) }; Text(state.status).font(.callout) }
                }
            }
        }
        .padding(18)
        .onAppear { player.stop() }
    }

    var dropZone: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(style: StrokeStyle(lineWidth: 2, dash: [8]))
                .foregroundStyle(targeted ? Color.accentColor : .secondary)
                .background(RoundedRectangle(cornerRadius: 12).fill(targeted ? Color.accentColor.opacity(0.08) : .clear))
            VStack(spacing: 8) {
                Image(systemName: "waveform").font(.system(size: 40)).foregroundStyle(.secondary)
                Text("Drag a song here (WAV, MP3, AIFF, M4A)")
                Text("or a .nrg / .cue disc image, then pick the song to split").font(.caption).foregroundStyle(.secondary)
                Button("Open Song…") { chooseSong() }
            }
        }
        .onDrop(of: [.fileURL], isTargeted: $targeted) { providers in
            _ = providers.first?.loadObject(ofClass: URL.self) { url, _ in
                if let url { DispatchQueue.main.async { state.open(url) } }
            }
            return true
        }
    }

    func trackPicker(_ c: SplitTabState.ImageChoice) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Which song from \(c.url.lastPathComponent) do you want to split?").font(.callout)
            List(c.result.tracks ?? []) { t in
                HStack {
                    Text(String(format: "%02d", t.number)).monospacedDigit().foregroundStyle(.secondary)
                    Text(t.title)
                    Spacer()
                    Text(timeString(t.seconds)).monospacedDigit().foregroundStyle(.secondary)
                    Button("Split This Song") { state.pick(t, from: c) }
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

    func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Use Folder"
        if panel.runModal() == .OK { state.setOutput(panel.url) }
    }
}

/// The Split Song tab with a song open; watches the song so the part count and buttons stay current.
struct OpenSong: View {
    @ObservedObject var m: SplitModel
    @ObservedObject var state: SplitTabState
    @ObservedObject var burn: BurnList
    let showBurnList: () -> Void
    let chooseSong: () -> Void
    let chooseFolder: () -> Void
    @AppStorage("wav") private var wav = false
    @State private var problem: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Image(systemName: "scissors").font(.title2)
                VStack(alignment: .leading, spacing: 1) {
                    Text(m.item.title).font(.headline)
                    Text(m.loading ? "Reading…" : "\(preciseTime(m.duration)) · \(m.markers.count + 1) part(s)")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Open Song…") { chooseSong() }.disabled(state.running)
                Button("Close") { state.close() }.disabled(state.running)
            }

            Group {
                if m.loading {
                    HStack { ProgressView().controlSize(.small); Text("Reading the song…") }.frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if let e = m.error {
                    Text("Couldn't read this song: \(e)").foregroundStyle(.red).frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    SplitWorkspace(m: m, problem: $problem)
                }
            }
            .frame(maxHeight: .infinity)

            if !m.loading && m.error == nil {
                HStack {
                    Text("Save parts to:").bold()
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
                HStack {
                    if state.running { ProgressView().controlSize(.small) }
                    if state.running, let s = state.started { ElapsedLabel(start: s) }
                    Text(problem ?? state.status).font(.callout).foregroundStyle(problem != nil ? Color.red : Color.primary)
                    Spacer()
                    Button("Add to Burn List") {
                        do {
                            burn.items += try m.makeParts()
                            m.pause()
                            showBurnList()
                        } catch { problem = error.localizedDescription }
                    }
                    .disabled(m.markers.isEmpty || state.running)
                    .help("Put the parts on the Burn CD tab, one after another with no gap")
                    Button(m.markers.isEmpty ? "Export Parts" : "Export \(m.markers.count + 1) Parts") { state.export(wav: wav) }
                        .keyboardShortcut(.defaultAction)
                        .disabled(m.markers.isEmpty || state.running || state.outputFolder == nil)
                        .help(state.outputFolder == nil ? "Choose a folder to save to first" : "Save each part as its own \(wav ? "WAV" : "MP3") file")
                }
                if !state.log.isEmpty {
                    ScrollView {
                        Text(state.log).font(.system(.caption, design: .monospaced))
                            .frame(maxWidth: .infinity, alignment: .leading).textSelection(.enabled).padding(6)
                    }
                    .frame(maxHeight: 80)
                    .background(RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.06)))   // light tint of the window background, so it follows the chosen colour
                }
            }
        }
    }
}
