import SwiftUI
import AVFoundation

extension String {
    /// Every match of a regular expression, in order.
    func ranges(of pattern: String) -> [Range<String.Index>] {
        var out: [Range<String.Index>] = []
        var from = startIndex
        while let r = range(of: pattern, options: .regularExpression, range: from..<endIndex) {
            out.append(r)
            from = r.upperBound
        }
        return out
    }
}

/// "1:23.45" — minutes, seconds and hundredths.
func preciseTime(_ s: Double) -> String {
    let cs = Int((max(0, s) * 100).rounded())
    return String(format: "%d:%02d.%02d", cs / 6000, (cs / 100) % 60, cs % 100)
}

/// Accepts "1:23.45", "83.45" or "1:23".
func parseTime(_ text: String) -> Double? {
    let parts = text.trimmingCharacters(in: .whitespaces).split(separator: ":")
    guard (1...2).contains(parts.count), let last = Double(parts.last!) else { return nil }
    if parts.count == 2 { guard let m = Double(parts[0]) else { return nil }; return m * 60 + last }
    return last
}

// MARK: - Model

/// One song being split: its audio, a waveform overview, the split points, and a small player.
final class SplitModel: NSObject, ObservableObject, AVAudioPlayerDelegate {
    let item: BurnItem
    @Published var loading = true
    @Published var error: String?
    @Published var peaks: [Float] = []             // loudest of the two channels
    @Published var peaksL: [Float] = []
    @Published var peaksR: [Float] = []
    @Published var markers: [Double] = []          // split points in seconds, sorted
    @Published var names: [String?] = [nil]        // one per part; nil = the automatic name
    @Published var position: Double = 0
    @Published var playing = false
    private(set) var pcm = Data()
    private(set) var duration: Double = 0
    private var player: AVAudioPlayer?
    private var timer: Timer?
    private var stopAt: Double?

    static let frame = 1.0 / 75                    // CD tracks can only start on these steps
    static let minPart = 1.0                       // shortest part allowed, in seconds

    init(item: BurnItem) {
        self.item = item
        super.init()
        DispatchQueue.global().async {
            do {
                let pcm = try BurnList.pcm(for: item)
                let (left, right) = Self.peaks(pcm, count: 1200)
                let player = try AVAudioPlayer(data: Previewer.wav(pcm))
                DispatchQueue.main.async {
                    self.pcm = pcm
                    self.duration = Double(pcm.count) / 176_400
                    self.peaksL = left
                    self.peaksR = right
                    self.peaks = zip(left, right).map { max($0, $1) }
                    self.player = player
                    player.delegate = self
                    player.prepareToPlay()
                    self.loading = false
                }
            } catch {
                DispatchQueue.main.async { self.error = error.localizedDescription; self.loading = false }
            }
        }
    }

    /// Loudest sample of each channel in each slice of the song, 0...1, for drawing the waveform.
    static func peaks(_ pcm: Data, count: Int) -> ([Float], [Float]) {
        pcm.withUnsafeBytes { raw -> ([Float], [Float]) in
            let s = raw.bindMemory(to: Int16.self)
            let frames = s.count / 2                 // a frame is one left sample followed by one right sample
            guard frames > 0 else { return ([], []) }
            let per = max(1, frames / count)
            let step = max(1, per / 400)             // look at a few hundred frames per slice
            var left = [Float](), right = [Float]()
            for b in 0..<count {
                var l: Int32 = 0, r: Int32 = 0
                var f = b * per
                let end = min(frames, f + per)
                while f < end {
                    l = max(l, abs(Int32(Int16(littleEndian: s[2 * f]))))
                    r = max(r, abs(Int32(Int16(littleEndian: s[2 * f + 1]))))
                    f += step
                }
                left.append(Float(l) / 32768)
                right.append(Float(r) / 32768)
            }
            return (left, right)
        }
    }

    // parts
    var cuts: [Double] { [0] + markers + [duration] }
    func range(_ k: Int) -> (Double, Double) { (cuts[k], cuts[k + 1]) }
    func defaultName(_ k: Int) -> String { "\(item.title) (Part \(k + 1))" }
    func name(_ k: Int) -> String {
        let n = names[k]?.trimmingCharacters(in: .whitespaces) ?? ""
        return n.isEmpty ? defaultName(k) : n
    }

