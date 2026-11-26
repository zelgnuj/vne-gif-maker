import AVFoundation
import AppKit
import SwiftUI
import UniformTypeIdentifiers

enum ExportStatus: Equatable {
    case idle
    case encoding(String)
    case done(GifResult)
    case failed(String)
}

struct Clip: Identifiable {
    let id = UUID()
    let url: URL              // original (used for export)
    var asset: AVURLAsset     // for preview/thumbnails (a proxy if the original isn't decodable)
    var previewTransform: CGAffineTransform = .identity  // rotation of `asset`'s video track
    var isProxy = false
    var frameDur = 1.0 / 30                  // from the track's nominal frame rate
    var cuts: [Double] = []                  // scene cut times (source seconds, frame-aligned)
    var sceneProgress: Double?               // 0…1 while analyzing
    var scenesAnalyzed = false
    let duration: Double
    let size: CGSize
    var thumbnails: [CGImage] = []
    /// Frame from the middle of the selected range, for the sequence card.
    var cardThumb: CGImage?
    var cardThumbTime: Double?
    var inPoint: Double
    var outPoint: Double
    var edit = ClipEdit()
    var length: Double { outPoint - inPoint }           // source seconds
    var outLength: Double { length / edit.speed }       // seconds in the GIF
    var name: String { url.deletingPathExtension().lastPathComponent }
    var isVertical: Bool { size.height > size.width }
}

@MainActor
final class AppState: ObservableObject {
    static let shared = AppState()

    static let widthOptions = [480, 600, 680, 800, 1000, 1200]
    static let fpsOptions = [10, 12, 15, 20, 25]
    static let minLength = 0.3

    /// Two players: while one plays, "Phát toàn bộ" preloads the next clip on the other
    /// (already seeked to its in point), so switching clips doesn't stall.
    private let players = [AVPlayer(), AVPlayer()]
    private var active = 0
    var player: AVPlayer { players[active] }
    private struct Preload {
        let id: UUID
        let item: AVPlayerItem
        let output: AVPlayerItemVideoOutput
        let time: Double          // the clip's first frame (mid-frame time of its in point)
        var started = false
    }
    private var preloaded: Preload?
    /// AVPlayer needs ~150–200 ms to get going; the next clip starts this early, hidden,
    /// and frames before its in point are never shown.
    private static let leadIn = 0.2
    /// How long the hidden player takes to get going. Not a fixed guess: it is corrected at
    /// every switch from how early/late the next clip actually arrived (depends on codec,
    /// resolution, disk, machine).
    private var startLatency = 0.06
    /// While a seek is in flight the player still reports the old time; ignore it.
    private var pendingSeeks = 0

    @Published private(set) var clips: [Clip] = [] {
        didSet { session.scheduleSave(self); scheduleCardThumbs() }
    }
    private var cardThumbTask: Task<Void, Never>?
    private var writingCardThumb = false
    @Published var selectedID: UUID? { didSet { session.scheduleSave(self) } }
    let session = SessionStore()
    private var loadChain: Task<Void, Never>?
    let clock = PlaybackClock()
    var currentTime: Double { clock.time }
    let preview = PreviewEngine()
    let timeline = TimelineZoom()
    @Published private(set) var previewIssue: PreviewEngine.Issue = .none
    @Published private(set) var isPlaying = false
    @Published private(set) var playAll = false
    @Published var status: ExportStatus = .idle
    @Published var loadError: String?
    @Published private(set) var preparing: String?
    func setPreparing(_ msg: String?) { preparing = msg }

    /// Clip being dragged in the strip (for reordering).
    var draggingClip: UUID?
    /// While a trim handle is dragged, playback is suspended.
    private var scrubbing = false
    private var wasPlayingBeforeScrub = false

    private let d = UserDefaults.standard
    @Published var aspect: Aspect { didSet { d.set(aspect.rawValue, forKey: "aspect"); syncPreview() } }
    @Published var customW: Double { didSet { d.set(customW, forKey: "customW"); syncPreview() } }
    @Published var customH: Double { didSet { d.set(customH, forKey: "customH"); syncPreview() } }
    @Published var width: Int { didSet { d.set(width, forKey: "width"); syncPreview() } }
    @Published var fps: Int { didSet { d.set(fps, forKey: "fps") } }
    @Published var dither: Dither { didSet { d.set(dither.rawValue, forKey: "dither") } }
    @Published var limitMB: Double { didSet { d.set(limitMB, forKey: "limitMB") } }
    @Published var autoFit: Bool { didSet { d.set(autoFit, forKey: "autoFit") } }
    @Published var snapToScenes: Bool { didSet { d.set(snapToScenes, forKey: "snapToScenes") } }
    @Published var fitPriority: FitPriority { didSet { d.set(fitPriority.rawValue, forKey: "fitPriority") } }
    @Published var minFps: Int { didSet { d.set(minFps, forKey: "minFps") } }
    @Published var minWidth: Int { didSet { d.set(minWidth, forKey: "minWidth") } }
    @Published var forceLimit: Bool { didSet { d.set(forceLimit, forKey: "forceLimit") } }

