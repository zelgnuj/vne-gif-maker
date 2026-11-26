import AVFoundation
import AppKit
import CoreImage
import SwiftUI

/// Playback time lives in its own object so the 30 Hz updates only redraw the playhead,
/// not the whole window.
final class PlaybackClock: ObservableObject {
    @Published var time: Double = 0
}

/// Pulls decoded frames from the player and renders the edited output frame with Core Image.
/// Edits redraw instantly from the last frame, even while paused.
/// Debug timing log, enabled with the VNE_TRACE environment variable.
let traceOn = ProcessInfo.processInfo.environment["VNE_TRACE"] != nil
func trace(_ s: @autoclosure () -> String) {
    guard traceOn else { return }
    FileHandle.standardError.write(String(format: "%.3f %@\n", CACurrentMediaTime(), s()).data(using: .utf8)!)
}

@MainActor
final class PreviewEngine {
    private var lastFrameAt = CACurrentMediaTime()
    enum Issue: Equatable {
        case none
        case failed(String)   // AVPlayerItem failed
        case noFrames         // item is ready but never produced a frame (unsupported codec…)
    }

    var onIssue: ((Issue) -> Void)?
    private(set) var issue: Issue = .none {
        didSet { if issue != oldValue { onIssue?(issue) } }
    }

    weak var targetLayer: CALayer?
    var pixelSize: CGSize = .zero { didSet { if pixelSize != oldValue { dirty = true } } }

    private var output: AVPlayerItemVideoOutput?
    private weak var item: AVPlayerItem?
    private var orientation: CGImagePropertyOrientation = .up
    private var source: CIImage?
    private var gotFrame = false
    private var edit = ClipEdit()
    private var outWidth = 680.0
    private var dirty = true
    private var attachedAt = Date()
    private var statusObs: NSKeyValueObservation?
    private let context = CIContext(options: [.cacheIntermediates: false, .name: "vne-preview"])

    /// Adds a frame output to an item (done ahead of time for preloaded items).
    static func makeOutput(for item: AVPlayerItem) -> AVPlayerItemVideoOutput {
        let out = AVPlayerItemVideoOutput(pixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
        ])
        item.add(out)
        return out
    }

    /// Frames earlier than this item time are skipped (lead-in of a clip started early).
    private var minTime = -Double.infinity
    /// Output of the preloaded next clip. It is polled every refresh so it never goes idle
    /// (an unpolled AVPlayerItemVideoOutput takes ~200 ms to wake up), and its latest frame
    /// is shown the instant it becomes the main output.
    private var standby: AVPlayerItemVideoOutput?
    private var standbyFrame: (buffer: CVPixelBuffer, time: Double)?

    func setStandby(_ o: AVPlayerItemVideoOutput?) {
        standby = o
        standbyFrame = nil
    }

    func attach(_ item: AVPlayerItem, transform: CGAffineTransform, output existing: AVPlayerItemVideoOutput? = nil,
                minTime: Double = -.infinity) {
        output = existing ?? Self.makeOutput(for: item)
        self.minTime = minTime
        var ready: CVPixelBuffer?
        if let existing, existing === standby {
            if let f = standbyFrame, f.time >= minTime { ready = f.buffer }
            standby = nil
            standbyFrame = nil
        }
        self.item = item
        orientation = PreviewRenderer.orientation(for: transform)
        trace("attach output, standby frame ready: \(ready != nil)")
        gotFrame = false        // keep showing the previous frame until the new item delivers one
        if let ready {
            source = CIImage(cvPixelBuffer: ready).oriented(orientation)
            gotFrame = true
            lastFrameAt = CACurrentMediaTime()
        }
        attachedAt = Date()
        issue = .none
        dirty = true
        statusObs = item.observe(\.status, options: [.new]) { [weak self] it, _ in
            let failed = it.status == .failed
            let msg = it.error?.localizedDescription ?? "Lỗi không rõ"
            DispatchQueue.main.async {
                MainActor.assumeIsolated { if failed { self?.issue = .failed(msg) } }
            }
        }
    }

    func detach() {
        output = nil
        item = nil
        source = nil
        statusObs = nil
        issue = .none
        dirty = true
    }

    func update(edit: ClipEdit, outWidth: Double) {
        guard edit != self.edit || outWidth != self.outWidth else { return }
        self.edit = edit
        self.outWidth = outWidth
        dirty = true
    }

    func markDirty() { dirty = true }

    /// Called every display refresh.
    func tick() {
        if let sb = standby {
            let st = sb.itemTime(forHostTime: CACurrentMediaTime())
            var shown = CMTime.invalid
            if sb.hasNewPixelBuffer(forItemTime: st),
               let pb = sb.copyPixelBuffer(forItemTime: st, itemTimeForDisplay: &shown) {
                standbyFrame = (pb, shown.isValid ? shown.seconds : st.seconds)
            }
        }
        if let output, let item {
            let t = output.itemTime(forHostTime: CACurrentMediaTime())
            var shown = CMTime.invalid
            if output.hasNewPixelBuffer(forItemTime: t),
               let pb = output.copyPixelBuffer(forItemTime: t, itemTimeForDisplay: &shown),
               !(shown.isValid && shown.seconds < minTime) {
                let now = CACurrentMediaTime()
                if traceOn {
                    if !gotFrame { trace("first frame after attach: \(Int((Date().timeIntervalSince(attachedAt)) * 1000)) ms") }
                    else if now - lastFrameAt > 0.08 { trace("frame gap \(Int((now - lastFrameAt) * 1000)) ms at item t=\(t.seconds)") }
                }
                lastFrameAt = now
                gotFrame = true
                source = CIImage(cvPixelBuffer: pb).oriented(orientation)
                dirty = true
                if issue == .noFrames { issue = .none }
            } else if !gotFrame, issue == .none, item.status == .readyToPlay,
                      Date().timeIntervalSince(attachedAt) > 3 {
                issue = .noFrames
            }
        }
        if dirty { render() }
    }

    private func render() {
        dirty = false
        guard let layer = targetLayer else { return }
        guard let src = source, pixelSize.width >= 2, pixelSize.height >= 2 else {
            layer.contents = nil
            return
        }
        let img = PreviewRenderer.render(src, edit: edit, size: pixelSize, outWidth: outWidth)
        layer.contents = context.createCGImage(img, from: CGRect(origin: .zero, size: pixelSize))
    }
}

