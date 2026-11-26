import SwiftUI
import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        MainActor.assumeIsolated { Updater.shared.checkOnLaunch() }
    }

    // Load the previous session (offered on the start screen) before any launch files open.
    func applicationWillFinishLaunching(_ notification: Notification) {
        MainActor.assumeIsolated { AppState.shared.offerSession() }
    }

    func applicationWillTerminate(_ notification: Notification) {
        MainActor.assumeIsolated { AppState.shared.session.save(AppState.shared) }
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        Task { @MainActor in AppState.shared.add(urls) }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

@main
struct VNEGifMakerApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var state = AppState.shared

    init() {
        // Headless mode for scripting/tests: VNEGifMaker --cli <video> [start] [duration]
        let a = CommandLine.arguments
        if a.count >= 4, a[1] == "--cli" || a[1] == "--preview-frame" {
            CLI.run(Array(a.dropFirst(2)), previewFrame: a[1] == "--preview-frame")
        }
    }

    var body: some Scene {
        Window(Brand.name, id: "main") {
            ContentView()
                .environmentObject(state)
                .frame(minWidth: 920, minHeight: 580)
        }
        .defaultSize(width: 1180, height: 720)
        .commands {
            CommandGroup(replacing: .appInfo) {
                Button("Giới thiệu \(Brand.name)") { showAbout() }
                Button("Kiểm tra cập nhật…") { Task { await Updater.shared.check(userInitiated: true) } }
            }
            CommandGroup(replacing: .newItem) {
                Button("Thêm video…") { state.openPanel() }
                    .keyboardShortcut("o")
                Button("Lấy thêm đoạn từ video này") { state.addSegmentFromSelected() }
                    .keyboardShortcut("d")
                    .disabled(state.selected == nil)
                Divider()
                Button("Xuất GIF…") { state.export() }
                    .keyboardShortcut("e")
                    .disabled(state.clips.isEmpty || state.isEncoding)
            }
        }
    }
}

/// Standard About panel with the author credit.
@MainActor
func showAbout() {
    let credits = NSMutableAttributedString()
    let center = NSMutableParagraphStyle()
    center.alignment = .center
    credits.append(NSAttributedString(string: "Tác giả: \(Brand.author) – \(Brand.team)\n", attributes: [
        .font: NSFont.systemFont(ofSize: 12, weight: .semibold),
        .foregroundColor: NSColor.labelColor, .paragraphStyle: center,
    ]))
    credits.append(NSAttributedString(string: "Công cụ nội bộ tạo ảnh GIF thumbnail cho VnExpress.\nMã hoá bằng FFmpeg (GPL) và dav1d.", attributes: [
        .font: NSFont.systemFont(ofSize: 11),
        .foregroundColor: NSColor.secondaryLabelColor, .paragraphStyle: center,
    ]))
    NSApp.orderFrontStandardAboutPanel(options: [
        .applicationName: Brand.name,
        .credits: credits,
    ])
    NSApp.activate(ignoringOtherApps: true)
}