    private var exportTask: Task<Void, Never>?
    private var keyMonitor: Any?
    private var scrollMonitor: Any?
    private var sceneQueue: [UUID] = []
    private var sceneTask: Task<Void, Never>?
    private var sceneClip: UUID?

    private init() {
        aspect = Aspect(rawValue: d.string(forKey: "aspect") ?? "") ?? .r5x3
        customW = d.object(forKey: "customW") as? Double ?? 5
        customH = d.object(forKey: "customH") as? Double ?? 3
        width = d.object(forKey: "width") as? Int ?? 680
        // Default changed 15 → 12 fps: drop a stored 15 left over from the old default (once).
        if !d.bool(forKey: "fpsDefault12") {
            if d.object(forKey: "fps") as? Int == 15 { d.removeObject(forKey: "fps") }
            d.set(true, forKey: "fpsDefault12")
        }
        fps = d.object(forKey: "fps") as? Int ?? 12
        dither = Dither(rawValue: d.string(forKey: "dither") ?? "") ?? .balanced
        limitMB = d.object(forKey: "limitMB") as? Double ?? 8
        autoFit = d.object(forKey: "autoFit") as? Bool ?? true
        snapToScenes = d.object(forKey: "snapToScenes") as? Bool ?? true
        fitPriority = FitPriority(rawValue: d.string(forKey: "fitPriority") ?? "") ?? .keepSize
        minFps = d.object(forKey: "minFps") as? Int ?? 10
        minWidth = d.object(forKey: "minWidth") as? Int ?? 600
        forceLimit = d.object(forKey: "forceLimit") as? Bool ?? true

        preview.onIssue = { [weak self] in self?.previewIssue = $0 }
        for p in players {
            p.isMuted = true
            p.actionAtItemEnd = .pause
            // Local files: start on play() instead of buffering ahead first.
            p.automaticallyWaitsToMinimizeStalling = false
            p.addPeriodicTimeObserver(forInterval: CMTime(value: 1, timescale: 30), queue: .main) { [weak self, weak p] t in
                MainActor.assumeIsolated {
                    guard let self, p === self.player else { return }
                    self.tick(t.seconds)
                }
            }
        }
        NotificationCenter.default.addObserver(forName: .AVPlayerItemDidPlayToEndTime, object: nil, queue: .main) { [weak self] n in
            let item = n.object as AnyObject?
            MainActor.assumeIsolated {
                guard let self, item === self.player.currentItem else { return }
                self.reachedEnd()
            }
        }
    }

    // MARK: Derived

    var selectedIndex: Int? { clips.firstIndex { $0.id == selectedID } }
    var selected: Clip? { selectedIndex.map { clips[$0] } }
    var totalDuration: Double { clips.reduce(0) { $0 + $1.outLength } }
    var isEncoding: Bool { if case .encoding = status { true } else { false } }

    /// Output aspect (w/h). "Gốc" = aspect of the first clip.
    var outputAspect: Double {
        if let r = aspect.ratio { return r }
        if aspect == .custom { return min(max(customW / max(customH, 0.01), 0.2), 5) }
        if let c = clips.first, c.size.height > 0 { return c.size.width / c.size.height }
        return 16.0 / 9.0
    }
    var aspectLabel: String {
        aspect == .custom ? "\(Num.fmt(customW, 2)):\(Num.fmt(customH, 2))" : aspect.label
    }
    var outputSize: (Int, Int) { GifEncoder.size(width: width, aspect: outputAspect) }

    /// Window subtitle, e.g. "2 đoạn · 5,2 giây · 680 × 408 px".
    var summary: String {
        guard !clips.isEmpty else { return "" }
        let (w, h) = outputSize
        return "\(clips.count) đoạn · \(Num.fmt(totalDuration, 1)) giây · \(w) × \(h) px"
    }


    // MARK: Loading

