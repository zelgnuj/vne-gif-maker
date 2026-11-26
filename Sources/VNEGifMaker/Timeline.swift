import AVFoundation
import AppKit
import SwiftUI

// MARK: - Zoom / visible window model

/// The visible part of the selected clip's timeline, plus thumbnails for that part.
@MainActor
final class TimelineZoom: ObservableObject {
    struct Tile: Identifiable {
        let id = UUID()
        let start: Double
        let dur: Double
        let image: CGImage
    }

    static let minLength = 0.5     // shortest visible window, seconds

    @Published private(set) var vs: Double = 0      // visible start
    @Published private(set) var vl: Double = 1      // visible length
    @Published private(set) var tiles: [Tile] = []
    private(set) var duration: Double = 1
    private(set) var clipID: UUID?
    private var asset: AVAsset?
    private var thumbTask: Task<Void, Never>?
    /// Recently made tile sets, so switching back to a clip (e.g. "Phát toàn bộ") decodes nothing.
    private var tileCache: [String: [Tile]] = [:]
    private var tileCacheOrder: [String] = []

    /// The detail track view (for scroll-wheel hit testing).
    weak var hitView: NSView?
    var trackWidth: CGFloat = 600 {
        didSet { if abs(trackWidth - oldValue) > 40 { scheduleThumbs() } }
    }

    func attach(_ clip: Clip) {
        let newClip = clip.id != clipID
        guard newClip || asset !== clip.asset else { return }
        clipID = clip.id
        asset = clip.asset
        duration = max(clip.duration, 0.1)
        tiles = []
        if newClip {
            if duration <= 20 {
                vs = 0; vl = duration
            } else {
                let len = min(duration, max(clip.length * 5, 15))
                vl = len
                vs = min(max((clip.inPoint + clip.outPoint) / 2 - len / 2, 0), duration - len)
            }
        }
        scheduleThumbs()
    }

    func detach() {
        clipID = nil
        asset = nil
        tiles = []
        thumbTask?.cancel()
    }

    func setWindow(_ start: Double, _ length: Double) {
        let len = min(max(length, min(Self.minLength, duration)), duration)
        let st = min(max(start, 0), duration - len)
        guard abs(st - vs) > 1e-6 || abs(len - vl) > 1e-6 else { return }
        vs = st
        vl = len
        scheduleThumbs()
    }

    /// factor > 1 zooms in, keeping time `t` under the same screen position.
    func zoom(_ factor: Double, around t: Double) {
        let len = min(max(vl / factor, min(Self.minLength, duration)), duration)
        let frac = vl > 0 ? (t - vs) / vl : 0.5
        setWindow(t - frac * len, len)
    }

    func pan(_ seconds: Double) { setWindow(vs + seconds, vl) }

    /// Make sure [a, b] is visible (pans, or zooms out if it doesn't fit).
    func reveal(_ a: Double, _ b: Double) {
        guard a < vs || b > vs + vl else { return }
        if b - a > vl {
            fit(a, b)
        } else {
            setWindow((a + b) / 2 - vl / 2, vl)
        }
    }

    func fit(_ a: Double, _ b: Double) {
        let len = max((b - a) * 1.6, Self.minLength)
        setWindow((a + b) / 2 - len / 2, len)
    }

    func showAll() { setWindow(0, duration) }

    /// 0 = whole clip, 1 = closest zoom (log scale).
    var level: Double {
        guard duration > Self.minLength else { return 0 }
        return log(vl / duration) / log(Self.minLength / duration)
    }

    func setLevel(_ z: Double, around t: Double) {
        guard duration > Self.minLength else { return }
        let len = duration * pow(Self.minLength / duration, min(max(z, 0), 1))
        zoom(vl / len, around: t)
    }

    func x(_ t: Double, _ w: CGFloat) -> CGFloat { CGFloat((t - vs) / max(vl, 1e-6)) * w }
    func t(_ x: CGFloat, _ w: CGFloat) -> Double { vs + Double(x / max(w, 1)) * vl }

