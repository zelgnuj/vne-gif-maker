import AppKit
import SwiftUI

// MARK: - Number formatting (Vietnamese: decimal comma)

enum Num {
    private static var cache: [Int: NumberFormatter] = [:]
    static func fmt(_ v: Double, _ decimals: Int, fixed: Bool = false) -> String {
        let key = decimals * 2 + (fixed ? 1 : 0)
        let f: NumberFormatter
        if let c = cache[key] { f = c } else {
            f = NumberFormatter()
            f.locale = Locale(identifier: "vi_VN")
            f.numberStyle = .decimal
            f.usesGroupingSeparator = false
            f.minimumFractionDigits = fixed ? decimals : 0
            f.maximumFractionDigits = decimals
            cache[key] = f
        }
        return f.string(from: NSNumber(value: v)) ?? "\(v)"
    }

    /// Parses "1,5", "1.5", "45%", "-0,2×" …
    static func parse(_ s: String) -> Double? {
        let cleaned = s.replacingOccurrences(of: ",", with: ".")
            .filter { "0123456789.-+".contains($0) }
        return Double(cleaned)
    }

    /// 00:03,25 — or 1:02:29,40 from one hour up.
    static func time(_ t: Double) -> String {
        let cs = Int((max(0, t) * 100).rounded(.down))      // centiseconds
        let h = cs / 360_000, m = (cs / 6000) % 60, s = (cs / 100) % 60, c = cs % 100
        return h > 0 ? String(format: "%d:%02d:%02d,%02d", h, m, s, c)
                     : String(format: "%02d:%02d,%02d", m, s, c)
    }

    /// Accepts "1:02:29,4", "2:29.4", "149,4" or "149.4 s" (seconds).
    static func parseTime(_ str: String) -> Double? {
        let parts = str.replacingOccurrences(of: ",", with: ".")
            .filter { "0123456789.:".contains($0) }
            .split(separator: ":", omittingEmptySubsequences: false)
        guard !parts.isEmpty, parts.count <= 3 else { return nil }
        var total = 0.0
        for p in parts {
            guard let v = Double(p.isEmpty ? "0" : p) else { return nil }
            total = total * 60 + v
        }
        return total
    }
}

// MARK: - Scrub field (Adobe-style "hot text")

/// Drag horizontally to change the value (⌥ = fine, ⇧ = coarse). Click to type;
/// Return commits, Esc cancels, ↑/↓ step. External updates never overwrite text being typed.
struct ScrubField: NSViewRepresentable {
    @Binding var value: Double
    var range: ClosedRange<Double>
    var step: Double                 // keyboard step, in value units
    var scale: Double = 1            // display = value × scale
    var decimals: Int = 0
    var unit: String = ""
    var highlighted = false
    var fixedDecimals = false
    var timecode = false

    func makeNSView(context: Context) -> ScrubTextField { ScrubTextField() }

    func updateNSView(_ f: ScrubTextField, context: Context) {
        f.range = range
        f.step = step
        f.scale = scale
        f.decimals = decimals
        f.unit = unit
        f.accent = highlighted
        f.fixedDecimals = fixedDecimals
        f.timecode = timecode
        f.onChange = { v in value = v }
        if !f.editing {
            f.value = value
            f.refresh()
        }
    }
}

final class ScrubTextField: NSTextField, NSTextFieldDelegate {
    var value = 0.0
    var range: ClosedRange<Double> = 0...1
    var step = 0.01
    var scale = 1.0
    var decimals = 0
    var unit = ""
    var accent = false { didSet { if accent != oldValue { refresh() } } }
    var fixedDecimals = false
    var timecode = false
    var onChange: ((Double) -> Void)?
    private(set) var editing = false

    override class var cellClass: AnyClass? {
        get { VCenterCell.self }
        set {}
    }

    private var downX: CGFloat = 0
    private var lastX: CGFloat = 0
    private var dragged = false

    init() {
        super.init(frame: .zero)
        isEditable = false
        isSelectable = false
        isBordered = false
        isBezeled = false
        drawsBackground = true
        backgroundColor = NSColor.quaternaryLabelColor.withAlphaComponent(0.12)
        alignment = .right
        font = .monospacedDigitSystemFont(ofSize: NSFont.smallSystemFontSize, weight: .medium)
        focusRingType = .none
        lineBreakMode = .byClipping
        usesSingleLineMode = true
        delegate = self
        wantsLayer = true
        layer?.cornerRadius = 4
        toolTip = "Kéo ngang để chỉnh · giữ ⌥ để chỉnh tinh, ⇧ để chỉnh nhanh · nhấp để nhập số"
    }
    required init?(coder: NSCoder) { fatalError() }

    override var intrinsicContentSize: NSSize { NSSize(width: NSView.noIntrinsicMetric, height: 20) }