    static func snap(_ t: Double) -> Double { (t * 75).rounded() / 75 }

    @discardableResult
    func addMarker(at t: Double) -> Bool {
        let t = Self.snap(t)
        guard t >= Self.minPart, t <= duration - Self.minPart,
              !markers.contains(where: { abs($0 - t) < Self.minPart }) else { return false }
        let i = markers.firstIndex { $0 > t } ?? markers.count
        markers.insert(t, at: i)
        names.insert(nil, at: i + 1)                 // the part being split keeps its name; the new one starts unnamed
        return true
    }

    /// Applies a typed list of part start times (with optional names). Returns how many split points
    /// were made and which times couldn't be used (past the end, or too close to another split).
    func applyTimes(_ entries: [(start: Double, name: String?)], replace: Bool) -> (added: Int, skipped: [Double]) {
        if replace { markers = []; names = [nil] }
        var added = 0, skipped: [Double] = []
        for e in entries.sorted(by: { $0.start < $1.start }) where e.start > 0.001 {
            if addMarker(at: e.start) { added += 1 } else { skipped.append(e.start) }
        }
        // names go to whichever part now starts at (or nearest to) each typed time
        for e in entries {
            guard let n = e.name, !n.isEmpty else { continue }
            let k = cuts.dropLast().enumerated().min { abs($0.element - e.start) < abs($1.element - e.start) }!.offset
            if abs(cuts[k] - e.start) < 0.05 { names[k] = n }
        }
        return (added, skipped)
    }

    func moveMarker(_ i: Int, to t: Double) {
        let lo = (i == 0 ? 0 : markers[i - 1]) + Self.minPart
        let hi = (i == markers.count - 1 ? duration : markers[i + 1]) - Self.minPart
        guard lo <= hi else { return }
        markers[i] = Self.snap(min(max(t, lo), hi))
    }

    /// Removing a split point joins the two parts around it; the first one's name is kept.
    func removeMarker(_ i: Int) {
        markers.remove(at: i)
        names.remove(at: i + 1)
    }

    // playback
    func togglePlay() { playing ? pause() : play() }

    func play(from t: Double? = nil, until end: Double? = nil) {
        guard let player else { return }
        if let t { player.currentTime = t; position = t }
        stopAt = end
        player.play()
        playing = true
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 1.0 / 30, repeats: true) { [weak self] _ in
            guard let self, let p = self.player else { return }
            self.position = p.currentTime
            if let e = self.stopAt, p.currentTime >= e { self.pause() }
        }
    }

    func pause() {
        player?.pause()
        playing = false
        stopAt = nil
        timer?.invalidate(); timer = nil
        position = player?.currentTime ?? position
    }

    func seek(_ t: Double) {
        let t = min(max(0, t), duration)
        player?.currentTime = t
        position = t
    }

    func stop() { pause(); player = nil }

    func audioPlayerDidFinishPlaying(_ player: AVAudioPlayer, successfully flag: Bool) {
        playing = false; timer?.invalidate(); timer = nil; position = duration
    }

    // result

    /// Turns the split points into burn-list songs: the first part keeps the original gap; the rest
    /// follow with no gap, so the music plays straight through while the CD gets separate tracks.
    func makeParts() throws -> [BurnItem] {
        let totalBytes = Int64(pcm.count)
        let byteCuts = cuts.enumerated().map { i, t -> Int64 in
            i == cuts.count - 1 ? totalBytes : min(totalBytes, Int64((t * 75).rounded()) * 2352)
        }
        // songs from audio files are decoded once into a CD-format file that the parts point into
        var rawFile: URL?
        var rawBase: Int64 = 0
        switch item.source {
        case .file:
            let dir = FileManager.default.temporaryDirectory.appendingPathComponent("ChurchSound splits")
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let f = dir.appendingPathComponent(UUID().uuidString + ".pcm")
            try pcm.write(to: f)
            rawFile = f
        case .raw(let url, let offset, _):
            rawFile = url; rawBase = offset
        case .nrg:
            break
        }

        return (0..<(cuts.count - 1)).map { k in
            let a = byteCuts[k], b = byteCuts[k + 1]
            let source: BurnItem.Source
            switch item.source {
            case .nrg(let url, let t, let swap):
                let first = a / 2352, sectors = (b - a + 2351) / 2352
                let part = TrackInfo(number: t.number, title: name(k), performer: t.performer, seconds: Double(b - a) / 176_400,
                                     start: t.start + first * Int64(t.sectorSize), sectors: sectors, sectorSize: t.sectorSize, gap: nil)
                source = .nrg(url, part, swap: swap)
            default:
                source = .raw(rawFile!, offset: rawBase + a, length: b - a)
            }
            let (s, e) = range(k)
            return BurnItem(title: name(k), source: source, seconds: Double(b - a) / 176_400,
                            gap: k == 0 ? item.gap : 0,
                            note: "Part \(k + 1) of \(cuts.count - 1) from “\(item.title)” · \(preciseTime(s))–\(preciseTime(e))")
        }
    }
}

