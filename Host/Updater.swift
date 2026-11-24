import AppKit
import Foundation

/// Checks GitHub for a newer release at launch and offers to download it.
/// The Mac host is not code-signed, so instead of replacing itself it downloads the new disk image and opens it:
/// drag Lumen Host onto Applications to finish.
@MainActor
enum Updater {
    private static let repo = "cheapearth9/Lumen"
    private static var checked = false

    static var current: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    }

    private static func parts(_ v: String) -> [Int] {
        v.trimmingCharacters(in: CharacterSet(charactersIn: "vV")).split(separator: ".").map { Int($0) ?? 0 }
    }

    private static func isNewer(_ a: String, than b: String) -> Bool {
        let x = parts(a), y = parts(b)
        for i in 0..<max(x.count, y.count) {
            let p = i < x.count ? x[i] : 0, q = i < y.count ? y[i] : 0
            if p != q { return p > q }
        }
        return false
    }

    static func checkAtLaunch() {
        guard !checked else { return }
        checked = true
        Task {
            guard let url = URL(string: "https://api.github.com/repos/\(repo)/releases/latest") else { return }
            var req = URLRequest(url: url, timeoutInterval: 15)
            req.setValue("LumenHost-Updater", forHTTPHeaderField: "User-Agent")
            guard let (data, _) = try? await URLSession.shared.data(for: req),
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let tag = json["tag_name"] as? String, isNewer(tag, than: current) else { return }
            let d = UserDefaults.standard
            if d.string(forKey: "updateSkipped") == tag { return }
            if d.double(forKey: "updateRemindAfter") > Date().timeIntervalSince1970 { return }
            let assets = json["assets"] as? [[String: Any]] ?? []
            guard let dmg = assets.first(where: { ($0["name"] as? String) == "Lumen-Host-macOS.dmg" }),
                  let dl = (dmg["browser_download_url"] as? String).flatMap(URL.init(string:)) else { return }

            let alert = NSAlert()
            alert.messageText = "Would you like to update to the latest version?"
            alert.informativeText = "Lumen Host \(tag.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))) is available (you have \(current))."
            alert.addButton(withTitle: "Update")
            alert.addButton(withTitle: "Remind Me Later")
            alert.addButton(withTitle: "Skip This Version")
            switch alert.runModal() {
            case .alertFirstButtonReturn: await download(dl)
            case .alertThirdButtonReturn: d.set(tag, forKey: "updateSkipped")
            default: d.set(Date().addingTimeInterval(86400).timeIntervalSince1970, forKey: "updateRemindAfter")
            }
        }
    }

    private static func download(_ url: URL) async {
        do {
            let (tmp, _) = try await URLSession.shared.download(from: url)
            let dest = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0].appendingPathComponent("Lumen-Host-macOS.dmg")
            try? FileManager.default.removeItem(at: dest)
            try FileManager.default.moveItem(at: tmp, to: dest)
            NSWorkspace.shared.open(dest)
            let done = NSAlert()
            done.messageText = "Update downloaded"
            done.informativeText = "Drag Lumen Host onto Applications (replace the old one), then quit and reopen it."
            done.runModal()
        } catch {
            let f = NSAlert()
            f.messageText = "Update failed"
            f.informativeText = error.localizedDescription
            f.runModal()
        }
    }
}