    private func scheduleThumbs() {
        thumbTask?.cancel()
        guard let asset else { return }
        let start = vs, len = vl
        let n = max(4, Int(trackWidth / 80))
        let key = "\(ObjectIdentifier(asset).hashValue)|\(Int(start * 1000))|\(Int(len * 1000))|\(n)"
        if let hit = tileCache[key] { tiles = hit; return }
        let progressive = tiles.isEmpty
        thumbTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 120_000_000)
            if Task.isCancelled { return }
            let gen = AVAssetImageGenerator(asset: asset)
            gen.maximumSize = CGSize(width: 200, height: 200)
            gen.appliesPreferredTrackTransform = true
            let tileDur = len / Double(n)
            let tol = CMTime(seconds: tileDur / 2, preferredTimescale: 600)
            gen.requestedTimeToleranceBefore = tol
            gen.requestedTimeToleranceAfter = tol
            let times = (0..<n).map { CMTime(seconds: start + tileDur * (Double($0) + 0.5), preferredTimescale: 600) }
            var fresh: [Tile] = []
            for await r in gen.images(for: times) {
                if Task.isCancelled { return }
                guard let img = try? r.image else { continue }
                fresh.append(Tile(start: r.requestedTime.seconds - tileDur / 2, dur: tileDur, image: img))
                if progressive { self?.tiles = fresh }
            }
            if !Task.isCancelled, let self {
                self.tiles = fresh
                self.tileCache[key] = fresh
                self.tileCacheOrder.removeAll { $0 == key }
                self.tileCacheOrder.append(key)
                if self.tileCacheOrder.count > 12 { self.tileCache[self.tileCacheOrder.removeFirst()] = nil }
            }
        }
    }
}

/// Lets the scroll-wheel monitor find the detail track.
struct HitViewReporter: NSViewRepresentable {
    let zoom: TimelineZoom
    func makeNSView(context: Context) -> NSView {
        let v = PassthroughView()
        MainActor.assumeIsolated { zoom.hitView = v }
        return v
    }
    func updateNSView(_ v: NSView, context: Context) {
        MainActor.assumeIsolated { zoom.hitView = v }
    }
    final class PassthroughView: NSView {
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
    }
}

// MARK: - Timeline view

final class TrimDrag: ObservableObject {
    enum Mode { case inHandle, outHandle, move(Double), scrub }
    var mode: Mode?
    var zoomStart: Double?
}

