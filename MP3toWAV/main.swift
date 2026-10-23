import SwiftUI
import AVFoundation
import UniformTypeIdentifiers

struct Job: Identifiable {
    let id = UUID()
    let source: URL
    var status: String = "Waiting…"
    var output: URL?
}

@MainActor
final class Converter: ObservableObject {
    @Published var jobs: [Job] = []
    // nil = save next to the original MP3; remembered between launches
    @Published var outputFolder: URL? = UserDefaults.standard.url(forKey: "outputFolder") {
        didSet { UserDefaults.standard.set(outputFolder, forKey: "outputFolder") }
    }

    func add(_ urls: [URL]) {
        for url in urls where url.pathExtension.lowercased() == "mp3" {
            let job = Job(source: url)
            jobs.append(job)
            run(job.id, url)
        }
    }

    private func run(_ id: UUID, _ src: URL) {
        update(id) { $0.status = "Converting…" }
        let name = src.deletingPathExtension().lastPathComponent + ".wav"
        let dest = (outputFolder ?? src.deletingLastPathComponent()).appendingPathComponent(name)
        Task.detached {
            do {
                try Converter.convert(src, to: dest)
                await self.update(id) { $0.status = "Done"; $0.output = dest }
            } catch {
                await self.update(id) { $0.status = "Failed: \(error.localizedDescription)" }
            }
        }
    }

    private func update(_ id: UUID, _ change: (inout Job) -> Void) {
        if let i = jobs.firstIndex(where: { $0.id == id }) { change(&jobs[i]) }
    }

    // Decode MP3 and write 16-bit PCM WAV at the original sample rate/channels
    nonisolated static func convert(_ src: URL, to dest: URL) throws {
        let input = try AVAudioFile(forReading: src)
        let fmt = input.processingFormat
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: fmt.sampleRate,
            AVNumberOfChannelsKey: fmt.channelCount,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
        ]
        let output = try AVAudioFile(forWriting: dest, settings: settings,
                                     commonFormat: fmt.commonFormat, interleaved: fmt.isInterleaved)
        let buffer = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: 32768)!
        while input.framePosition < input.length {
            try input.read(into: buffer)
            if buffer.frameLength == 0 { break }
            try output.write(from: buffer)
        }
    }
}

struct ContentView: View {
    @StateObject private var converter = Converter()
    @State private var targeted = false

    var body: some View {
        VStack(spacing: 16) {
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(style: StrokeStyle(lineWidth: 2, dash: [8]))
                .foregroundStyle(targeted ? Color.accentColor : .secondary)
                .frame(height: 140)
                .overlay {
                    VStack(spacing: 8) {
                        Image(systemName: "waveform").font(.largeTitle)
                        Text("Drop MP3 files here")
                        Button("Choose Files…", action: choose)
                    }
                }
                .onDrop(of: [.fileURL], isTargeted: $targeted) { providers in
                    for p in providers {
                        _ = p.loadObject(ofClass: URL.self) { url, _ in
                            if let url { Task { @MainActor in converter.add([url]) } }
                        }
                    }
                    return true
                }

            List(converter.jobs) { job in
                HStack {
                    Text(job.source.lastPathComponent).lineLimit(1).truncationMode(.middle)
                    Spacer()
                    if let out = job.output {
                        Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([out]) }
                    } else {
                        Text(job.status).foregroundStyle(.secondary)
                    }
                }
            }
            HStack {
                Text("Save to:")
                Text(converter.outputFolder?.path(percentEncoded: false) ?? "Same folder as each MP3")
                    .lineLimit(1).truncationMode(.middle).foregroundStyle(.secondary)
                Spacer()
                if converter.outputFolder != nil {
                    Button("Reset") { converter.outputFolder = nil }
                }
                Button("Choose Folder…", action: chooseFolder)
            }
        }
        .padding()
        .frame(minWidth: 480, minHeight: 400)
    }

    private func choose() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.mp3]
        panel.allowsMultipleSelection = true
        if panel.runModal() == .OK { converter.add(panel.urls) }
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.prompt = "Choose"
        if panel.runModal() == .OK { converter.outputFolder = panel.url }
    }
}

@main
struct MP3toWAVApp: App {
    var body: some Scene {
        WindowGroup("MP3 to WAV") { ContentView() }
            .windowResizability(.contentMinSize)
    }
}
