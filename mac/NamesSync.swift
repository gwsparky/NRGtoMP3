import SwiftUI
import Security

/// Keeps names files in a GitHub repository's "names" folder ("names/Church Music.txt"), so they can be edited on
/// github.com and picked up by both apps (the Windows app does the same). Downloading needs no key for a public
/// repository; uploading needs a GitHub access key ("personal access token") allowed to change its contents.
enum NamesSync {
    static let defaultRepo = "gwsparky/NRGtoMP3"
    static let folder = "names"

    static var on: Bool { UserDefaults.standard.object(forKey: "githubSync") as? Bool ?? true }
    static var repo: String { UserDefaults.standard.string(forKey: "githubRepo") ?? defaultRepo }

    /// The access key lives in the Mac's Keychain, not in the settings.
    static var token: String? {
        get {
            var item: CFTypeRef?
            let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "NRG to MP3 GitHub",
                                    kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
            guard SecItemCopyMatching(q as CFDictionary, &item) == errSecSuccess, let data = item as? Data else { return nil }
            return String(data: data, encoding: .utf8)
        }
        set {
            let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "NRG to MP3 GitHub"]
            SecItemDelete(q as CFDictionary)
            let v = newValue?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            guard !v.isEmpty else { return }
            var add = q
            add[kSecValueData as String] = Data(v.utf8)
            SecItemAdd(add as CFDictionary, nil)
        }
    }

    struct Remote { let text: String; let sha: String }

    struct Problem: LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    static func remotePath(_ image: URL) -> String { "\(folder)/\(image.deletingPathExtension().lastPathComponent).txt" }

    static func request(_ image: URL, method: String = "GET") -> URLRequest {
        let path = remotePath(image).split(separator: "/")
            .map { String($0).addingPercentEncoding(withAllowedCharacters: .urlPathAllowed.subtracting(CharacterSet(charactersIn: "/?#"))) ?? String($0) }
            .joined(separator: "/")
        let r = repo.trimmingCharacters(in: CharacterSet(charactersIn: "/ "))
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(r)/contents/\(path)")!)
        req.httpMethod = method
        req.timeoutInterval = 20
        req.setValue("ChurchSound/1.0", forHTTPHeaderField: "User-Agent")
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        if let t = token, !t.isEmpty { req.setValue("Bearer \(t)", forHTTPHeaderField: "Authorization") }
        return req
    }

    static func problem(_ code: Int, _ body: Data) -> Problem {
        switch code {
        case 401: return Problem(message: "GitHub didn't accept the access key. Check it with GitHub…")
        case 403 where String(decoding: body, as: UTF8.self).contains("rate limit"):
            return Problem(message: "GitHub is limiting requests for now. Try again in an hour, or add an access key.")
        case 403, 404: return Problem(message: "The access key isn't allowed to change this repository (it needs \"Contents: Read and write\").")
        case 409, 422: return Problem(message: "The file changed on GitHub at the same moment. Try again.")
        default: return Problem(message: "GitHub answered with error \(code).")
        }
    }

    /// The names file for this image on GitHub, or nil if there isn't one.
    static func download(_ image: URL) async throws -> Remote? {
        let (data, resp) = try await URLSession.shared.data(for: request(image))
        let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
        if code == 404 { return nil }
        guard code == 200 else { throw problem(code, data) }
        guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let b64 = json["content"] as? String, let sha = json["sha"] as? String,
              let bytes = Data(base64Encoded: b64.replacingOccurrences(of: "\n", with: "").replacingOccurrences(of: "\r", with: ""))
        else { throw Problem(message: "GitHub sent something unexpected.") }
        let text = String(data: bytes, encoding: .utf8) ?? String(data: bytes, encoding: .isoLatin1) ?? ""
        return Remote(text: text.replacingOccurrences(of: "\u{FEFF}", with: ""), sha: sha)
    }

    /// Puts the names file on GitHub, replacing the one there. Returns false if GitHub already had exactly this.
    @discardableResult
    static func upload(_ image: URL, text: String) async throws -> Bool {
        guard let t = token, !t.isEmpty else { throw Problem(message: "Uploading needs a GitHub access key. Add one with GitHub…") }
        let existing = try await download(image)
        if let existing, same(existing.text, text) { return false }
        var body: [String: Any] = ["message": "Song names for \(image.lastPathComponent)",
                                   "content": Data(text.replacingOccurrences(of: "\r\n", with: "\n").utf8).base64EncodedString()]
        if let existing { body["sha"] = existing.sha }
        var req = request(image, method: "PUT")
        req.httpBody = try JSONSerialization.data(withJSONObject: body)
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let (data, resp) = try await URLSession.shared.data(for: req)
        let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
        guard code == 200 || code == 201 else { throw problem(code, data) }
        return true
    }

    /// The same names, ignoring line endings, the BOM and trailing spaces (Windows and github.com differ in these).
    static func same(_ a: String, _ b: String) -> Bool {
        func norm(_ s: String) -> String {
            s.replacingOccurrences(of: "\u{FEFF}", with: "").replacingOccurrences(of: "\r", with: "")
                .components(separatedBy: "\n")
                .map { $0.replacingOccurrences(of: #"\s+$"#, with: "", options: .regularExpression) }
                .joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
        }
        return norm(a) == norm(b)
    }

    static func describe(_ error: Error) -> String {
        (error as? URLError) != nil ? "couldn't reach GitHub (no internet?)" : error.localizedDescription
    }

    /// Loads the disc's names from GitHub if they differ from the names file on this Mac (which is then updated too).
    /// Automatic = just opened: stay quiet unless something changed.
    @MainActor
    static func pull(_ d: Disc, automatic: Bool) async {
        let place = remotePath(d.url)
        do {
            let remote = try await download(d.url)
            let local = (try? String(contentsOf: d.namesFile, encoding: .utf8)) ?? (try? String(contentsOf: d.namesFile, encoding: .isoLatin1))
            if let remote {
                if let local, same(remote.text, local) {
                    if !automatic { d.namesNote = "The names on this Mac already match GitHub" }
                } else {
                    d.namesNote = "\(d.applyNames(remote.text)) name(s) loaded from GitHub (\(place))"
                    if (try? remote.text.write(to: d.namesFile, atomically: true, encoding: .utf8)) == nil {
                        d.namesNote! += "; couldn't update the names file next to the image"
                    }
                }
            } else if !automatic {
                d.namesNote = "There's no \(place) on GitHub yet. Save Names will put it there."
            }
        } catch {
            d.namesNote = automatic ? (d.namesNote.map { $0 + " · " } ?? "") + "GitHub: " + describe(error)
                                    : "Couldn't get names from GitHub: " + describe(error)
        }
    }

    /// After Save Names: puts the same names on GitHub (if syncing is on).
    @MainActor
    static func push(_ d: Disc, text: String) async {
        guard on else { return }
        let saved = d.namesNote ?? "Names saved"
        guard let t = token, !t.isEmpty else {
            d.namesNote = saved + ". To upload them to GitHub too, add an access key with GitHub…"; return
        }
        d.namesNote = saved + " · uploading to GitHub…"
        do {
            let changed = try await upload(d.url, text: text)
            d.namesNote = saved + (changed ? " and uploaded to GitHub (\(remotePath(d.url)))" : " · GitHub already has these names")
        } catch {
            d.namesNote = saved + " · not uploaded: " + describe(error)
        }
    }
}

/// "GitHub…": turn names syncing on or off, choose the repository, and paste the access key.
struct NamesSyncSheet: View {
    @Environment(\.dismiss) private var dismiss
    @State private var on = NamesSync.on
    @State private var repo = NamesSync.repo
    @State private var token = NamesSync.token ?? ""

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Song Names on GitHub").font(.headline)
            Toggle("Keep song names in sync with GitHub", isOn: $on)
            Text("Names are kept in the repository's \"names\" folder, one file per disc (\"names/Church Music.txt\"). When you open a disc, names edited on github.com are loaded. Save Names also uploads them.")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Text("Downloading works without a key. To upload, make a key on github.com: Settings → Developer settings → Personal access tokens → Fine-grained tokens → Generate new token; choose only this repository and Repository permissions → Contents: Read and write. Paste it below. It's kept in your Mac's Keychain.")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Grid(alignment: .leading) {
                GridRow { Text("Repository"); TextField(NamesSync.defaultRepo, text: $repo).textFieldStyle(.roundedBorder) }
                GridRow { Text("Access key"); SecureField("github_pat_…", text: $token).textFieldStyle(.roundedBorder) }
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("OK") {
                    UserDefaults.standard.set(on, forKey: "githubSync")
                    let r = repo.trimmingCharacters(in: .whitespaces)
                    if r.isEmpty { UserDefaults.standard.removeObject(forKey: "githubRepo") } else { UserDefaults.standard.set(r, forKey: "githubRepo") }
                    NamesSync.token = token
                    dismiss()
                }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(20).frame(width: 480)
    }
}