// MARK: - View

struct SplitEditor: View {
    @StateObject private var m: SplitModel
    let onDone: ([BurnItem]) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var problem: String?

    init(item: BurnItem, onDone: @escaping ([BurnItem]) -> Void) {
        _m = StateObject(wrappedValue: SplitModel(item: item))
        self.onDone = onDone
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Image(systemName: "scissors").font(.title2)
                VStack(alignment: .leading) {
                    Text("Split Track Creator").font(.title2).bold()
                    Text(m.item.title + (m.loading ? "" : " · \(preciseTime(m.duration))")).foregroundStyle(.secondary)
                }
            }

            if m.loading {
                HStack { ProgressView().controlSize(.small); Text("Reading the song…") }
                    .frame(maxWidth: .infinity, minHeight: 300)
            } else if let e = m.error {
                Text("Couldn't read this song: \(e)").foregroundStyle(.red).frame(maxWidth: .infinity, minHeight: 300)
            } else {
                SplitWorkspace(m: m, problem: $problem)
            }

            HStack {
                Text("Parts follow each other with no gap, so the music plays straight through.")
                    .font(.caption).foregroundStyle(.secondary)
                Spacer()
                if let problem { Text(problem).font(.caption).foregroundStyle(.red) }
                Button("Cancel") { m.stop(); dismiss() }.keyboardShortcut(.cancelAction)
                Button(m.markers.isEmpty ? "Create Tracks" : "Create \(m.markers.count + 1) Tracks") { create() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(m.loading || m.markers.isEmpty)
            }
        }
        .padding(20)
        .frame(width: 900, height: 680)
    }

    func create() {
        do {
            let parts = try m.makeParts()
            m.stop()
            onDone(parts)
            dismiss()
        } catch { problem = error.localizedDescription }
    }
}


/// A song's waveform: left channel above right (or one combined lane), each labelled.
struct WaveformCanvas: View {
    let left: [Float]
    let right: [Float]
    let combined: [Float]
    var stereo = true

    var body: some View {
        Canvas { ctx, size in
            let lanes: [[Float]] = stereo ? [left, right] : [combined]
            let laneH = size.height / CGFloat(lanes.count)
            for (li, peaks) in lanes.enumerated() where !peaks.isEmpty {
                let bw = size.width / CGFloat(peaks.count)
                let mid = laneH * (CGFloat(li) + 0.5)
                var path = Path()
                for (i, p) in peaks.enumerated() {
                    let ph = max(1, CGFloat(p) * (laneH - 6))
                    path.addRect(CGRect(x: CGFloat(i) * bw, y: mid - ph / 2, width: max(1, bw), height: ph))
                }
                ctx.fill(path, with: .color(li == 0 ? Color.accentColor.opacity(0.75) : Color.teal.opacity(0.75)))
            }
            if stereo {   // divider and channel labels
                ctx.fill(Path(CGRect(x: 0, y: laneH - 0.5, width: size.width, height: 1)), with: .color(.secondary.opacity(0.4)))
                ctx.draw(Text("L").font(.caption2.bold()).foregroundColor(.secondary), at: CGPoint(x: size.width - 10, y: 9))
                ctx.draw(Text("R").font(.caption2.bold()).foregroundColor(.secondary), at: CGPoint(x: size.width - 10, y: laneH + 9))
            }
        }
    }
}