struct TrimTimeline: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var zoom: TimelineZoom
    @StateObject private var drag = TrimDrag()
    private let grip: CGFloat = 12

    var body: some View {
        if let clip = state.selected {
            VStack(spacing: 6) {
                OverviewBar(zoom: zoom, clock: state.clock, clip: clip)
                    .padding(.horizontal, grip)
                TimeRuler(zoom: zoom, total: clip.duration)
                    .frame(height: 18)
                    .padding(.horizontal, grip)
                detailTrack(clip)
                    .frame(height: 56)
                    .padding(.horizontal, grip)
                controls(clip)
                    .padding(.horizontal, grip)
            }
        }
    }

    // MARK: Detail track

    private func detailTrack(_ clip: Clip) -> some View {
        GeometryReader { geo in
            let w = geo.size.width, h = geo.size.height
            let x0 = zoom.x(clip.inPoint, w)
            let x1 = zoom.x(clip.outPoint, w)
            ZStack(alignment: .topLeading) {
                Rectangle().fill(Color.gray.opacity(0.2))
                ForEach(zoom.tiles) { tile in
                    Image(decorative: tile.image, scale: 1)
                        .resizable()
                        .aspectRatio(contentMode: .fill)
                        .frame(width: max(1, CGFloat(tile.dur / zoom.vl) * w + 1), height: h)
                        .clipped()
                        .offset(x: zoom.x(tile.start, w))
                }
                Rectangle().fill(.black.opacity(0.6))
                    .frame(width: min(max(0, x0), w), height: h)
                Rectangle().fill(.black.opacity(0.6))
                    .frame(width: min(max(0, w - x1), w), height: h)
                    .offset(x: max(0, x1))
                SceneMarks(cuts: clip.cuts, start: zoom.vs, length: zoom.vl, prominent: true)
                    .allowsHitTesting(false)
                ZStack {
                    RoundedRectangle(cornerRadius: 5).stroke(Brand.bright, lineWidth: 3)
                    HStack {
                        gripView
                        Spacer(minLength: 0)
                        gripView
                    }
                }
                .frame(width: max(grip * 2, x1 - x0 + grip * 2), height: h)
                .offset(x: x0 - grip)
                .allowsHitTesting(false)
                ClockMarker(clock: state.clock, height: h + 8) { zoom.x($0, w) }
                    .offset(y: -4)
            }
            .mask(Rectangle().padding(.horizontal, -grip).padding(.vertical, -6))
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { g in onDrag(g, clip: clip, width: w, x0: x0, x1: x1) }
                    .onEnded { _ in
                        drag.mode = nil
                        state.endScrub(resumeFromIn: true)
                    }
            )
            .simultaneousGesture(
                MagnifyGesture()
                    .onChanged { v in
                        if drag.zoomStart == nil { drag.zoomStart = zoom.vl }
                        let target = (drag.zoomStart ?? zoom.vl) / v.magnification
                        zoom.zoom(zoom.vl / target, around: zoom.t(v.startLocation.x, w))
                    }
                    .onEnded { _ in drag.zoomStart = nil }
            )
            .simultaneousGesture(
                SpatialTapGesture(count: 2).onEnded { v in
                    state.selectScene(at: zoom.t(v.location.x, w))
                }
            )
            .background(HitViewReporter(zoom: zoom))
            .onAppear { zoom.trackWidth = w }
            .onChange(of: w) { zoom.trackWidth = w }
        }
        .help("Kéo hai mép đoạn chọn để cắt · kéo phần giữa để dời đoạn · nhấp bên ngoài để tua · nhấp đúp để chọn cả cảnh\n⌥/⌘ + cuộn hoặc chụm hai ngón để thu phóng · cuộn ngang để di chuyển · giữ ⌘ khi kéo để tạm tắt bắt dính")
    }

    private var gripView: some View {
        RoundedRectangle(cornerRadius: 3)
            .fill(Brand.bright)
            .frame(width: grip)
            .overlay(Capsule().fill(Color.black.opacity(0.6)).frame(width: 2, height: 16))
    }

    private func onDrag(_ g: DragGesture.Value, clip: Clip, width w: CGFloat, x0: CGFloat, x1: CGFloat) {
        // Auto-scroll when dragging past the edges.
        if g.location.x < 0 { zoom.pan(-zoom.vl * 0.02) }
        else if g.location.x > w { zoom.pan(zoom.vl * 0.02) }
        let raw = zoom.t(min(max(g.location.x, 0), w), w)
        let tol = Double(8 / max(w, 1)) * zoom.vl                 // 8 px
        let noSnap = NSEvent.modifierFlags.contains(.command)
        let snap: (Double) -> Double = { noSnap ? $0 : state.snapped($0, tolerance: tol) }
        let t = snap(raw)
        if drag.mode == nil {
            let x = g.startLocation.x
            let dIn = abs(x - x0), dOut = abs(x - x1)
            if min(dIn, dOut) <= grip + 4 {
                drag.mode = dIn < dOut ? .inHandle : .outHandle
            } else if x > x0 && x < x1 {
                drag.mode = .move(zoom.t(x, w) - clip.inPoint)
            } else {
                drag.mode = .scrub
            }
        }
        switch drag.mode {
        case .inHandle:
            state.setIn(t)
            state.scrub(state.selected?.inPoint ?? t)
        case .outHandle:
            state.setOut(t)
            state.scrub(state.selected?.outPoint ?? t)
        case .move(let grab):
            // Snap whichever edge lands closer to a cut.
            let newIn = raw - grab, len = clip.length
            let si = snap(newIn), so = snap(newIn + len)
            let start = abs(si - newIn) > 1e-9 && (abs(so - newIn - len) < 1e-9 || abs(si - newIn) <= abs(so - newIn - len))
                ? si : (abs(so - newIn - len) > 1e-9 ? so - len : newIn)
            state.moveRange(to: start)
            state.scrub(state.selected?.inPoint ?? t)
        case .scrub, .none:
            state.scrub(t)
        }
    }

    // MARK: Controls row

    private func controls(_ clip: Clip) -> some View {
        HStack(spacing: 14) {
            timeField("Điểm vào", Binding(get: { clip.inPoint }, set: { state.setIn($0); revealSelection() }),
                      0...clip.duration)
            timeField("Điểm ra", Binding(get: { clip.outPoint }, set: { state.setOut($0); revealSelection() }),
                      0...clip.duration)
            timeField("Thời lượng", Binding(get: { clip.length }, set: { state.setOut(clip.inPoint + $0); revealSelection() }),
                      AppState.minLength...clip.duration, timecode: false)
            if clip.edit.speed != 1 {
                Text("→ \(Num.fmt(clip.outLength, 2)) s trong GIF")
                    .font(.callout)
                    .foregroundStyle(.orange)
            }
            Spacer(minLength: 8)
            zoomControls(clip)
        }
    }

    private func revealSelection() {
        if let c = state.selected { zoom.reveal(c.inPoint, c.outPoint) }
    }

    private func timeField(_ label: String, _ b: Binding<Double>, _ range: ClosedRange<Double>,
                           timecode: Bool = true) -> some View {
        HStack(spacing: 6) {
            Text(label).font(.callout).foregroundStyle(.secondary)
            if timecode {
                ScrubField(value: b, range: range, step: 1.0 / 30, decimals: 2, timecode: true)
                    .frame(width: 92, height: 20)
            } else {
                ScrubField(value: b, range: range, step: 1.0 / 30, decimals: 2, unit: " s", fixedDecimals: true)
                    .frame(width: 66, height: 20)
            }
        }
    }

    private func zoomControls(_ clip: Clip) -> some View {
        let anchor: Double = {
            let p = state.currentTime
            return (p >= zoom.vs && p <= zoom.vs + zoom.vl) ? p : zoom.vs + zoom.vl / 2
        }()
        return HStack(spacing: 6) {
            Button { zoom.zoom(1 / 1.6, around: anchor) } label: { Image(systemName: "minus.magnifyingglass") }
                .buttonStyle(.borderless)
                .help("Thu nhỏ timeline (-)")
            FineSlider(value: Binding(get: { zoom.level }, set: { zoom.setLevel($0, around: anchor) }), range: 0...1)
                .frame(width: 90)
            Button { zoom.zoom(1.6, around: anchor) } label: { Image(systemName: "plus.magnifyingglass") }
                .buttonStyle(.borderless)
                .help("Phóng to timeline (=)")
            Button("Vừa đoạn chọn") { zoom.fit(clip.inPoint, clip.outPoint) }
                .controlSize(.small)
                .help("Thu phóng để vừa đoạn đang chọn")
            Button("Toàn bộ") { zoom.showAll() }
                .controlSize(.small)
                .help("Hiển thị toàn bộ video")
        }
    }
}