    private func text(_ v: Double) -> String {
        timecode ? Num.time(v) : Num.fmt(v * scale, decimals, fixed: fixedDecimals)
    }
    private func parse(_ str: String) -> Double? {
        timecode ? Num.parseTime(str) : Num.parse(str).map { $0 / scale }
    }

    func refresh() {
        guard !editing else { return }
        stringValue = text(value) + (timecode ? "" : unit)
        textColor = accent ? Brand.nsBright : .labelColor
    }

    override func resetCursorRects() {
        if !editing && isEnabled { addCursorRect(bounds, cursor: .resizeLeftRight) }
    }

    override func mouseDown(with e: NSEvent) {
        guard isEnabled else { return }
        if editing { super.mouseDown(with: e); return }
        downX = e.locationInWindow.x
        lastX = downX
        dragged = false
    }

    override func mouseDragged(with e: NSEvent) {
        guard isEnabled else { return }
        if editing { super.mouseDragged(with: e); return }
        let x = e.locationInWindow.x
        if !dragged && abs(x - downX) < 3 { return }
        dragged = true
        let dx = Double(x - lastX)
        lastX = x
        var factor = 0.5                                  // half a step per pixel
        if e.modifierFlags.contains(.option) { factor = 0.05 }
        else if e.modifierFlags.contains(.shift) { factor = 5 }
        set(value + dx * step * factor)
    }

    override func mouseUp(with e: NSEvent) {
        guard isEnabled else { return }
        if editing { super.mouseUp(with: e); return }
        if !dragged { beginEditing() }
    }

    private func set(_ v: Double) {
        let c = min(max(v, range.lowerBound), range.upperBound)
        value = c
        if editing { stringValue = text(c) } else { refresh() }
        onChange?(c)
    }

    private func beginEditing() {
        editing = true
        isEditable = true
        isSelectable = true
        textColor = .labelColor
        stringValue = text(value)
        window?.makeFirstResponder(self)
        currentEditor()?.selectAll(nil)
        window?.invalidateCursorRects(for: self)
    }

    private func finishEditing(commit: Bool) {
        guard editing else { return }
        if commit, let v = parse(stringValue) { set(v) }
        editing = false
        isEditable = false
        isSelectable = false
        refresh()
        window?.invalidateCursorRects(for: self)
    }

    func controlTextDidEndEditing(_ obj: Notification) { finishEditing(commit: true) }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy sel: Selector) -> Bool {
        switch sel {
        case #selector(NSResponder.cancelOperation(_:)):
            finishEditing(commit: false)
            window?.makeFirstResponder(nil)
            return true
        case #selector(NSResponder.insertNewline(_:)):
            window?.makeFirstResponder(nil)    // ends editing → commits
            return true
        case #selector(NSResponder.moveUp(_:)), #selector(NSResponder.moveDown(_:)):
            let cur = parse(textView.string) ?? value
            let mult = NSEvent.modifierFlags.contains(.shift) ? 10.0 : 1.0
            set(cur + (sel == #selector(NSResponder.moveUp(_:)) ? step : -step) * mult)
            textView.string = text(value)
            textView.selectAll(nil)
            return true
        default:
            return false
        }
    }
}

/// Text cell that centers its line vertically and keeps a small horizontal inset.
final class VCenterCell: NSTextFieldCell {
    private func adjusted(_ rect: NSRect) -> NSRect {
        var r = rect.insetBy(dx: 5, dy: 0)
        let h = cellSize(forBounds: rect).height
        if h < r.height {
            r.origin.y += ((r.height - h) / 2).rounded(.down)
            r.size.height = h
        }
        return r
    }
    override func drawingRect(forBounds rect: NSRect) -> NSRect { adjusted(super.drawingRect(forBounds: rect)) }
    override func edit(withFrame rect: NSRect, in controlView: NSView, editor textObj: NSText,
                       delegate: Any?, event: NSEvent?) {
        super.edit(withFrame: adjusted(rect), in: controlView, editor: textObj, delegate: delegate, event: event)
    }
    override func select(withFrame rect: NSRect, in controlView: NSView, editor textObj: NSText,
                         delegate: Any?, start selStart: Int, length selLength: Int) {
        super.select(withFrame: adjusted(rect), in: controlView, editor: textObj, delegate: delegate,
                     start: selStart, length: selLength)
    }
}

// MARK: - Slider with fine control

final class SliderDrag: ObservableObject { var lastX: CGFloat? }

/// Slim slider. Click jumps, drag is relative; hold ⌥ while dragging for 10× finer control.
/// The filled part starts at `origin` (e.g. 0 for bipolar values).
struct FineSlider: View {
    @Binding var value: Double
    let range: ClosedRange<Double>
    var origin: Double? = nil
    @Environment(\.isEnabled) private var enabled
    @StateObject private var drag = SliderDrag()

    var body: some View {
        GeometryReader { g in
            let w = max(g.size.width - 14, 1)
            let span = range.upperBound - range.lowerBound
            let f = (min(max(value, range.lowerBound), range.upperBound) - range.lowerBound) / span
            let o = ((origin ?? range.lowerBound) - range.lowerBound) / span
            ZStack(alignment: .leading) {
                Capsule().fill(Color.primary.opacity(0.12)).frame(height: 4)
                    .padding(.horizontal, 7)
                Capsule().fill(enabled ? Brand.bright : Color.secondary.opacity(0.4))
                    .frame(width: max(0, abs(f - o) * w), height: 4)
                    .offset(x: 7 + min(f, o) * w)
                Circle()
                    .fill(Color(nsColor: .controlColor))
                    .overlay(Circle().stroke(Color.primary.opacity(0.25), lineWidth: 0.5))
                    .shadow(color: .black.opacity(0.25), radius: 1, y: 0.5)
                    .frame(width: 14, height: 14)
                    .offset(x: f * w)
            }
            .frame(height: g.size.height)
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { v in
                        let x = v.location.x
                        if drag.lastX == nil {
                            let knobX = 7 + f * w
                            if abs(x - knobX) > 9 {       // clicked the track: jump there
                                value = range.lowerBound + min(max((x - 7) / w, 0), 1) * span
                            }
                            drag.lastX = x
                            return
                        }
                        let fine = NSEvent.modifierFlags.contains(.option)
                        let dx = Double(x - (drag.lastX ?? x))
                        drag.lastX = x
                        let nv = value + dx / w * span * (fine ? 0.1 : 1)
                        value = min(max(nv, range.lowerBound), range.upperBound)
                    }
                    .onEnded { _ in drag.lastX = nil }
            )
        }
        .frame(height: 18)
        .opacity(enabled ? 1 : 0.5)
        .help("Kéo để chỉnh · giữ ⌥ để chỉnh tinh")
    }
}

