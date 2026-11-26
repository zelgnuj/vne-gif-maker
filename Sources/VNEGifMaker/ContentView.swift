import AVFoundation
import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// Small holders instead of @State (the CommandLineTools SDK lacks the SwiftUI macro plugin).
final class UIFlag: ObservableObject { @Published var on = false }

struct ContentView: View {
    @EnvironmentObject var state: AppState
    @StateObject private var dropping = UIFlag()

    var body: some View {
        Group {
            if state.clips.isEmpty {
                DropZone()
            } else {
                HStack(spacing: 0) {
                    VStack(spacing: 0) {
                        PreviewArea()
                            .padding([.horizontal, .top], 12)
                        TransportBar()
                            .padding(.horizontal, 12)
                            .padding(.vertical, 8)
                        TrimTimeline(zoom: state.timeline)
                            .padding(.horizontal, 12)
                        Divider().padding(.top, 12)
                        ClipStrip()
                            .padding(12)
                    }
                    Divider()
                    Inspector().frame(width: 340)
                }
            }
        }
        .navigationTitle("GIF Maker")
        .navigationSubtitle(state.summary)
        .toolbar {
            ToolbarItem(placement: .navigation) {
                BrandMarkView()
                    .frame(width: 20, height: 20)
                    .padding(.horizontal, 4)
                    .help(Brand.name)
            }
            ToolbarItem(placement: .navigation) {
                Button { state.openPanel() } label: {
                    Label("Thêm video", systemImage: "plus")
                }
                .help("Thêm video (⌘O)")
            }
            ToolbarItem(placement: .primaryAction) {
                AspectToolbarMenu()
            }
            ToolbarItem(placement: .primaryAction) {
                ExportToolbarButton()
                .keyboardShortcut(.return, modifiers: .command)
                .help("Xuất GIF (⌘↩)")
            }
        }
        .dropDestination(for: URL.self) { urls, _ in
            guard !urls.isEmpty else { return false }
            state.add(urls)
            return true
        } isTargeted: { dropping.on = $0 }
        .overlay {
            if dropping.on {
                RoundedRectangle(cornerRadius: 12)
                    .stroke(Brand.bright, lineWidth: 3)
                    .padding(6)
                    .allowsHitTesting(false)
            }
        }
        .overlay(alignment: .top) {
            if let msg = state.preparing {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text(msg).font(.callout)
                }
                .padding(.horizontal, 14).padding(.vertical, 8)
                .background(.regularMaterial, in: Capsule())
                .padding(.top, 12)
            }
        }
        .alert("Không mở được tệp", isPresented: Binding(get: { state.loadError != nil },
                                                        set: { if !$0 { state.loadError = nil } })) {
            Button("OK") { state.loadError = nil }
        } message: { Text(state.loadError ?? "") }
        .tint(Brand.red)
        .onAppear { state.installKeyMonitor() }
    }
}

/// Output aspect ratio, as a compact menu in the toolbar.
struct AspectToolbarMenu: View {
    @EnvironmentObject var state: AppState
    @StateObject private var custom = UIFlag()

    var body: some View {
        Menu {
            Section("Ngang") { items(Aspect.landscape) }
            Section("Vuông & dọc") { items(Aspect.squareAndPortrait) }
            Divider()
            item(.original, "Gốc (theo đoạn đầu)")
            Button {
                state.aspect = .custom
                custom.on = true
            } label: {
                if state.aspect == .custom { Label("Tuỳ chỉnh…", systemImage: "checkmark") } else { Text("Tuỳ chỉnh…") }
            }
        } label: {
            Label("Tỉ lệ \(state.aspectLabel)", systemImage: "aspectratio")
                .labelStyle(.titleAndIcon)
        }
        .help("Tỉ lệ khung hình GIF (áp dụng cho mọi đoạn)")
        .popover(isPresented: $custom.on, arrowEdge: .bottom) {
            HStack(spacing: 6) {
                Text("Tỉ lệ riêng").foregroundStyle(.secondary)
                ScrubField(value: $state.customW, range: 0.1...100, step: 1, decimals: 2)
                    .frame(width: 60, height: 20)
                Text(":").foregroundStyle(.secondary)
                ScrubField(value: $state.customH, range: 0.1...100, step: 1, decimals: 2)
                    .frame(width: 60, height: 20)
                Button {
                    (state.customW, state.customH) = (state.customH, state.customW)
                } label: { Image(systemName: "arrow.left.arrow.right") }
                    .buttonStyle(.borderless)
                    .help("Đổi chiều ngang ↔ dọc")
            }
            .padding(12)
        }
    }

