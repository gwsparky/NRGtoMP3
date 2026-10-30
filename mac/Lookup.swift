import SwiftUI
import CryptoKit

// MARK: - MusicBrainz

struct TOC: Decodable {
    let exact: Bool
    let first: Int
    let last: Int
    let leadout: Int
    let offsets: [Int]
}

struct MBRelease: Identifiable {
    let id: String
    let title: String
    let artist: String
    let date: String
    let country: String
    let trackCount: Int
}

struct MBTracks {
    let album: String
    let artist: String
    let titles: [String]
}

/// Talks to musicbrainz.org, the free music database most CD rippers use. Only the disc layout
/// or the words being searched for are sent.
enum MusicBrainz {
    static let base = "https://musicbrainz.org/ws/2/"
    static let userAgent = "ChurchSound/1.0 (personal CD archiving tool)"
    private static var lastRequest = Date.distantPast

    /// MusicBrainz disc ID: SHA-1 of the table of contents, in their URL-safe Base64.
    static func discID(_ toc: TOC) -> String {
        var s = String(format: "%02X%02X%08X", toc.first, toc.last, toc.leadout)
        for i in 0..<99 { s += String(format: "%08X", i < toc.offsets.count ? toc.offsets[i] : 0) }
        let digest = Insecure.SHA1.hash(data: Data(s.utf8))
        return Data(digest).base64EncodedString()
            .replacingOccurrences(of: "+", with: ".")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "-")
    }

    static func get(_ path: String, _ query: [URLQueryItem]) async throws -> [String: Any] {
        // MusicBrainz asks for at most one request per second
        let wait = 1.1 - Date().timeIntervalSince(lastRequest)
        if wait > 0 { try await Task.sleep(nanoseconds: UInt64(wait * 1_000_000_000)) }
        lastRequest = Date()
        var c = URLComponents(string: base + path)!
        c.queryItems = query + [URLQueryItem(name: "fmt", value: "json")]
        var req = URLRequest(url: c.url!)
        req.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        req.timeoutInterval = 20
        let (data, resp) = try await URLSession.shared.data(for: req)
        let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
        if code == 404 { return [:] }
        guard code == 200 else { throw NSError(domain: "MusicBrainz", code: code, userInfo: [NSLocalizedDescriptionKey: "MusicBrainz answered with error \(code). Try again in a moment."]) }
        return (try JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
    }

    static func artistName(_ r: [String: Any]) -> String {
        ((r["artist-credit"] as? [[String: Any]]) ?? []).map { ($0["name"] as? String ?? "") + ($0["joinphrase"] as? String ?? "") }.joined()
    }

    static func release(_ r: [String: Any]) -> MBRelease? {
        guard let id = r["id"] as? String else { return nil }
        let media = r["media"] as? [[String: Any]] ?? []
        let count = r["track-count"] as? Int ?? media.reduce(0) { $0 + ($1["track-count"] as? Int ?? 0) }
        return MBRelease(id: id, title: r["title"] as? String ?? "", artist: artistName(r),
                         date: r["date"] as? String ?? "", country: r["country"] as? String ?? "", trackCount: count)
    }

    /// Exact match by disc ID; if there's none, MusicBrainz also tries a near match on the track layout.
    /// Real disc layouts: exact disc-ID match only (MusicBrainz's "similar layout" guesses are usually
    /// unrelated albums with the same number of songs). Layouts guessed from silences: near matches.
    static func lookup(_ toc: TOC) async throws -> [MBRelease] {
        var query = [URLQueryItem(name: "cdstubs", value: "no"), URLQueryItem(name: "inc", value: "artist-credits")]
        if !toc.exact {
            let tocString = ([toc.first, toc.last, toc.leadout] + toc.offsets).map(String.init).joined(separator: " ")
            query.append(URLQueryItem(name: "toc", value: tocString))
        }
        let d = try await get("discid/\(toc.exact ? discID(toc) : "-")", query)
        return ((d["releases"] as? [[String: Any]]) ?? []).compactMap(release)
    }

    static func search(album: String, artist: String) async throws -> [MBRelease] {
        func esc(_ s: String) -> String { s.replacingOccurrences(of: "\"", with: " ") }
        var parts: [String] = []
        if !album.isEmpty { parts.append("release:\"\(esc(album))\"") }
        if !artist.isEmpty { parts.append("artist:\"\(esc(artist))\"") }
        guard !parts.isEmpty else { return [] }
        let d = try await get("release", [URLQueryItem(name: "query", value: parts.joined(separator: " AND ")),
                                          URLQueryItem(name: "limit", value: "25")])
        return ((d["releases"] as? [[String: Any]]) ?? []).compactMap(release)
    }

    /// Song names of a release. Multi-disc sets: uses the disc whose song count matches, else the first.
    static func tracks(_ releaseID: String, wanted: Int) async throws -> MBTracks {
        let d = try await get("release/\(releaseID)", [URLQueryItem(name: "inc", value: "recordings artist-credits")])
        let media = (d["media"] as? [[String: Any]]) ?? []
        let medium = media.first { ($0["tracks"] as? [Any])?.count == wanted } ?? media.first ?? [:]
        let titles = ((medium["tracks"] as? [[String: Any]]) ?? []).map { $0["title"] as? String ?? "" }
        return MBTracks(album: d["title"] as? String ?? "", artist: artistName(d), titles: titles)
    }
}