/// The editing area shared by the Split pop-up (Burn tab) and the Split Song tab:
/// waveform, play controls and the list of parts.
struct SplitWorkspace: View {
    @ObservedObject var m: SplitModel
    @Binding var problem: String?
    @State private var dragMarker: Int?
    @State private var dragDecided = false
    @State private var typedTime = ""
    @AppStorage("stereoWaveform") private var stereo = true
    @State private var enteringTimes = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            waveform
            transport
            partsList
        }
        .sheet(isPresented: $enteringTimes) {
            TimeListSheet(m: m) { problem = $0 }
        }
    }

    func addTyped() {
        guard let t = parseTime(typedTime) else { problem = "Type a time like 1:23.45 or 83.5"; return }
        guard t > 0, t < m.duration else { problem = "\(typedTime) is outside the song (it's \(preciseTime(m.duration)) long)."; return }
        if m.addMarker(at: t) { problem = nil; typedTime = "" }
        else { problem = "\(preciseTime(t)) is too close to the start, the end, or another split point." }
    }

    // the song's waveform, split points (red) and play head (blue)
    var waveform: some View {
        GeometryReader { geo in
            let w = geo.size.width, h = geo.size.height
            let x = { (t: Double) -> CGFloat in m.duration > 0 ? CGFloat(t / m.duration) * w : 0 }
            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.05))
                WaveformCanvas(left: m.peaksL, right: m.peaksR, combined: m.peaks, stereo: stereo)
                // shade alternate parts so each one is easy to see
                ForEach(0..<(m.markers.count + 1), id: \.self) { k in
                    let (a, b) = m.range(k)
                    Rectangle().fill(k % 2 == 0 ? Color.clear : Color.primary.opacity(0.06))
                        .frame(width: max(0, x(b) - x(a)), height: h).offset(x: x(a))
                        .allowsHitTesting(false)
                    Text("\(k + 1)").font(.caption2).bold().foregroundStyle(.secondary)
                        .offset(x: x(a) + 4, y: 3).allowsHitTesting(false)
                }
                ForEach(Array(m.markers.enumerated()), id: \.offset) { i, t in
                    Rectangle().fill(Color.red).frame(width: dragMarker == i ? 3 : 2, height: h).offset(x: x(t) - 1)
                        .allowsHitTesting(false)
                }
                Rectangle().fill(Color.blue).frame(width: 2, height: h).offset(x: x(m.position) - 1)
                    .allowsHitTesting(false)
            }
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0)
                .onChanged { v in
                    let t = Double(min(max(0, v.location.x), w) / w) * m.duration
                    if !dragDecided {     // starting on a red marker moves it; anywhere else moves the play head
                        dragMarker = m.markers.firstIndex { abs(x($0) - v.startLocation.x) < 6 }
                        dragDecided = true
                    }
                    if let i = dragMarker { m.moveMarker(i, to: t) } else { m.seek(t) }
                }
                .onEnded { _ in dragMarker = nil; dragDecided = false })
        }
        .frame(height: stereo ? 200 : 150)
        .help("Click to move the play head. Drag a red split point to move it.")
    }

    var transport: some View {
        HStack(spacing: 12) {
            Button { m.togglePlay() } label: {
                Label(m.playing ? "Pause" : "Play", systemImage: m.playing ? "pause.fill" : "play.fill").frame(width: 70)
            }
            .keyboardShortcut("p", modifiers: .command)
            Button { m.seek(m.position - 5) } label: { Image(systemName: "gobackward.5") }.help("Back 5 seconds")
            Button { m.seek(m.position + 5) } label: { Image(systemName: "goforward.5") }.help("Forward 5 seconds")
            Text("\(preciseTime(m.position)) / \(preciseTime(m.duration))").monospacedDigit().font(.title3)
            Picker("", selection: $stereo) {
                Text("Stereo").tag(true)
                Text("Combined").tag(false)
            }
            .pickerStyle(.segmented).labelsHidden().frame(width: 150)
            .help("Show the left and right channels separately, or as one waveform")
            Spacer()
            Text("Split at").foregroundStyle(.secondary)
            TextField("1:23.45", text: $typedTime)
                .textFieldStyle(.roundedBorder).monospacedDigit().frame(width: 80)
                .onSubmit(addTyped)
                .help("Type a time (minutes:seconds.hundredths, or just seconds) and press Return")
            Button("Add") { addTyped() }.disabled(typedTime.isEmpty)
            Button("Enter Times…") { enteringTimes = true }
                .help("Type or paste a list of times (and names) to make all the split points at once")
            Divider().frame(height: 20)
            Button {
                problem = m.addMarker(at: m.position) ? nil : "Too close to the start, the end, or another split point."
            } label: { Label("Add Split Here", systemImage: "scissors") }
                .keyboardShortcut("k", modifiers: .command)
                .help("Split at the play head (⌘K). Works while playing: press it as you hear each new section begin.")
        }
    }

    var partsList: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(m.markers.isEmpty
                 ? "No split points yet. Play the song and press Add Split Here (⌘K) where each new track should start."
                 : "\(m.markers.count + 1) tracks:")
                .font(.callout).foregroundStyle(.secondary)
            List {
                ForEach(0..<(m.markers.count + 1), id: \.self) { k in
                    partRow(k)
                }
            }
            .scrollContentBackground(.hidden)
        }
    }

    func partRow(_ k: Int) -> some View {
        let (a, b) = m.range(k)
        return HStack(spacing: 8) {
            Text("\(k + 1)").monospacedDigit().bold().frame(width: 22)
            Button { m.play(from: a, until: b) } label: { Image(systemName: "play.circle.fill").font(.title3) }
                .buttonStyle(.borderless).help("Play this part")
            TextField(m.defaultName(k), text: Binding(get: { m.names[k] ?? "" }, set: { m.names[k] = $0 }))
                .textFieldStyle(.roundedBorder).frame(minWidth: 220)
            if k > 0 {
                // controls for this part's start, i.e. split point k-1
                let i = k - 1
                Button { m.moveMarker(i, to: m.markers[i] - 1) } label: { Image(systemName: "backward.fill") }.help("Start 1 s earlier")
                Button { m.moveMarker(i, to: m.markers[i] - 0.1) } label: { Image(systemName: "backward.frame.fill") }.help("Start 0.1 s earlier")
                StartField(seconds: m.markers[i]) { m.moveMarker(i, to: $0) }
                Button { m.moveMarker(i, to: m.markers[i] + 0.1) } label: { Image(systemName: "forward.frame.fill") }.help("Start 0.1 s later")
                Button { m.moveMarker(i, to: m.markers[i] + 1) } label: { Image(systemName: "forward.fill") }.help("Start 1 s later")
                Button { m.play(from: max(0, m.markers[i] - 3), until: m.markers[i] + 3) } label: { Image(systemName: "ear") }
                    .help("Hear 3 seconds either side of this split point")
            } else {
                Text(preciseTime(0)).monospacedDigit().foregroundStyle(.secondary).frame(width: 236, alignment: .center)
            }
            Text("→ \(preciseTime(b))").monospacedDigit().foregroundStyle(.secondary)
            Text("(\(timeString(b - a)))").monospacedDigit().foregroundStyle(.secondary)
            Spacer()
            if k > 0 {
                Button { m.removeMarker(k - 1) } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.borderless).help("Remove this split point (joins this part to the one before)")
            }
        }
        .buttonStyle(.borderless)
    }

}

