import SwiftUI
import AppKit

/// Checks GitHub Releases (gwsparky/NRGtoMP3) for a newer ChurchSound. Release tags are the version: "v26.11.08", "v26.11.08-V3".
/// Uses the saved GitHub key when there is one, so it still works if the project is made private.
final class UpdateChecker: ObservableObject {
    static let repo = "gwsparky/NRGtoMP3"
    static var current: String { Bundle.main.infoDictionary?["ChurchSoundVersion"] as? String ?? "0" }

    @Published var newer: (version: String, page: URL)?
    @Published var checking = false
    @Published var message: String?

    /// "26.11.08 V3" → [26, 11, 8, 3]; a plain date is V1.
    static func parts(_ v: String) -> [Int] {
        var n = v.split(whereSeparator: { !$0.isNumber }).compactMap { Int($0) }
        while n.count < 4 { n.append(1) }
        return n
    }

    static func isNewer(_ a: String, than b: String) -> Bool { parts(b).lexicographicallyPrecedes(parts(a)) }

    /// At start-up, at most once a day.
    func checkAutomatically() {
        let last = UserDefaults.standard.double(forKey: "lastUpdateCheck")
        guard Date().timeIntervalSince1970 - last > 20 * 3600 else { return }
        check(quiet: true)
    }

    func check(quiet: Bool = false) {
        checking = true
        if !quiet { message = nil }
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(Self.repo)/releases/latest")!)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("ChurchSound", forHTTPHeaderField: "User-Agent")
        if let t = NamesSync.token, !t.isEmpty { req.setValue("Bearer \(t)", forHTTPHeaderField: "Authorization") }
        req.timeoutInterval = 15
        URLSession.shared.dataTask(with: req) { data, resp, error in
            let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
            var found: (String, URL)?
            if code == 200, let data, let j = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let tag = j["tag_name"] as? String, let page = (j["html_url"] as? String).flatMap(URL.init(string:)) {
                found = (tag.trimmingCharacters(in: CharacterSet(charactersIn: "vV")).replacingOccurrences(of: "-", with: " "), page)
            }
            DispatchQueue.main.async {
                self.checking = false
                if code == 200 { UserDefaults.standard.set(Date().timeIntervalSince1970, forKey: "lastUpdateCheck") }
                if let (v, page) = found, Self.isNewer(v, than: Self.current) {
                    self.newer = (v, page)
                    self.message = nil
                } else if quiet {
                    return
                } else if found != nil {
                    self.message = "You have the newest version (\(Self.current))."
                } else if code == 404 {
                    self.message = "Couldn't see the releases on GitHub. If the project is private, add your GitHub key with GitHub… on the Convert tab."
                } else {
                    self.message = "Couldn't check for updates: \(error?.localizedDescription ?? "GitHub answered \(code)")."
                }
            }
        }.resume()
    }
}

/// "Check for Updates" in the top bar, plus a highlighted button when a newer version is out.
struct UpdateButton: View {
    @ObservedObject var updates: UpdateChecker

    var body: some View {
        if let n = updates.newer {
            Button("Version \(n.version) is available: Download") { NSWorkspace.shared.open(n.page) }
                .controlSize(.small).tint(.green).buttonStyle(.borderedProminent)
                .help("Opens the GitHub page with the new installer (ChurchSound Installer.dmg)")
        } else {
            Button(updates.checking ? "Checking…" : "Check for Updates") { updates.check() }
                .controlSize(.small).disabled(updates.checking)
                .popover(isPresented: Binding(get: { updates.message != nil }, set: { if !$0 { updates.message = nil } })) {
                    Text(updates.message ?? "").padding(12).frame(maxWidth: 320)
                }
        }
    }
}