// MARK: - Pieces

/// A vertical line at the playhead, observing only the clock.
struct ClockMarker: View {
    @ObservedObject var clock: PlaybackClock
    let height: CGFloat
    let x: (Double) -> CGFloat

    var body: some View {
        Rectangle().fill(.white).frame(width: 2, height: height)
            .shadow(radius: 1)
            .offset(x: x(clock.time) - 1)
            .allowsHitTesting(false)
    }
}

/// Whole-clip strip: selection (yellow), visible window (blue), playhead. Drag to navigate.
struct OverviewBar: View {
    @ObservedObject var zoom: TimelineZoom
    let clock: PlaybackClock
    let clip: Clip

    var body: some View {
        GeometryReader { geo in
            let w = geo.size.width, h = geo.size.height
            let d = max(clip.duration, 0.001)
            let n = max(clip.thumbnails.count, 1)
            ZStack(alignment: .topLeading) {
                HStack(spacing: 0) {
                    ForEach(clip.thumbnails.indices, id: \.self) { i in
                        Image(decorative: clip.thumbnails[i], scale: 1)
                            .resizable()
                            .aspectRatio(contentMode: .fill)
                            .frame(width: w / CGFloat(n), height: h)
                            .clipped()
                    }
                }
                .frame(width: w, height: h, alignment: .leading)
                .opacity(0.45)
                SceneMarks(cuts: clip.cuts, start: 0, length: d, prominent: false)
                Rectangle().fill(Brand.bright)
                    .frame(width: max(3, CGFloat(clip.length / d) * w), height: h)
                    .offset(x: CGFloat(clip.inPoint / d) * w)
                RoundedRectangle(cornerRadius: 3)
                    .fill(Color.white.opacity(0.12))
                    .overlay(RoundedRectangle(cornerRadius: 3).stroke(Color.white.opacity(0.85), lineWidth: 1.5))
                    .frame(width: max(6, CGFloat(zoom.vl / d) * w), height: h)
                    .offset(x: CGFloat(zoom.vs / d) * w)
                ClockMarker(clock: clock, height: h) { CGFloat($0 / d) * w }
            }
            .background(Color.primary.opacity(0.08))
            .clipShape(RoundedRectangle(cornerRadius: 3))
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { v in
                        zoom.setWindow(Double(v.location.x / w) * d - zoom.vl / 2, zoom.vl)
                    }
            )
        }
        .frame(height: 16)
        .help("Toàn bộ video · khung trắng là vùng đang xem — kéo để di chuyển")
    }
}