// MARK: - Lookup window

struct LookupSheet: View {
    let heading: String                  // what's being named, shown under the title
    let count: Int                       // number of songs
    let toc: TOC?                        // track layout, for Match Disc
    let startAlbum: String
    let startArtist: String
    let onApply: (MBTracks) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var album = ""
    @State private var artist = ""
    @State private var results: [MBRelease] = []
    @State private var chosen: MBRelease.ID?
    @State private var preview: MBTracks?
    @State private var busy = false
    @State private var message = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Look Up Song Names").font(.title2).bold()
            Text(heading + " · \(count) songs").foregroundStyle(.secondary)

            HStack {
                TextField("Album", text: $album).onSubmit(search)
                TextField("Artist (optional)", text: $artist).onSubmit(search)
                Button("Search", action: search).disabled(busy || (album.isEmpty && artist.isEmpty))
                if toc != nil {
                    Button("Match Disc") { matchDisc() }.disabled(busy)
                        .help("Find this exact CD on MusicBrainz by its track layout")
                }
            }

            HStack(alignment: .top, spacing: 12) {
                List(results, selection: $chosen) { r in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(r.title).bold()
                        Text([r.artist, r.date, r.country, "\(r.trackCount) songs"].filter { !$0.isEmpty }.joined(separator: " · "))
                            .font(.caption)
                            .foregroundStyle(r.trackCount == count ? Color.green : Color.secondary)
                    }
                    .tag(r.id)
                }
                .frame(minWidth: 300)

                VStack(alignment: .leading, spacing: 4) {
                    if let p = preview {
                        Text(p.album).bold()
                        Text(p.artist).font(.caption).foregroundStyle(.secondary)
                        if p.titles.count != count {
                            Text("This release has \(p.titles.count) songs; your disc has \(count). Names will be applied in order.")
                                .font(.caption).foregroundStyle(.orange)
                        }
                        List(Array(p.titles.enumerated()), id: \.offset) { i, t in
                            Text(String(format: "%02d  ", i + 1) + t).font(.callout)
                        }
                    } else {
                        Text("Choose a result to see its songs.").foregroundStyle(.secondary)
                        Spacer()
                    }
                }
                .frame(minWidth: 280)
            }
            .frame(minHeight: 320)

            HStack {
                if busy { ProgressView().controlSize(.small) }
                Text(message).font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Use These Names") { apply() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(preview == nil)
            }
            Text("Song names come from MusicBrainz.org, a free community database.").font(.caption2).foregroundStyle(.secondary)
        }
        .padding(20)
        .frame(width: 720)
        .onAppear {
            album = startAlbum
            artist = startArtist
            if let toc = toc, toc.exact { matchDisc() }
        }
        .onChange(of: chosen) { id in loadPreview(id) }
    }

    func run(_ work: @escaping () async throws -> Void) {
        busy = true
        Task {
            do { try await work() } catch { message = error.localizedDescription }
            busy = false
        }
    }

    func matchDisc() {
        guard let toc = toc else { return }
        message = "Looking for this disc…"
        run {
            let found = try await MusicBrainz.lookup(toc)
            results = found; preview = nil; chosen = nil
            message = found.isEmpty
                ? (toc.exact ? "This disc isn't in MusicBrainz. Try searching by album or artist." :
                               "No match. This file's song breaks were guessed from silences, so search by album or artist instead.")
                : "Found \(found.count) matching release(s)."
            if found.count == 1 { chosen = found[0].id }
        }
    }

    func search() {
        guard !album.isEmpty || !artist.isEmpty else { return }
        message = "Searching…"
        run {
            let found = try await MusicBrainz.search(album: album, artist: artist)
            // releases with the same number of songs as this disc first
            results = found.sorted { ($0.trackCount == count ? 0 : 1) < ($1.trackCount == count ? 0 : 1) }
            preview = nil; chosen = nil
            message = found.isEmpty ? "Nothing found. Try fewer or different words." : "\(found.count) result(s). Green ones have \(count) songs, like your disc."
        }
    }

    func loadPreview(_ id: String?) {
        guard let id else { preview = nil; return }
        run {
            let t = try await MusicBrainz.tracks(id, wanted: count)
            if chosen == id { preview = t }
        }
    }

    func apply() {
        guard let p = preview else { return }
        onApply(p)
        dismiss()
    }
}

extension LookupSheet {
    /// Names the songs of an opened disc image or CD.
    init(disc: Disc) {
        self.init(heading: disc.url.lastPathComponent, count: disc.tracks.count, toc: disc.toc,
                  startAlbum: disc.album, startArtist: disc.artist) { p in
            disc.album = p.album
            disc.artist = p.artist
            for (i, t) in disc.tracks.enumerated() where i < p.titles.count && !p.titles[i].isEmpty {
                disc.names[t.number] = p.titles[i]
            }
        }
    }
}
