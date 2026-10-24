import Foundation

/// An audio CD in the drive, as macOS shows it: a volume of "1 Audio Track.aiff" files that Core Audio can play,
/// plus the album, artist and song names burned on the disc as CD-Text (macOS itself never shows these).
struct AudioCD {
    let volume: URL
    let tracks: [URL]            // in track order
    var album = ""
    var artist = ""
    var names: [Int: String] = [:]

    /// Every audio CD that's in a drive right now.
    static func inserted() -> [AudioCD] {
        let vols = FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: nil, options: [.skipHiddenVolumes]) ?? []
        return vols.compactMap { v in
            guard FileManager.default.fileExists(atPath: v.appendingPathComponent(".TOC.plist").path) else { return nil }
            let files = ((try? FileManager.default.contentsOfDirectory(at: v, includingPropertiesForKeys: nil)) ?? [])
                .filter { $0.pathExtension.lowercased() == "aiff" }
                .sorted { number($0) < number($1) }
            guard !files.isEmpty else { return nil }
            var cd = AudioCD(volume: v, tracks: files)
            cd.readText()
            return cd
        }
    }

    static func number(_ f: URL) -> Int { Int(f.lastPathComponent.prefix { $0.isNumber }) ?? 0 }

    /// The volume's raw device ("/Volumes/Audio CD" → "/dev/rdisk5").
    var device: String? {
        var s = statfs()
        guard statfs(volume.path, &s) == 0 else { return nil }
        let dev = withUnsafePointer(to: &s.f_mntfromname) { String(cString: UnsafeRawPointer($0).assumingMemoryBound(to: CChar.self)) }
        return dev.replacingOccurrences(of: "/dev/disk", with: "/dev/rdisk")
    }

    mutating func readText() {
        guard let dev = device else { return }
        let fd = cd_open(dev)
        guard fd >= 0 else { return }
        defer { close(fd) }
        var buf = [UInt8](repeating: 0, count: 32768)
        let len = Int(cd_text(fd, &buf, UInt16(buf.count)))
        guard len > 0 else { return }
        let titles = Self.strings(buf, len, type: 0x80), performers = Self.strings(buf, len, type: 0x81)
        album = titles[0] ?? ""
        artist = performers[0] ?? ""
        for (n, t) in titles where n > 0 && !t.isEmpty { names[n] = t }
    }

    /// One kind of CD-Text (0x80 = titles, 0x81 = performers) by track number; 0 is the whole disc.
    /// Each 18-byte pack carries 12 bytes of text; names run on from pack to pack, ending with a zero byte,
    /// and a tab means "the same as the previous one". Only the first language block is read.
    static func strings(_ b: [UInt8], _ len: Int, type: UInt8) -> [Int: String] {
        var out: [Int: String] = [:], track = -1, bytes: [UInt8] = [], last = ""
        var i = 4
        while i + 18 <= len {
            defer { i += 18 }
            guard b[i] == type, (b[i + 3] >> 4) & 7 == 0 else { continue }
            if track < 0 { track = Int(b[i + 1] & 0x7F) }
            for c in b[(i + 4)..<(i + 16)] {
                if c != 0 { bytes.append(c); continue }
                var s = String(bytes: bytes, encoding: .isoLatin1) ?? ""
                if s == "\t" { s = last }
                if track >= 0 {      // zero padding after the last name just adds empty ones past the end
                    out[track] = s.trimmingCharacters(in: .whitespaces); last = s; track += 1
                }
                bytes = []
            }
        }
        return out
    }
}