/// Layer-backed view that drives the engine from a display link.
struct LivePreviewView: NSViewRepresentable {
    let engine: PreviewEngine

    func makeNSView(context: Context) -> PreviewNSView { PreviewNSView(engine: engine) }
    func updateNSView(_ v: PreviewNSView, context: Context) { v.engine = engine }

    final class PreviewNSView: NSView {
        var engine: PreviewEngine
        private var link: CADisplayLink?

        init(engine: PreviewEngine) {
            self.engine = engine
            super.init(frame: .zero)
            wantsLayer = true
            layer?.contentsGravity = .resize
            layer?.backgroundColor = NSColor.black.cgColor
        }
        required init?(coder: NSCoder) { fatalError() }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            link?.invalidate()
            link = nil
            guard window != nil else { return }
            MainActor.assumeIsolated { engine.targetLayer = layer }
            let l = displayLink(target: self, selector: #selector(step))
            l.add(to: .main, forMode: .common)
            link = l
            updatePixelSize()
        }

        override func layout() {
            super.layout()
            updatePixelSize()
        }

        override func viewDidChangeBackingProperties() {
            super.viewDidChangeBackingProperties()
            updatePixelSize()
        }

        private func updatePixelSize() {
            let scale = window?.backingScaleFactor ?? 2
            var w = bounds.width * scale, h = bounds.height * scale
            let cap = 1600.0
            if w > cap { h = h * cap / w; w = cap }
            MainActor.assumeIsolated {
                engine.pixelSize = CGSize(width: w.rounded(), height: h.rounded())
            }
        }

        @objc private func step() {
            MainActor.assumeIsolated { engine.tick() }
        }

        deinit { link?.invalidate() }
    }
}

/// The playhead line, observing only the clock.
struct PlayheadView: View {
    @ObservedObject var clock: PlaybackClock
    let duration: Double
    let width: CGFloat
    let height: CGFloat

    var body: some View {
        Rectangle().fill(.white).frame(width: 2, height: height + 8)
            .shadow(radius: 1)
            .offset(x: clock.time / max(duration, 0.001) * width - 1, y: -4)
            .allowsHitTesting(false)
    }
}