/// Time ruler for the visible window.
struct TimeRuler: View {
    @ObservedObject var zoom: TimelineZoom
    let total: Double

    var body: some View {
        Canvas { ctx, size in
            guard size.width > 0, zoom.vl > 0 else { return }
            let step = Self.niceStep(zoom.vl * 90 / Double(size.width))
            let minor = step / 5
            var t = (zoom.vs / minor).rounded(.down) * minor
            let end = zoom.vs + zoom.vl
            while t <= end + 1e-9 {
                let x = CGFloat((t - zoom.vs) / zoom.vl) * size.width
                let major = abs((t / step).rounded() * step - t) < minor / 10
                var p = Path()
                p.move(to: CGPoint(x: x, y: size.height))
                p.addLine(to: CGPoint(x: x, y: size.height - (major ? 7 : 3)))
                ctx.stroke(p, with: .color(.secondary.opacity(major ? 0.8 : 0.4)), lineWidth: 1)
                if major && x >= 0 {
                    ctx.draw(Text(Self.label(t, step: step, total: total))
                                .font(.system(size: 9).monospacedDigit())
                                .foregroundColor(.secondary),
                             at: CGPoint(x: x + 3, y: 0), anchor: .topLeading)
                }
                t += minor
            }
        }
    }

    static func niceStep(_ raw: Double) -> Double {
        let steps: [Double] = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600]
        return steps.first { $0 >= raw } ?? 7200
    }

    static func label(_ t: Double, step: Double, total: Double) -> String {
        let s = max(0, t)
        let h = Int(s) / 3600, m = (Int(s) % 3600) / 60, sec = Int(s) % 60
        var out = total >= 3600 ? String(format: "%d:%02d:%02d", h, m, sec) : String(format: "%d:%02d", m, sec)
        if step < 1 { out += "," + String(Int(((s - s.rounded(.down)) * 10).rounded()) % 10) }
        return out
    }
}

/// Scene cut lines for a time window.
struct SceneMarks: View {
    let cuts: [Double]
    let start: Double
    let length: Double
    let prominent: Bool

    var body: some View {
        Canvas { ctx, size in
            guard length > 0 else { return }
            var lastX = -10.0
            for t in cuts where t >= start && t <= start + length {
                let x = (t - start) / length * Double(size.width)
                if !prominent && x - lastX < 1.5 { continue }       // avoid smearing when dense
                lastX = x
                var p = Path()
                p.move(to: CGPoint(x: x, y: 0))
                p.addLine(to: CGPoint(x: x, y: size.height))
                if prominent {
                    ctx.stroke(p, with: .color(.black.opacity(0.5)), lineWidth: 3)
                    ctx.stroke(p, with: .color(.white.opacity(0.95)), lineWidth: 1.5)
                    var tri = Path()
                    tri.move(to: CGPoint(x: x - 4, y: 0))
                    tri.addLine(to: CGPoint(x: x + 4, y: 0))
                    tri.addLine(to: CGPoint(x: x, y: 5))
                    tri.closeSubpath()
                    ctx.fill(tri, with: .color(.white))
                } else {
                    ctx.stroke(p, with: .color(.white.opacity(0.7)), lineWidth: 1)
                }
            }
        }
    }
}
