import Foundation

/// Remembers the open clips (file, in/out, edits) between launches.
@MainActor
final class SessionStore {
    struct SavedClip: Codable, Equatable {
        var path: String
        var inPoint: Double
        var outPoint: Double
        var edit: ClipEdit
    }
    struct Saved: Codable, Equatable {
        var clips: [SavedClip]
        var selected: Int?
    }

    /// While restoring, partial state must not overwrite the saved session.
    var suspended = false
    private var pending: Task<Void, Never>?
    private var last: Saved?

    private let url: URL = {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("VNEGifMaker", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("session.json")
    }()

    func load() -> Saved? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        let s = try? JSONDecoder().decode(Saved.self, from: data)
        last = s
        return s
    }

    /// Debounced: edits arrive many times per second while dragging.
    func scheduleSave(_ state: AppState) {
        guard !suspended else { return }
        pending?.cancel()
        pending = Task { [weak state] in
            try? await Task.sleep(nanoseconds: 400_000_000)
            guard !Task.isCancelled, let state, !self.suspended else { return }
            self.save(state)
        }
    }

    func save(_ state: AppState) {
        let clips = state.clips.map { SavedClip(path: $0.url.path, inPoint: $0.inPoint, outPoint: $0.outPoint, edit: $0.edit) }
        let s = Saved(clips: clips, selected: state.selectedIndex)
        guard s != last else { return }
        last = s
        if let data = try? JSONEncoder().encode(s) { try? data.write(to: url, options: .atomic) }
    }
}