    @ViewBuilder private func items(_ list: [Aspect]) -> some View {
        ForEach(list) { item($0, $0.label) }
    }

    private func item(_ a: Aspect, _ title: String) -> some View {
        Button { state.aspect = a } label: {
            if state.aspect == a { Label(title, systemImage: "checkmark") } else { Text(title) }
        }
    }
}

/// The one export action: a brand-red pill in the toolbar.
struct ExportToolbarButton: View {
    @EnvironmentObject var state: AppState
    @StateObject private var hover = UIFlag()

    var body: some View {
        let disabled = state.clips.isEmpty || state.isEncoding || GifEncoder.ffmpegPath == nil
        Button { state.export() } label: {
            HStack(spacing: 6) {
                if state.isEncoding {
                    ProgressView().controlSize(.small).tint(.white)
                } else {
                    Image(systemName: "square.and.arrow.up")
                }
                Text(state.isEncoding ? "Đang xuất…" : "Xuất GIF…").fontWeight(.semibold)
            }
            .font(.system(size: 12.5))
            .foregroundStyle(.white.opacity(disabled && !state.isEncoding ? 0.55 : 1))
            .padding(.horizontal, 12)
            .frame(height: 26)
            .background(
                RoundedRectangle(cornerRadius: 7)
                    .fill(disabled && !state.isEncoding ? Brand.red.opacity(0.45)
                          : hover.on ? Brand.bright.opacity(0.9) : Brand.red)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(disabled)
        .onHover { hover.on = $0 }
    }
}

// MARK: - Empty state

struct DropZone: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        VStack(spacing: 0) {
            Spacer()
            VStack(spacing: 14) {
                BrandMarkView()
                    .frame(width: 64, height: 64)
                    .shadow(color: Brand.red.opacity(0.35), radius: 14, y: 4)
                Text("GIF Maker")
                    .font(.system(size: 30, weight: .bold))
                    .tracking(-0.5)
                Text("Ghép một hoặc nhiều đoạn video, ngang hoặc dọc,\nthành ảnh GIF ngang dung lượng dưới \(Num.fmt(state.limitMB, 1)) MB.")
                    .multilineTextAlignment(.center)
                    .foregroundStyle(.secondary)
            }
            VStack(spacing: 10) {
                Image(systemName: "arrow.down.doc")
                    .font(.system(size: 26, weight: .light))
                    .foregroundStyle(Brand.bright)
                Text("Thả video vào đây")
                    .font(.headline)
                Button {
                    state.openPanel()
                } label: {
                    Text("Chọn video…").padding(.horizontal, 10)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .keyboardShortcut("o")
                Text("MP4, MOV, M4V · H.264, HEVC, ProRes, AV1")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }
            .padding(.vertical, 28)
            .frame(width: 440)
            .background(
                RoundedRectangle(cornerRadius: 16)
                    .strokeBorder(Brand.bright.opacity(0.45), style: StrokeStyle(lineWidth: 1.5, dash: [7, 6]))
                    .background(RoundedRectangle(cornerRadius: 16).fill(Brand.red.opacity(0.06)))
            )
            .padding(.top, 30)
            if let saved = state.savedSession {
                SessionOffer(saved: saved)
                    .frame(width: 440)
                    .padding(.top, 14)
            }
            if GifEncoder.ffmpegPath == nil {
                Label("Không tìm thấy bộ mã hoá ffmpeg. Vui lòng cài lại ứng dụng.",
                      systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.orange)
                    .padding(.top, 14)
            }
            Spacer()
            Text(Brand.credit)
                .font(.caption2)
                .foregroundStyle(.tertiary)
                .padding(.bottom, 12)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(
            RadialGradient(colors: [Brand.red.opacity(0.16), .clear], center: .top, startRadius: 0, endRadius: 520)
        )
    }
}

/// "Continue where you left off" card on the start screen.
struct SessionOffer: View {
    @EnvironmentObject var state: AppState
    let saved: SessionStore.Saved

    var body: some View {
        let names = Array(Set(saved.clips.map { URL(fileURLWithPath: $0.path).lastPathComponent })).sorted()
        HStack(spacing: 12) {
            Image(systemName: "clock.arrow.circlepath")
                .font(.system(size: 20))
                .foregroundStyle(Brand.bright)
            VStack(alignment: .leading, spacing: 2) {
                Text("Phiên làm việc trước · \(saved.clips.count) đoạn").font(.callout.weight(.semibold))
                Text(names.joined(separator: ", "))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            Spacer(minLength: 8)
            Button("Bỏ qua") { state.savedSession = nil }
            Button("Mở lại") { state.restoreSession() }
                .buttonStyle(.borderedProminent)
                .tint(Brand.red)
                .keyboardShortcut(.defaultAction)
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 12).fill(Color.primary.opacity(0.06)))
    }
}

// MARK: - Preview

final class FramingDrag: ObservableObject {
    var start: (x: Double, y: Double)?
    var zoomStart: Double?
}

/// The composed output frame, exactly as it will be exported.
/// Drag = move, pinch = zoom, double-click = reset framing.
struct PreviewArea: View {
    @EnvironmentObject var state: AppState
    @StateObject private var g = FramingDrag()

    var body: some View {
        GeometryReader { geo in
            let bounds = CGRect(origin: .zero, size: geo.size).insetBy(dx: 10, dy: 10)
            let out = AVMakeRect(aspectRatio: CGSize(width: state.outputAspect, height: 1), insideRect: bounds)
            if let clip = state.selected {
                LivePreviewView(engine: state.preview)
                    .frame(width: out.width, height: out.height)
                    .overlay { PreviewIssueView(issue: state.previewIssue, clip: clip) }
                    .overlay(Rectangle().stroke(Color.white.opacity(0.18), lineWidth: 1))
                    .contentShape(Rectangle())
                    .gesture(
                        DragGesture(minimumDistance: 1)
                            .onChanged { v in pan(v.translation, clip: clip, frame: out.size) }
                            .onEnded { _ in g.start = nil }
                    )
                    .simultaneousGesture(
                        MagnifyGesture()
                            .onChanged { v in
                                if g.zoomStart == nil { g.zoomStart = clip.edit.zoom }
                                let z = min(4, max(1, (g.zoomStart ?? 1) * v.magnification))
                                state.mutateEdit { $0.zoom = z }
                            }
                            .onEnded { _ in g.zoomStart = nil }
                    )
                    .onTapGesture(count: 2) { state.mutateEdit { $0.resetFraming() } }
                    .help("Kéo để di chuyển · chụm hai ngón để thu phóng · nhấp đúp để căn giữa")
                    .position(x: out.midX, y: out.midY)
            }
        }
        .background(Color(white: 0.07))
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }

    private func pan(_ t: CGSize, clip: Clip, frame: CGSize) {
        guard state.previewIssue == .none else { return }
        if g.start == nil { g.start = (clip.edit.panX, clip.edit.panY) }
        let L = EditMath.layout(clip.edit, src: clip.size, W: frame.width, H: frame.height)
        let freeX = abs(frame.width - L.dw) / 2, freeY = abs(frame.height - L.dh) / 2
        let s = g.start ?? (0, 0)
        let fine = NSEvent.modifierFlags.contains(.option) ? 0.1 : 1
        state.mutateEdit { e in
            if freeX > 0.5 { e.panX = min(1, max(-1, s.x + t.width * fine / freeX)) }
            if freeY > 0.5 { e.panY = min(1, max(-1, s.y + t.height * fine / freeY)) }
        }
    }
}

/// Explains why there is no picture and offers the fix.
struct PreviewIssueView: View {
    @EnvironmentObject var state: AppState
    let issue: PreviewEngine.Issue
    let clip: Clip

    var body: some View {
        if issue != .none {
            VStack(spacing: 10) {
                Image(systemName: "eye.slash").font(.system(size: 28))
                Text("Không thể hiển thị video này").font(.headline)
                Text(detail)
                    .font(.callout)
                    .multilineTextAlignment(.center)
                    .foregroundStyle(.white.opacity(0.75))
                    .frame(maxWidth: 440)
                if !clip.isProxy {
                    Button("Tạo bản xem trước") { state.useProxyForSelected() }
                        .buttonStyle(.borderedProminent)
                        .disabled(state.preparing != nil)
                        .padding(.top, 4)
                }
            }
            .padding(20)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Color.black.opacity(0.88))
            .foregroundStyle(.white)
        }
    }

    private var detail: String {
        let file = "“\(clip.url.lastPathComponent)”"
        let fix = "Tạo bản xem trước H.264 để tiếp tục chỉnh sửa. Khi xuất, ứng dụng vẫn dùng tệp gốc."
        switch issue {
        case .failed(let msg):
            return "macOS không phát được tệp \(file) (\(msg)).\n\(fix)"
        case .noFrames:
            return clip.isProxy
                ? "Bản xem trước của \(file) không có hình ảnh. Tệp có thể đã bị hỏng."
                : "macOS không giải mã được định dạng của tệp \(file), ví dụ AV1 trên chip M1/M2.\n\(fix)"
        case .none:
            return ""
        }
    }
}

// MARK: - Transport

struct CurrentTimeLabel: View {
    @ObservedObject var clock: PlaybackClock
    let duration: Double
    var body: some View {
        HStack(spacing: 4) {
            Text(Num.time(clock.time)).foregroundStyle(.primary)
            Text("/ \(Num.time(duration))").foregroundStyle(.tertiary)
        }
        .font(.system(.callout, design: .monospaced))
    }
}

struct TransportBar: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        HStack(spacing: 6) {
            Button { state.jumpToIn() } label: { Image(systemName: "backward.end.fill") }
                .help("Về điểm vào")
            Button { state.togglePlay() } label: {
                Image(systemName: state.isPlaying && !state.playAll ? "pause.fill" : "play.fill")
                    .frame(width: 16)
            }
            .help("Phát / tạm dừng (Space)")
            if let c = state.selected {
                CurrentTimeLabel(clock: state.clock, duration: c.duration)
                    .padding(.leading, 6)
            }
            Spacer()
            if let c = state.selected { SceneControls(clip: c) }
            Divider().frame(height: 16)
            Button { state.markIn() } label: { Label("Điểm vào", systemImage: "arrow.right.to.line") }
                .help("Đặt điểm vào tại vị trí hiện tại (I)")
            Button { state.markOut() } label: { Label("Điểm ra", systemImage: "arrow.left.to.line") }
                .help("Đặt điểm ra tại vị trí hiện tại (O)")
        }
        .controlSize(.small)
    }
}