    func openPanel() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.movie, .video, .mpeg4Movie, .quickTimeMovie]
        panel.allowsMultipleSelection = true
        if panel.runModal() == .OK { add(panel.urls) }
    }

    func add(_ urls: [URL]) {
        enqueueLoad { for url in urls { await self.addOne(url) } }
    }

    /// Loads run one after another, so restored and newly opened clips keep their order.
    private func enqueueLoad(_ work: @escaping @MainActor () async -> Void) {
        let prev = loadChain
        loadChain = Task { await prev?.value; await work() }
    }

    /// The previous session, offered on the start screen until the user reopens or dismisses it.
    @Published var savedSession: SessionStore.Saved?

    func offerSession() {
        if let s = session.load(), !s.clips.isEmpty { savedSession = s }
    }

    /// Reopens the clips, trims and edits from the last session.
    func restoreSession() {
        guard let saved = savedSession else { return }
        savedSession = nil
        session.suspended = true
        enqueueLoad {
            var missing: [String] = []
            var ids: [UUID] = []
            for c in saved.clips {
                let url = URL(fileURLWithPath: c.path)
                guard FileManager.default.fileExists(atPath: c.path) else { missing.append(url.lastPathComponent); continue }
                if let id = await self.addOne(url, restore: c) { ids.append(id) }
            }
            if let i = saved.selected, ids.indices.contains(i) { self.select(ids[i], autoplay: false) }
            else if let first = ids.first { self.select(first, autoplay: false) }
            self.session.suspended = false
            self.session.scheduleSave(self)
            if !missing.isEmpty {
                self.loadError = "Không tìm thấy \(missing.count) video của phiên trước (ổ đĩa chưa kết nối hoặc tệp đã bị di chuyển): "
                    + missing.joined(separator: ", ")
            }
        }
    }

    @discardableResult
    private func addOne(_ url: URL, restore: SessionStore.SavedClip? = nil) async -> UUID? {
        let asset = AVURLAsset(url: url)
        do {
            let dur = try await asset.load(.duration).seconds
            guard let track = try await asset.loadTracks(withMediaType: .video).first else {
                throw EncodeError.ffmpeg("Tệp không chứa luồng video.")
            }
            let (size, transform) = try await track.load(.naturalSize, .preferredTransform)
            let r = CGRect(origin: .zero, size: size).applying(transform)
            let disp = CGSize(width: abs(r.width), height: abs(r.height))
            var previewAsset = asset
            var previewTransform = transform
            let decodable = (try? await track.load(.isDecodable)) ?? true
            if !decodable {
                preparing = "Đang tạo bản xem trước cho “\(url.lastPathComponent)”…"
                defer { preparing = nil }
                previewAsset = AVURLAsset(url: try await GifEncoder.makeProxy(for: url))
                previewTransform = .identity
            }
            // Default selection: whole clip if short, else first 3s.
            let out = dur <= 6 ? dur : 3
            let fps = Double((try? await track.load(.nominalFrameRate)) ?? 30)
            let fd = fps > 1 ? 1 / fps : 1.0 / 30
            var clip = Clip(url: url, asset: previewAsset, duration: dur, size: disp, inPoint: 0,
                            outPoint: (out / fd).rounded(.down) * fd)
            clip.frameDur = fd
            clip.previewTransform = previewTransform
            clip.isProxy = !decodable
            clip.edit.fit = disp.height > disp.width   // vertical → fit + blurred bg by default
            if let r = restore {
                let a = min(max(0, r.inPoint), dur), b = min(max(a, r.outPoint), dur)
                if b - a >= Self.minLength { clip.inPoint = a; clip.outPoint = b }
                clip.edit = r.edit
            }
            clips.append(clip)
            if restore == nil {
                savedSession = nil
                if status != .idle, !isEncoding { status = .idle }
                select(clip.id, autoplay: false)
            }
            makeThumbnails(for: clip.id)
            analyzeScenes(for: clip.id)
            return clip.id
        } catch {
            loadError = "“\(url.lastPathComponent)”: \(error.localizedDescription)"
            return nil
        }
    }

    /// Keeps each card's picture on the middle of its selected range (debounced while dragging).
    private func scheduleCardThumbs() {
        guard !writingCardThumb, clips.contains(where: { c in c.cardThumbTime.map { abs($0 - (c.inPoint + c.outPoint) / 2) > 0.001 } ?? true })
        else { return }
        cardThumbTask?.cancel()
        cardThumbTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 250_000_000)
            guard let self, !Task.isCancelled else { return }
            for c in self.clips {
                let t = (c.inPoint + c.outPoint) / 2
                if let old = c.cardThumbTime, abs(old - t) <= 0.001 { continue }
                let gen = AVAssetImageGenerator(asset: c.asset)
                gen.maximumSize = CGSize(width: 320, height: 320)
                gen.appliesPreferredTrackTransform = true
                let tol = CMTime(seconds: c.frameDur, preferredTimescale: 90000)
                gen.requestedTimeToleranceBefore = tol
                gen.requestedTimeToleranceAfter = tol
                let img = try? await gen.image(at: CMTime(seconds: t, preferredTimescale: 90000)).image
                if Task.isCancelled { return }
                guard let i = self.clips.firstIndex(where: { $0.id == c.id }) else { continue }
                self.writingCardThumb = true
                self.clips[i].cardThumb = img ?? self.clips[i].cardThumb
                self.clips[i].cardThumbTime = t
                self.writingCardThumb = false
            }
        }
    }

    private func makeThumbnails(for id: UUID) {
        guard let c = clips.first(where: { $0.id == id }) else { return }
        let asset = c.asset, duration = c.duration
        Task {
            let gen = AVAssetImageGenerator(asset: asset)
            gen.maximumSize = CGSize(width: 180, height: 180)
            gen.appliesPreferredTrackTransform = true
            let tol = CMTime(seconds: max(0.3, duration / 30), preferredTimescale: 600)
            gen.requestedTimeToleranceBefore = tol
            gen.requestedTimeToleranceAfter = tol
            let n = 16
            let times = (0..<n).map { CMTime(seconds: duration * (Double($0) + 0.5) / Double(n), preferredTimescale: 600) }
            var imgs: [CGImage] = []
            for await result in gen.images(for: times) {
                if let img = try? result.image {
                    imgs.append(img)
                    if let i = clips.firstIndex(where: { $0.id == id }) { clips[i].thumbnails = imgs }
                }
            }
        }
    }

    func remove(_ id: UUID) {
        guard let i = clips.firstIndex(where: { $0.id == id }) else { return }
        sceneQueue.removeAll { $0 == id }
        if sceneClip == id { sceneTask?.cancel() }
        clips.remove(at: i)
        if selectedID == id {
            if clips.isEmpty {
                selectedID = nil
                player.replaceCurrentItem(with: nil)
                stopPreload()
                preview.detach()
                timeline.detach()
                isPlaying = false
                playAll = false
            } else {
                select(clips[min(i, clips.count - 1)].id, autoplay: isPlaying)
            }
        }
    }

    /// Adds another segment of the selected clip's video right after it, starting where
    /// the current one ends (at the next scene cut when scenes are known), same edits.
    func addSegmentFromSelected() {
        guard let i = selectedIndex else { return }
        let src = clips[i]
        let len = src.length
        var a = src.outPoint
        if let cut = src.cuts.first(where: { $0 >= src.outPoint - src.frameDur / 2 }) { a = cut }
        var b = min(a + len, src.duration)
        if let next = src.cuts.first(where: { $0 > a + src.frameDur / 2 }), next < b, next - a >= 1 { b = next }
        if b - a < Self.minLength { a = 0; b = min(len, src.duration) }
        var c = Clip(url: src.url, asset: src.asset, duration: src.duration, size: src.size,
                     inPoint: snapFrame(a, src), outPoint: snapFrame(b, src))
        c.frameDur = src.frameDur
        c.previewTransform = src.previewTransform
        c.isProxy = src.isProxy
        c.cuts = src.cuts
        c.scenesAnalyzed = src.scenesAnalyzed
        c.thumbnails = src.thumbnails
        c.cardThumb = nil
        c.edit = src.edit
        clips.insert(c, at: i + 1)
        select(c.id, autoplay: false)
        if !c.scenesAnalyzed { analyzeScenes(for: c.id) }
    }

    func removeAll() {
        sceneQueue.removeAll()
        sceneTask?.cancel()
        clips.removeAll()
        selectedID = nil
        player.replaceCurrentItem(with: nil)
        stopPreload()
        preview.detach()
        timeline.detach()
        isPlaying = false
        playAll = false
        status = .idle
    }

    func move(_ id: UUID, before target: UUID) {
        guard id != target,
              let from = clips.firstIndex(where: { $0.id == id }),
              let to = clips.firstIndex(where: { $0.id == target }) else { return }
        withAnimation(.easeInOut(duration: 0.15)) {
            clips.move(fromOffsets: IndexSet(integer: from), toOffset: to > from ? to + 1 : to)
        }
    }

    func select(_ id: UUID, autoplay: Bool? = nil, reload: Bool = false) {
        guard let c = clips.first(where: { $0.id == id }) else { return }
        let t0 = CACurrentMediaTime()
        let usedPreload = preloaded?.id == id
        defer { trace("select \(c.name) preloaded=\(usedPreload) took \(Int((CACurrentMediaTime() - t0) * 1000)) ms") }
        let curURL = (player.currentItem?.asset as? AVURLAsset)?.url
        var changed = reload || player.currentItem == nil || curURL != c.asset.url
        var positioned = false
        var running = false
        if !reload, let p = preloaded, p.id == id {
            // Swap to the player that already holds this clip, positioned at its in point.
            let old = player
            active = 1 - active
            old.pause()
            positioned = abs(p.time - midFrame(c.inPoint, c)) < 0.0005
            preview.attach(p.item, transform: c.previewTransform, output: p.output,
                           minTime: positioned ? c.inPoint - c.frameDur / 2 : -.infinity)
            if positioned { clock.time = p.time }
            // Already playing toward its in point: re-issuing play or the end time would make
            // AVPlayer resync and freeze for ~150 ms.
            running = positioned && p.started && players[active].rate > 0
            if running {
                // Ideal: the next clip is half a frame before its first frame at the switch.
                let err = players[active].currentTime().seconds - p.time
                let target = -c.frameDur / 2
                startLatency = min(max(startLatency + (target - err) * 0.5, 0), 0.5)
                trace(String(format: "switch error %+.0f ms → start latency %.0f ms", err * 1000, startLatency * 1000))
            }
            changed = false
            old.replaceCurrentItem(with: nil)
        }
        preloaded = nil
        selectedID = id
        if changed {
            let item = AVPlayerItem(asset: c.asset)
            player.replaceCurrentItem(with: item)
            preview.attach(item, transform: c.previewTransform)
        }
        timeline.attach(c)
        syncPreview()
        if !positioned { seek(c.inPoint) }
        if let play = autoplay {
            isPlaying = play
            if play { if !running { startPlayback() } } else { player.pause() }
        } else if isPlaying, !running {
            startPlayback()
        }
        preloadNext()
    }

    /// During "Phát toàn bộ", loads the next clip on the idle player and seeks it to its in point.
    private func preloadNext() {
        guard playAll, isPlaying, clips.count > 1, let i = selectedIndex else { return }
        let n = clips[(i + 1) % clips.count]
        // Always on the idle player, even for the same video, so the switch needs no seek.
        let other = players[1 - active]
        let item = AVPlayerItem(asset: n.asset)
        let out = PreviewEngine.makeOutput(for: item)
        item.forwardPlaybackEndTime = cm(n.outPoint - n.frameDur / 2)
        other.replaceCurrentItem(with: item)
        let t = midFrame(n.inPoint, n)
        let from = max(0, t - Self.leadIn)
        other.seek(to: cm(from), toleranceBefore: .zero, toleranceAfter: .zero) { [weak other] done in
            guard done else { return }
            DispatchQueue.main.async {
                // Warm up the decoder so playback starts on the first display refresh.
                if let other, other.currentItem === item, other.status == .readyToPlay {
                    other.preroll(atRate: 1)
                }
            }
        }
        preloaded = Preload(id: n.id, item: item, output: out, time: t)
        preview.setStandby(out)
    }

    private func stopPreload() {
        preloaded = nil
        preview.setStandby(nil)
        players[1 - active].replaceCurrentItem(with: nil)
    }

    private func midFrame(_ t: Double, _ c: Clip) -> Double {
        min(frameStart(t, c) + c.frameDur / 2, max(0, c.duration - c.frameDur / 2))
    }

    // MARK: Per-clip edits (selected)

    private func mutateSelected(_ f: (inout Clip) -> Void) {
        guard let i = selectedIndex else { return }
        f(&clips[i])
        if isPlaying && !scrubbing { applyPlaybackEnd() }
    }

    /// Binding into the selected clip's edit, for sidebar controls.
    func edit<T>(_ kp: WritableKeyPath<ClipEdit, T>) -> Binding<T> {
        Binding(
            get: { (self.selected?.edit ?? ClipEdit())[keyPath: kp] },
            set: { v in self.mutateEdit { $0[keyPath: kp] = v } }
        )
    }

    func mutateEdit(_ f: (inout ClipEdit) -> Void) {
        mutateSelected { f(&$0.edit) }
        syncPreview()
    }

    func rotate(_ deg: Int) {
        mutateEdit { $0.rotation = (($0.rotation + deg) % 360 + 360) % 360; $0.resetFraming() }
    }

    func resetEdit() {
        guard let c = selected else { return }
        mutateEdit { $0 = ClipEdit(); $0.fit = c.isVertical }
    }

    func applyLookToAll() {
        guard let src = selected?.edit else { return }
        for i in clips.indices { clips[i].edit.copyLook(from: src) }
    }

    // MARK: Preview

    /// Push the selected clip's edit to the live preview (redraws on the next display frame).
    func syncPreview() {
        guard let c = selected else { return }
        preview.update(edit: c.edit, outWidth: Double(width))
        preview.markDirty()
        player.defaultRate = Float(c.edit.speed)
        if isPlaying && !scrubbing { player.rate = Float(c.edit.speed) }
    }

    /// Re-open the selected clip through an H.264 proxy (for sources the preview can't show).
    func useProxyForSelected() {
        guard let c = selected, !c.isProxy else { return }
        let id = c.id, url = c.url
        Task {
            preparing = "Đang tạo bản xem trước cho “\(url.lastPathComponent)”…"
            defer { preparing = nil }
            do {
                let proxy = try await GifEncoder.makeProxy(for: url)
                guard let i = clips.firstIndex(where: { $0.id == id }) else { return }
                clips[i].asset = AVURLAsset(url: proxy)
                clips[i].previewTransform = .identity
                clips[i].isProxy = true
                if selectedID == id { select(id, autoplay: isPlaying, reload: true) }
                if clips[i].thumbnails.isEmpty { makeThumbnails(for: id) }
            } catch {
                loadError = "Không tạo được bản xem trước: \(error.localizedDescription)"
            }
        }
    }

    // MARK: Frame-accurate times
    //
    // In/out points sit on frame boundaries: the range covers frames whose start time is in
    // [in, out). Seeks land in the middle of a frame and exports are offset by half a frame,
    // so float rounding can never show or export a frame from the neighbouring scene.

    /// Nearest frame boundary.
    private func snapFrame(_ t: Double, _ c: Clip) -> Double { (t / c.frameDur).rounded() * c.frameDur }
    /// Start of the frame displayed at time t.
    private func frameStart(_ t: Double, _ c: Clip) -> Double { (t / c.frameDur + 0.01).rounded(.down) * c.frameDur }

    func setIn(_ t: Double) {
        mutateSelected { c in c.inPoint = min(max(0, snapFrame(t, c)), c.outPoint - Self.minLength) }
    }

    func setOut(_ t: Double) {
        mutateSelected { c in c.outPoint = max(min(c.duration, snapFrame(t, c)), c.inPoint + Self.minLength) }
    }

    /// Move the whole range so it starts at `t`.
    func moveRange(to t: Double) {
        mutateSelected { c in
            let len = c.length
            let s = min(max(0, snapFrame(t, c)), c.duration - len)
            c.inPoint = s
            c.outPoint = s + len
        }
    }

    func jumpToIn() {
        guard let c = selected else { return }
        seek(c.inPoint)
    }

    /// I: set the in point at the playhead. If that is past the out point, move the whole
    /// selection there and keep its length (handy for "take 3 s from here").
    func markIn() {
        guard let cur = selected else { return }
        let t = frameStart(currentTime, cur)
        mutateSelected { c in
            if t > c.outPoint - Self.minLength {
                let len = c.length
                c.inPoint = min(max(0, t), c.duration - Self.minLength)
                c.outPoint = min(c.duration, c.inPoint + len)
            } else {
                c.inPoint = max(0, t)
            }
        }
        if let c = selected { timeline.reveal(c.inPoint, c.outPoint) }
    }

    /// O: set the out point at the playhead. If that is before the in point, move the
    /// selection so it ends there and keep its length.
    func markOut() {
        guard let cur = selected else { return }
        let t = frameStart(currentTime, cur) + cur.frameDur     // include the frame on screen
        mutateSelected { c in
            if t < c.inPoint + Self.minLength {
                let len = c.length
                c.outPoint = max(min(c.duration, t), Self.minLength)
                c.inPoint = max(0, c.outPoint - len)
            } else {
                c.outPoint = min(c.duration, t)
            }
        }
        if let c = selected { timeline.reveal(c.inPoint, c.outPoint) }
    }

    // MARK: Playback

    private func tick(_ t: Double) {
        guard t.isFinite, pendingSeeks == 0 else { return }
        guard isPlaying, !scrubbing, let c = selected else { clock.time = t; return }
        clock.time = max(t, c.inPoint)      // a lead-in started clip runs slightly before its in point
        // Start the preloaded next clip early so it is already running at the switch.
        if var p = preloaded, !p.started, c.outPoint - t <= Self.leadIn + startLatency {
            players[1 - active].playImmediately(atRate: 1)
            p.started = true
            preloaded = p
        }
        // The item's forwardPlaybackEndTime stops playback exactly before the out frame;
        // this is only a fallback.
        if t > c.outPoint + 0.15 {
            advance()
        } else if t < c.inPoint - Self.leadIn - 0.15 {
            seek(c.inPoint)
        }
    }

    private func reachedEnd() {
        guard isPlaying, !scrubbing, pendingSeeks == 0 else { return }
        advance()
    }

    /// Loop current clip, or move to the next clip when previewing the whole sequence.
    private func advance() {
        guard let i = selectedIndex else { return }
        if playAll && clips.count > 1 {
            select(clips[(i + 1) % clips.count].id, autoplay: true)
        } else {
            seek(clips[i].inPoint)
            startPlayback()
        }
    }

    private func cm(_ t: Double) -> CMTime { CMTime(seconds: t, preferredTimescale: 90_000) }

    /// Shows the frame whose interval contains t (seeks to the middle of that frame).
    private func seek(_ t: Double) {
        let target = selected.map { midFrame(t, $0) } ?? t
        pendingSeeks += 1
        let p = player
        p.seek(to: cm(target), toleranceBefore: .zero, toleranceAfter: .zero) { [weak self] _ in
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.pendingSeeks -= 1
                    // A player that had stopped at its end time doesn't resume by itself after seeking.
                    if self.pendingSeeks == 0, self.isPlaying, !self.scrubbing, p === self.player, p.rate == 0 {
                        self.startPlayback()
                    }
                }
            }
        }
        clock.time = target
    }

    /// Playback stops right before the first frame after the out point.
    private func applyPlaybackEnd() {
        guard let c = selected, let item = player.currentItem else { return }
        let end = cm(c.outPoint - c.frameDur / 2)
        if item.forwardPlaybackEndTime != end { item.forwardPlaybackEndTime = end }
    }

    private func clearPlaybackEnd() {
        player.currentItem?.forwardPlaybackEndTime = .invalid
    }

    private func startPlayback() {
        applyPlaybackEnd()
        player.playImmediately(atRate: 1)
    }

    func togglePlay() {
        guard let c = selected else { return }
        isPlaying.toggle()
        if isPlaying {
            if currentTime < c.inPoint || currentTime >= c.outPoint - c.frameDur { seek(c.inPoint) }
            startPlayback()
        } else {
            player.pause()
            clearPlaybackEnd()
            playAll = false
            stopPreload()
        }
    }

    func togglePlayAll() {
        if playAll {
            playAll = false
            isPlaying = false
            player.pause()
            clearPlaybackEnd()
            stopPreload()
            return
        }
        guard let first = clips.first else { return }
        playAll = true
        select(first.id, autoplay: true)
    }

    /// Scrub to a time while dragging (pauses playback until `endScrub`).
    func scrub(_ t: Double) {
        if !scrubbing {
            scrubbing = true
            wasPlayingBeforeScrub = isPlaying
            player.pause()
            clearPlaybackEnd()
        }
        guard let c = selected else { return }
        seek(min(max(0, t), c.duration))
    }

    func endScrub(resumeFromIn: Bool) {
        guard scrubbing else { return }
        scrubbing = false
        if wasPlayingBeforeScrub {
            if resumeFromIn, let c = selected { seek(c.inPoint) }
            startPlayback()
        } else {
            isPlaying = false
        }
    }

    /// Step by whole frames (or seconds when `seconds` is set).
    func step(frames: Int = 0, seconds: Double = 0) {
        guard let c = selected else { return }
        if isPlaying { togglePlay() }
        let base = frameStart(currentTime, c)
        seek(min(max(0, base + Double(frames) * c.frameDur + seconds), c.duration))
    }

    // MARK: Export

    /// Ask where to save, then export. The chosen folder is remembered.
    func export() {
        guard !clips.isEmpty, !isEncoding else { return }
        let panel = NSSavePanel()
        panel.title = "Xuất GIF"
        panel.prompt = "Xuất"
        panel.message = "Chọn nơi lưu ảnh GIF"
        panel.allowedContentTypes = [.gif]
        panel.canCreateDirectories = true
        panel.isExtensionHidden = false
        panel.nameFieldStringValue = clips[0].name + ".gif"
        if let dir = d.string(forKey: "exportDir"), FileManager.default.fileExists(atPath: dir) {
            panel.directoryURL = URL(fileURLWithPath: dir, isDirectory: true)
        } else {
            panel.directoryURL = clips[0].url.deletingLastPathComponent()
        }
        let done: (NSApplication.ModalResponse) -> Void = { [weak self] r in
            guard r == .OK, let url = panel.url else { return }
            MainActor.assumeIsolated { self?.startExport(to: url) }
        }
        if let w = NSApp.keyWindow {
            panel.beginSheetModal(for: w, completionHandler: done)
        } else {
            done(panel.runModal())
        }
    }

    private func startExport(to output: URL) {
        d.set(output.deletingLastPathComponent().path, forKey: "exportDir")
        cancelExport()
        // Half-frame offsets: include the frame starting at `in`, exclude the one starting at `out`.
        let specs = clips.map { c -> ClipSpec in
            let start = max(0, c.inPoint - c.frameDur / 2)
            let end = c.outPoint - c.frameDur / 2
            return ClipSpec(url: c.url, start: start, duration: max(c.frameDur, end - start),
                            sourceSize: c.size, edit: c.edit)
        }
        let req = GifRequest(clips: specs, aspect: outputAspect, width: width, fps: fps, dither: dither,
                             limitBytes: Int(limitMB * 1_000_000), autoFit: autoFit,
                             priority: fitPriority, minFps: minFps, minWidth: minWidth, force: forceLimit)
        status = .encoding("Đang xuất GIF…")
        exportTask = Task { [weak self] in
            do {
                let result = try await GifEncoder.makeGif(req, output: output) { msg in
                    self?.status = .encoding(msg)
                }
                self?.status = .done(result)
            } catch is CancellationError {
                self?.status = .idle
            } catch {
                self?.status = .failed("Xuất GIF thất bại. \(error.localizedDescription)")
            }
        }
    }

    func cancelExport() {
        exportTask?.cancel()
        exportTask = nil
        if isEncoding { status = .idle }
    }

    // MARK: Scenes

    func analyzeScenes(for id: UUID) {
        guard !sceneQueue.contains(id), sceneClip != id else { return }
        sceneQueue.append(id)
        runSceneQueue()
    }

    private func updateClip(_ id: UUID, _ f: (inout Clip) -> Void) {
        if let i = clips.firstIndex(where: { $0.id == id }) { f(&clips[i]) }
    }

    private func runSceneQueue() {
        guard sceneTask == nil else { return }
        sceneTask = Task { [weak self] in
            while let self, !self.sceneQueue.isEmpty {
                let id = self.sceneQueue.removeFirst()
                guard let c = self.clips.first(where: { $0.id == id }) else { continue }
                // Analyze the H.264 proxy when there is one (same timeline, much faster).
                let src = c.isProxy ? c.asset.url : c.url
                if let cached = SceneDetector.cached(for: src) {
                    let fd = c.frameDur
                    self.updateClip(id) { $0.cuts = cached.map { ($0 / fd).rounded() * fd }; $0.scenesAnalyzed = true; $0.sceneProgress = nil }
                    continue
                }
                self.sceneClip = id
                self.updateClip(id) { $0.sceneProgress = 0 }
                do {
                    let fd = c.frameDur
                    let align: ([Double]) -> [Double] = { $0.map { ($0 / fd).rounded() * fd } }
                    let cuts = align(try await SceneDetector.detect(src: src, duration: c.duration) { [weak self] cuts, p in
                        self?.updateClip(id) { $0.cuts = align(cuts); $0.sceneProgress = p < 1 ? p : nil }
                    })
                    SceneDetector.store(cuts, for: src)
                    self.updateClip(id) { $0.cuts = cuts; $0.scenesAnalyzed = true; $0.sceneProgress = nil }
                } catch {
                    self.updateClip(id) { $0.sceneProgress = nil }
                }
                self.sceneClip = nil
                if Task.isCancelled { break }
            }
            self?.sceneTask = nil
            if let self, !self.sceneQueue.isEmpty { self.runSceneQueue() }
        }
    }

    /// The scene containing time t: (previous cut or 0, next cut or end).
    func sceneBounds(at t: Double, in c: Clip) -> (Double, Double) {
        let start = c.cuts.last { $0 <= t + 0.001 } ?? 0
        let end = c.cuts.first { $0 > t + 0.001 } ?? c.duration
        return (start, end)
    }

    /// X: select the whole scene under time t (default: playhead).
    func selectScene(at t: Double? = nil) {
        guard let c = selected else { return }
        let (a, b) = sceneBounds(at: t ?? currentTime, in: c)
        guard b - a >= Self.minLength else { return }
        mutateSelected { $0.inPoint = a; $0.outPoint = b }
        timeline.reveal(a, b)
        if !isPlaying { seek(a) }
    }

    /// ↑/↓: move the playhead to the previous/next scene cut.
    func jumpScene(_ direction: Int) {
        guard let c = selected else { return }
        let t = currentTime
        let target: Double? = direction > 0
            ? (c.cuts.first { $0 > t + 0.02 } ?? c.duration)
            : (c.cuts.last { $0 < t - 0.05 } ?? 0)
        guard let target else { return }
        if isPlaying { togglePlay() }
        seek(target)
        timeline.reveal(target, target)
    }

    /// Snap a time to the nearest scene cut (or clip start/end) within `tolerance` seconds.
    func snapped(_ t: Double, tolerance: Double) -> Double {
        guard snapToScenes, let c = selected else { return t }
        var best = t, bestD = tolerance
        for x in c.cuts + [0, c.duration] where abs(x - t) < bestD {
            best = x
            bestD = abs(x - t)
        }
        return best
    }

    // MARK: Timeline scroll / zoom

    /// Over the timeline: ⌥/⌘ + scroll zooms around the pointer; plain scroll pans.
    private func installScrollMonitor() {
        guard scrollMonitor == nil else { return }
        scrollMonitor = NSEvent.addLocalMonitorForEvents(matching: .scrollWheel) { [weak self] e in
            MainActor.assumeIsolated {
                guard let self, let v = self.timeline.hitView, v.window === e.window, v.bounds.width > 0 else { return e }
                let p = v.convert(e.locationInWindow, from: nil)
                guard v.bounds.contains(p) else { return e }
                let z = self.timeline
                var dx = Double(e.scrollingDeltaX), dy = Double(e.scrollingDeltaY)
                if !e.hasPreciseScrollingDeltas { dx *= 8; dy *= 8 }
                if e.modifierFlags.contains(.option) || e.modifierFlags.contains(.command) {
                    let t = z.vs + Double(p.x / v.bounds.width) * z.vl
                    z.zoom(exp(dy * 0.01), around: t)
                } else {
                    let d = abs(dx) >= abs(dy) ? dx : dy
                    z.pan(-d / Double(v.bounds.width) * z.vl)
                }
                return nil
            }
        }
    }

    // MARK: Keyboard

    func installKeyMonitor() {
        installScrollMonitor()
        guard keyMonitor == nil else { return }
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] e in
            MainActor.assumeIsolated {
                guard let self, self.selected != nil else { return e }
                if NSApp.keyWindow?.firstResponder is NSText { return e }
                if !e.modifierFlags.intersection([.command, .control, .option]).isEmpty { return e }
                let big = e.modifierFlags.contains(.shift)
                switch e.keyCode {
                case 49: self.togglePlay(); return nil                  // space
                case 123: big ? self.step(seconds: -1) : self.step(frames: -1); return nil   // ←
                case 124: big ? self.step(seconds: 1) : self.step(frames: 1); return nil     // →
                case 34: self.markIn(); return nil                       // I
                case 31: self.markOut(); return nil                      // O
                case 126: self.jumpScene(-1); return nil                // ↑
                case 125: self.jumpScene(1); return nil                 // ↓
                case 7: self.selectScene(); return nil                  // X
                case 1: self.snapToScenes.toggle(); return nil          // S
                case 24: self.timeline.zoom(1.6, around: self.currentTime); return nil      // =
                case 27: self.timeline.zoom(1 / 1.6, around: self.currentTime); return nil  // -
                default: return e
                }
            }
        }
    }
}
