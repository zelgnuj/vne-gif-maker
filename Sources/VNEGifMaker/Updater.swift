import AppKit
import Foundation

/// Checks GitHub Releases for a newer version and installs it in place.
/// Releases are published with ./release.sh (asset: VnExpress-GIF-Maker.zip).
@MainActor
final class Updater {
    static let shared = Updater()

    static let repo = "zelgnuj/vne-gif-maker"
    static let assetName = "VnExpress-GIF-Maker.zip"
    static var releasesPage: URL { URL(string: "https://github.com/\(repo)/releases/latest")! }

    private let d = UserDefaults.standard
    private var busy = false

    var currentVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    }

    struct Release {
        let version: String
        let notes: String
        let zip: URL
    }

    /// At launch: at most once a day, silent unless there is an update.
    func checkOnLaunch() {
        if let last = d.object(forKey: "lastUpdateCheck") as? Date, Date().timeIntervalSince(last) < 20 * 3600 { return }
        Task { await check(userInitiated: false) }
    }

    func check(userInitiated: Bool) async {
        guard !busy else { return }
        busy = true
        defer { busy = false }
        do {
            let rel = try await latest()
            d.set(Date(), forKey: "lastUpdateCheck")
            guard let rel, Self.isNewer(rel.version, than: currentVersion) else {
                if userInitiated { info("Bạn đang dùng bản mới nhất", "Phiên bản \(currentVersion).") }
                return
            }
            if !userInitiated, d.string(forKey: "skippedVersion") == rel.version { return }
            offer(rel)
        } catch {
            if userInitiated {
                info("Không kiểm tra được bản cập nhật", "Kiểm tra kết nối mạng rồi thử lại.\n\(error.localizedDescription)")
            }
        }
    }

    private func latest() async throws -> Release? {
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(Self.repo)/releases/latest")!)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("VNEGifMaker-Updater", forHTTPHeaderField: "User-Agent")
        req.timeoutInterval = 15
        let (data, resp) = try await URLSession.shared.data(for: req)
        if (resp as? HTTPURLResponse)?.statusCode == 404 { return nil }
        guard let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = obj["tag_name"] as? String,
              let assets = obj["assets"] as? [[String: Any]],
              let asset = assets.first(where: { $0["name"] as? String == Self.assetName }),
              let urlStr = asset["browser_download_url"] as? String, let url = URL(string: urlStr)
        else { return nil }
        let version = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
        return Release(version: version, notes: obj["body"] as? String ?? "", zip: url)
    }

    /// "1.10" > "1.9"; missing parts count as 0.
    static func isNewer(_ a: String, than b: String) -> Bool {
        let pa = a.split(separator: ".").map { Int($0) ?? 0 }
        let pb = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0, y = i < pb.count ? pb[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    private func offer(_ rel: Release) {
        let a = NSAlert()
        a.messageText = "Đã có phiên bản \(rel.version)"
        var text = "Bạn đang dùng bản \(currentVersion)."
        let notes = rel.notes.trimmingCharacters(in: .whitespacesAndNewlines)
        if !notes.isEmpty { text += "\n\n" + String(notes.prefix(800)) }
        a.informativeText = text
        a.addButton(withTitle: "Cập nhật ngay")
        a.addButton(withTitle: "Để sau")
        a.addButton(withTitle: "Bỏ qua bản này")
        switch a.runModal() {
        case .alertFirstButtonReturn: Task { await install(rel) }
        case .alertThirdButtonReturn: d.set(rel.version, forKey: "skippedVersion")
        default: break
        }
    }

    /// Downloads the zip, swaps the app bundle in place and relaunches.
    private func install(_ rel: Release) async {
        if AppState.shared.isEncoding {
            info("Đang xuất GIF", "Hãy đợi xuất xong rồi cập nhật lại (menu \(Brand.name) › Kiểm tra cập nhật…).")
            return
        }
        AppState.shared.setPreparing("Đang tải bản \(rel.version)…")
        defer { AppState.shared.setPreparing(nil) }
        let fm = FileManager.default
        let work = fm.temporaryDirectory.appendingPathComponent("vnegif-update-\(UUID().uuidString)")
        do {
            try fm.createDirectory(at: work, withIntermediateDirectories: true)
            let (tmpZip, resp) = try await URLSession.shared.download(from: rel.zip)
            guard (resp as? HTTPURLResponse)?.statusCode == 200 else { throw UpdateError.download }
            let zip = work.appendingPathComponent("update.zip")
            try fm.moveItem(at: tmpZip, to: zip)
            try await GifEncoder.run("/usr/bin/ditto", ["-x", "-k", zip.path, work.appendingPathComponent("x").path])

            guard let newApp = fm.enumerator(at: work.appendingPathComponent("x"), includingPropertiesForKeys: nil)?
                .compactMap({ $0 as? URL }).first(where: { $0.pathExtension == "app" }),
                  let newBundle = Bundle(url: newApp),
                  newBundle.bundleIdentifier == Bundle.main.bundleIdentifier
            else { throw UpdateError.badPackage }
            try? await GifEncoder.run("/usr/bin/xattr", ["-cr", newApp.path])

            let current = Bundle.main.bundleURL
            let parent = current.deletingLastPathComponent()
            guard fm.isWritableFile(atPath: parent.path) else { throw UpdateError.notWritable(parent.path) }
            // Swap: old bundle aside, new one in, old one to the Trash.
            let old = work.appendingPathComponent("\(Brand.name) (bản cũ).app")
            try fm.moveItem(at: current, to: old)
            do {
                try fm.moveItem(at: newApp, to: current)
            } catch {
                try? fm.moveItem(at: old, to: current)
                throw error
            }
            try? fm.trashItem(at: old, resultingItemURL: nil)
            AppState.shared.session.save(AppState.shared)
            relaunch(current)
        } catch {
            let a = NSAlert()
            a.messageText = "Cập nhật không thành công"
            a.informativeText = error.localizedDescription + "\n\nBạn có thể tải bản mới thủ công từ trang phát hành."
            a.addButton(withTitle: "Mở trang tải")
            a.addButton(withTitle: "Đóng")
            if a.runModal() == .alertFirstButtonReturn { NSWorkspace.shared.open(Self.releasesPage) }
        }
    }

    private func relaunch(_ app: URL) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "while kill -0 \(ProcessInfo.processInfo.processIdentifier) 2>/dev/null; do sleep 0.2; done; open \"$0\"", app.path]
        try? p.run()
        NSApp.terminate(nil)
    }

    private func info(_ title: String, _ text: String) {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = text
        a.runModal()
    }

    enum UpdateError: LocalizedError {
        case download, badPackage, notWritable(String)
        var errorDescription: String? {
            switch self {
            case .download: "Không tải được tệp cập nhật."
            case .badPackage: "Tệp cập nhật không hợp lệ."
            case .notWritable(let p): "Không có quyền ghi vào thư mục \(p)."
            }
        }
    }
}