struct SceneControls: View {
    @EnvironmentObject var state: AppState
    let clip: Clip

    var body: some View {
        HStack(spacing: 6) {
            if let p = clip.sceneProgress {
                ProgressView(value: p).frame(width: 70)
                Text("Đang phân tích cảnh \(Int(p * 100))%")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            } else if clip.scenesAnalyzed {
                Text("\(clip.cuts.count + 1) cảnh")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
            Button { state.jumpScene(-1) } label: { Image(systemName: "chevron.left") }
                .help("Về điểm cắt cảnh trước (↑)")
            Button { state.jumpScene(1) } label: { Image(systemName: "chevron.right") }
                .help("Tới điểm cắt cảnh sau (↓)")
            Button("Chọn cảnh") { state.selectScene() }
                .help("Chọn trọn cảnh tại vị trí hiện tại (X) — hoặc nhấp đúp lên timeline")
            Toggle(isOn: $state.snapToScenes) {
                Image(systemName: "arrow.right.and.line.vertical.and.arrow.left")
            }
            .toggleStyle(BrandToggleStyle())
            .help("Bắt dính vào điểm cắt cảnh khi kéo (S) · giữ ⌘ để tạm tắt")
        }
        .disabled(clip.cuts.isEmpty && clip.sceneProgress == nil && !clip.scenesAnalyzed)
    }
}

// MARK: - Sequence strip

struct ClipStrip: View {
    @EnvironmentObject var state: AppState
    @StateObject private var confirmClear = UIFlag()

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                Text("Trình tự").font(.headline)
                let total = state.totalDuration
                let ok = total >= 2.5 && total <= 6.5
                Text("\(state.clips.count) đoạn · \(Num.fmt(total, 1)) giây")
                    .font(.callout)
                    .foregroundStyle(ok ? Color.secondary : Color.orange)
                    .monospacedDigit()
                if !ok {
                    Image(systemName: "exclamationmark.circle")
                        .foregroundStyle(.orange)
                        .help("GIF thumbnail nên dài 3–6 giây")
                }
                Spacer()
                Button {
                    state.togglePlayAll()
                } label: {
                    Label(state.playAll ? "Dừng" : "Phát toàn bộ",
                          systemImage: state.playAll ? "stop.fill" : "play.rectangle.on.rectangle")
                }
                .disabled(state.clips.count < 2)
                Button {
                    state.addSegmentFromSelected()
                } label: {
                    Label("Lấy thêm đoạn", systemImage: "plus.square.on.square")
                }
                .disabled(state.selected == nil)
                .help("Thêm một đoạn khác từ cùng video đang chọn (⌘D)")
                Button("Xoá tất cả") { confirmClear.on = true }
            }
            .controlSize(.small)
            .confirmationDialog("Xoá tất cả đoạn khỏi trình tự?", isPresented: $confirmClear.on) {
                Button("Xoá tất cả", role: .destructive) { state.removeAll() }
            } message: {
                Text("Các tệp video gốc không bị ảnh hưởng.")
            }

            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 8) {
                    ForEach(Array(state.clips.enumerated()), id: \.element.id) { i, clip in
                        ClipCard(clip: clip, index: i + 1, selected: clip.id == state.selectedID)
                            .onTapGesture { state.select(clip.id) }
                            .onDrag {
                                state.draggingClip = clip.id
                                return NSItemProvider(object: clip.id.uuidString as NSString)
                            }
                            .onDrop(of: [.text, .fileURL], delegate: ReorderDelegate(target: clip.id, state: state))
                            .contextMenu {
                                Button("Lấy thêm đoạn từ video này") {
                                    state.select(clip.id)
                                    state.addSegmentFromSelected()
                                }
                                Divider()
                                Button("Xoá đoạn") { state.remove(clip.id) }
                            }
                    }
                    Button { state.openPanel() } label: {
                        VStack(spacing: 4) {
                            Image(systemName: "plus").font(.title3)
                            Text("Thêm video").font(.caption)
                        }
                        .foregroundStyle(.secondary)
                        .frame(width: 96, height: 72)
                        .background(RoundedRectangle(cornerRadius: 8)
                            .strokeBorder(style: StrokeStyle(lineWidth: 1.2, dash: [5]))
                            .foregroundStyle(.tertiary))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help("Thêm video (⌘O) — hoặc thả tệp vào bất kỳ đâu trong cửa sổ")
                }
                .padding(.vertical, 3)
                .padding(.horizontal, 2)
            }
        }
    }
}