/// A part's start time, editable by typing (e.g. 1:23.45).
struct StartField: View {
    let seconds: Double
    let onSet: (Double) -> Void
    @State private var text = ""

    var body: some View {
        TextField("", text: $text)
            .monospacedDigit().multilineTextAlignment(.center)
            .textFieldStyle(.roundedBorder).frame(width: 82)
            .onAppear { text = preciseTime(seconds) }
            .onChange(of: seconds) { s in text = preciseTime(s) }
            .onSubmit {
                // the split point may be adjusted (rounded to 1/75 s, kept clear of its neighbours); show where it landed
                if let t = parseTime(text), abs(t - seconds) > 0.005 { onSet(t) } else { text = preciseTime(seconds) }
            }
            .help("Type a start time like 1:23.45 and press Return")
    }
}

/// Type or paste a list of times, one per line, each optionally followed by a name.
struct TimeListSheet: View {
    @ObservedObject var m: SplitModel
    let report: (String?) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var text = ""
    @State private var lengths = false        // false: each time is where a part starts; true: each time is a part's length
    @State private var replace = true

    /// Lines like "1:32 Verse 2", "1:32 - Verse 2", "3. Verse 2 1:32" or just "1:32", turned into part start times.
    /// With `lengths`, each time is how long that part lasts instead of when it starts.
    static func entries(_ text: String, lengths: Bool) -> [(start: Double, name: String?)] {
        let parsed: [(Double, String?)] = text.split(whereSeparator: \.isNewline).compactMap { raw in
            let line = String(raw).trimmingCharacters(in: .whitespaces)
            // the time is the last thing on the line that looks like m:ss(.xx), or else the last plain number
            let colon = line.ranges(of: #"\d+:\d{1,2}(\.\d+)?"#)
            guard let r = colon.last ?? line.ranges(of: #"\d+(\.\d+)?"#).last, let t = parseTime(String(line[r])) else { return nil }
            var name = line.replacingCharacters(in: r, with: " ")
            name = name.replacingOccurrences(of: #"^\s*\d{1,2}[.)]\s+"#, with: "", options: .regularExpression)   // "3. " track numbers
            name = name.replacingOccurrences(of: #"[(\[]\s*[)\]]"#, with: "", options: .regularExpression)       // "Verse (1:32)" leaves "()"
            name = name.trimmingCharacters(in: CharacterSet.whitespaces.union(CharacterSet(charactersIn: "-–—:|\t")))
            return (t, name.isEmpty ? nil : name)
        }
        guard lengths else { return parsed.map { (start: $0.0, name: $0.1) } }
        var t = 0.0
        return parsed.map { e in defer { t += e.0 }; return (start: t, name: e.1) }
    }

    var starts: [(start: Double, name: String?)] { Self.entries(text, lengths: lengths) }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Enter Split Times").font(.title2).bold()
            Text("One part per line: a time, then an optional name. Song length: \(preciseTime(m.duration)).")
                .foregroundStyle(.secondary)
            Picker("The times are", selection: $lengths) {
                Text("Start times (when each part begins)").tag(false)
                Text("Lengths (how long each part lasts)").tag(true)
            }
            .pickerStyle(.radioGroup)
            TextEditor(text: $text)
                .font(.system(.body, design: .monospaced))
                .frame(height: 220)
                .overlay(alignment: .topLeading) {
                    if text.isEmpty {
                        Text(lengths ? "1:32 Introduction\n1:38 Verse 2\n2:05 Chorus" : "0:00 Introduction\n1:32 Verse 2\n3:10.50 Chorus")
                            .font(.system(.body, design: .monospaced)).foregroundStyle(.tertiary)
                            .padding(.leading, 5).allowsHitTesting(false)
                    }
                }
                .border(Color.secondary.opacity(0.3))
            let s = starts
            let inside = s.filter { $0.start > 0.001 && $0.start < m.duration }
            Text(s.isEmpty ? "No times recognised yet."
                 : "\(s.count) line(s) → \(inside.count) split point(s): " + inside.prefix(6).map { preciseTime($0.start) }.joined(separator: ", ")
                   + (inside.count > 6 ? "…" : "") + (s.contains { $0.start >= m.duration } ? "  (times past the end of the song will be skipped)" : ""))
                .font(.caption).foregroundStyle(.secondary)
            Toggle("Replace the split points already there", isOn: $replace)
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Make Split Points") {
                    let (added, skipped) = m.applyTimes(starts, replace: replace)
                    report(skipped.isEmpty ? nil
                           : "Made \(added) split point(s); skipped \(skipped.map(preciseTime).joined(separator: ", ")) (past the end or too close to another split).")
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(starts.isEmpty)
            }
        }
        .padding(20)
        .frame(width: 520)
    }
}