// MARK: - Parameter row

/// Label · slider · value. Double-click the label to reset.
struct ParamRow: View {
    let label: String
    @Binding var value: Double
    let range: ClosedRange<Double>
    var reset: Double
    var unit: String = ""
    var scale: Double = 1
    var decimals: Int = 0
    var step: Double = 0.01
    var bipolar = false

    var body: some View {
        let modified = abs(value - reset) > 1e-9
        HStack(spacing: 10) {
            Text(label)
                .font(.callout)
                .foregroundStyle(modified ? Color.primary : Color.secondary)
                .lineLimit(1)
                .frame(width: 88, alignment: .leading)
                .contentShape(Rectangle())
                .onTapGesture(count: 2) { value = reset }
                .help("Nhấp đúp để đặt lại")
            FineSlider(value: $value, range: range, origin: bipolar ? reset : nil)
            ScrubField(value: $value, range: range, step: step, scale: scale,
                       decimals: decimals, unit: unit, highlighted: modified)
                .frame(width: 60, height: 20)
        }
    }
}

// MARK: - Inspector section

final class SectionState: ObservableObject {
    @Published var collapsed: Set<String> = []
    func toggle(_ id: String) {
        if collapsed.contains(id) { collapsed.remove(id) } else { collapsed.insert(id) }
    }
}

struct InspectorSection<Content: View, Accessory: View>: View {
    let title: String
    @ObservedObject var sections: SectionState
    @ViewBuilder var accessory: () -> Accessory
    @ViewBuilder var content: () -> Content

    var body: some View {
        let open = !sections.collapsed.contains(title)
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 6) {
                Button {
                    withAnimation(.easeInOut(duration: 0.15)) { sections.toggle(title) }
                } label: {
                    HStack(spacing: 6) {
                        Image(systemName: "chevron.right")
                            .font(.system(size: 9, weight: .bold))
                            .rotationEffect(.degrees(open ? 90 : 0))
                            .foregroundStyle(.secondary)
                        Text(title).font(.subheadline.weight(.semibold))
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                Spacer()
                accessory()
            }
            if open {
                VStack(alignment: .leading, spacing: 9) { content() }
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
    }
}

extension InspectorSection where Accessory == EmptyView {
    init(title: String, sections: SectionState, @ViewBuilder content: @escaping () -> Content) {
        self.title = title
        self.sections = sections
        self.accessory = { EmptyView() }
        self.content = content
    }
}

/// Small row label used for non-slider controls inside sections.
struct RowLabel: View {
    let text: String
    var body: some View {
        Text(text).font(.callout).foregroundStyle(.secondary).frame(width: 88, alignment: .leading)
    }
}