struct ReorderDelegate: DropDelegate {
    let target: UUID
    let state: AppState

    func dropEntered(info: DropInfo) {
        MainActor.assumeIsolated {
            if let id = state.draggingClip { state.move(id, before: target) }
        }
    }
    func dropUpdated(info: DropInfo) -> DropProposal? { DropProposal(operation: .move) }
    func performDrop(info: DropInfo) -> Bool {
        MainActor.assumeIsolated {
            if state.draggingClip != nil {
                state.draggingClip = nil
                return true
            }
            let providers = info.itemProviders(for: [.fileURL])
            for p in providers {
                _ = p.loadObject(ofClass: URL.self) { url, _ in
                    if let url { Task { @MainActor in AppState.shared.add([url]) } }
                }
            }
            return !providers.isEmpty
        }
    }
}

struct ClipCard: View {
    @EnvironmentObject var state: AppState
    let clip: Clip
    let index: Int
    let selected: Bool
    @StateObject private var hover = UIFlag()

    var body: some View {
        ZStack(alignment: .topLeading) {
            Group {
                if let img = clip.cardThumb ?? clip.thumbnails.first {
                    Image(decorative: img, scale: 1).resizable().aspectRatio(contentMode: .fill)
                } else {
                    Color.gray.opacity(0.3)
                }
            }
            .frame(width: 124, height: 72)
            .clipped()
            LinearGradient(colors: [.clear, .black.opacity(0.8)], startPoint: .center, endPoint: .bottom)
            Text("\(index)")
                .font(.caption2.bold())
                .padding(.horizontal, 6).padding(.vertical, 2)
                .background(Capsule().fill(Color.black.opacity(0.6)))
                .padding(5)
            VStack {
                Spacer()
                HStack(spacing: 4) {
                    Text("\(Num.fmt(clip.outLength, 1)) s").bold().monospacedDigit()
                    if clip.edit.speed != 1 { Text("\(Num.fmt(clip.edit.speed, 2))×") }
                    Spacer()
                    if clip.edit.fit { Image(systemName: "square.split.1x2").help("Vừa khung, nền mờ") }
                    if clip.isVertical { Image(systemName: "rectangle.portrait").help("Video dọc") }
                }
                .font(.caption2)
                .padding(6)
            }
            if hover.on {
                HStack {
                    Spacer()
                    Button { state.remove(clip.id) } label: {
                        Image(systemName: "xmark.circle.fill").font(.title3)
                            .symbolRenderingMode(.palette)
                            .foregroundStyle(.white, .black.opacity(0.7))
                    }
                    .buttonStyle(.plain)
                    .help("Xoá đoạn")
                    .padding(4)
                }
            }
        }
        .foregroundStyle(.white)
        .frame(width: 124, height: 72)
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(selected ? Brand.bright : .clear, lineWidth: 2.5))
        .onHover { hover.on = $0 }
        .help(clip.url.lastPathComponent)
    }
}
