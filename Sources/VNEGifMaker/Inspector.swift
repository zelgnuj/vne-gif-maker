import AppKit
import SwiftUI

final class InspectorTab: ObservableObject { @Published var tab = 0 }

struct Inspector: View {
    @EnvironmentObject var state: AppState
    @StateObject private var tab = InspectorTab()
    @StateObject private var sections = SectionState()

    var body: some View {
        VStack(spacing: 0) {
            Picker("", selection: $tab.tab) {
                Text("Chỉnh sửa").tag(0)
                Text("Xuất").tag(1)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .padding(12)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    if tab.tab == 0 {
                        if let clip = state.selected {
                            ClipInspector(clip: clip, sections: sections)
                        } else {
                            Text("Chọn một đoạn trong trình tự để chỉnh sửa.")
                                .foregroundStyle(.secondary)
                                .padding(14)
                        }
                    } else {
                        ExportInspector(sections: sections)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            if state.status != .idle {
                Divider()
                ExportPanel().padding(12)
            }
        }
    }
}

// MARK: - Clip editing

struct ClipInspector: View {
    @EnvironmentObject var state: AppState
    let clip: Clip
    @ObservedObject var sections: SectionState

    var body: some View {
        let (ow, oh) = state.outputSize
        let L = EditMath.layout(clip.edit, src: clip.size, W: Double(ow), H: Double(oh))
        let canX = abs(Double(ow) - L.dw) > 1, canY = abs(Double(oh) - L.dh) > 1

        VStack(alignment: .leading, spacing: 3) {
            HStack {
                Text("Đoạn \((state.selectedIndex ?? 0) + 1)").font(.headline)
                Spacer()
                Button("Đặt lại") { state.resetEdit() }
                    .controlSize(.small)
                    .help("Đặt lại mọi chỉnh sửa của đoạn này")
            }
            Text(clip.url.lastPathComponent)
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.middle)
            Text("\(Int(clip.size.width)) × \(Int(clip.size.height)) px · \(clip.isVertical ? "Dọc" : "Ngang")"
                 + (clip.isProxy ? " · Đang dùng bản xem trước" : ""))
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(14)
        Divider()

        InspectorSection(title: "Bố cục", sections: sections) {
            HStack(spacing: 10) {
                RowLabel(text: "Chế độ")
                Picker("", selection: state.edit(\.fit)) {
                    Text("Lấp đầy").tag(false)
                    Text("Vừa khung").tag(true)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
            }
            if clip.edit.fit {
                Text("Phần trống được lấp bằng nền mờ từ chính video.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .padding(.leading, 98)
            }
            ParamRow(label: "Thu phóng", value: state.edit(\.zoom), range: 1...4, reset: 1,
                     unit: "%", scale: 100, decimals: 0, step: 0.01)
            ParamRow(label: "Vị trí ngang", value: state.edit(\.panX), range: -1...1, reset: 0,
                     unit: "%", scale: 100, decimals: 0, step: 0.01, bipolar: true)
                .disabled(!canX)
            ParamRow(label: "Vị trí dọc", value: state.edit(\.panY), range: -1...1, reset: 0,
                     unit: "%", scale: 100, decimals: 0, step: 0.01, bipolar: true)
                .disabled(!canY)
            HStack(spacing: 10) {
                RowLabel(text: "Căn nhanh")
                if canY {
                    alignButtons(["Trên", "Giữa", "Dưới"]) { v in state.mutateEdit { $0.panY = v } }
                } else if canX {
                    alignButtons(["Trái", "Giữa", "Phải"]) { v in state.mutateEdit { $0.panX = v } }
                } else {
                    Text("Tăng thu phóng để di chuyển khung")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
            }
            HStack(spacing: 10) {
                RowLabel(text: "Xoay & lật")
                HStack(spacing: 4) {
                    Button { state.rotate(-90) } label: { Image(systemName: "rotate.left") }
                        .help("Xoay trái 90°")
                    Button { state.rotate(90) } label: { Image(systemName: "rotate.right") }
                        .help("Xoay phải 90°")
                    Toggle(isOn: state.edit(\.flipH)) {
                        Image(systemName: "arrow.left.and.right.righttriangle.left.righttriangle.right")
                    }
                    .toggleStyle(BrandToggleStyle())
                    .help("Lật ngang")
                    Toggle(isOn: state.edit(\.flipV)) {
                        Image(systemName: "arrow.up.and.down.righttriangle.up.righttriangle.down")
                    }
                    .toggleStyle(BrandToggleStyle())
                    .help("Lật dọc")
                }
                .controlSize(.small)
                if clip.edit.rotation != 0 {
                    Text("\(clip.edit.rotation)°").font(.caption).foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
            }
        }
        Divider()

        InspectorSection(title: "Cắt viền", sections: sections) {
            ParamRow(label: "Trái", value: state.edit(\.cropL), range: 0...0.45, reset: 0,
                     unit: "%", scale: 100, decimals: 1, step: 0.005)
            ParamRow(label: "Phải", value: state.edit(\.cropR), range: 0...0.45, reset: 0,
                     unit: "%", scale: 100, decimals: 1, step: 0.005)
            ParamRow(label: "Trên", value: state.edit(\.cropT), range: 0...0.45, reset: 0,
                     unit: "%", scale: 100, decimals: 1, step: 0.005)
            ParamRow(label: "Dưới", value: state.edit(\.cropB), range: 0...0.45, reset: 0,
                     unit: "%", scale: 100, decimals: 1, step: 0.005)
        }
        Divider()

        InspectorSection(title: "Tốc độ", sections: sections) {
            ParamRow(label: "Tốc độ phát", value: state.edit(\.speed), range: 0.25...2, reset: 1,
                     unit: "×", decimals: 2, step: 0.05, bipolar: true)
            HStack(spacing: 10) {
                RowLabel(text: "")
                HStack(spacing: 4) {
                    ForEach([0.5, 0.75, 1.0, 1.5, 2.0], id: \.self) { v in
                        Button("\(Num.fmt(v, 2))×") { state.mutateEdit { $0.speed = v } }
                    }
                }
                .controlSize(.small)
            }
            Text("Thời lượng trong GIF: \(Num.fmt(clip.outLength, 2)) giây")
                .font(.caption)
                .foregroundStyle(.secondary)
                .padding(.leading, 98)
        }
        Divider()

        InspectorSection(title: "Màu sắc", sections: sections) {
            if state.clips.count > 1 {
                Button("Áp dụng cho tất cả") { state.applyLookToAll() }
                    .controlSize(.mini)
                    .help("Áp dụng thiết lập màu sắc của đoạn này cho mọi đoạn trong trình tự")
            }
        } content: {
            ParamRow(label: "Độ sáng", value: state.edit(\.brightness), range: -0.3...0.3, reset: 0,
                     scale: 100, decimals: 0, step: 0.01, bipolar: true)
            ParamRow(label: "Tương phản", value: state.edit(\.contrast), range: 0.5...1.5, reset: 1,
                     unit: "%", scale: 100, decimals: 0, step: 0.01, bipolar: true)
            ParamRow(label: "Độ bão hoà", value: state.edit(\.saturation), range: 0...2, reset: 1,
                     unit: "%", scale: 100, decimals: 0, step: 0.01, bipolar: true)
            ParamRow(label: "Độ nét", value: state.edit(\.sharpen), range: 0...1.5, reset: 0,
                     unit: "%", scale: 100, decimals: 0, step: 0.01)
            HStack(spacing: 10) {
                RowLabel(text: "Khử nhiễu")
                Toggle("Giảm nhiễu hạt", isOn: state.edit(\.denoise))
                    .toggleStyle(BrandCheckboxStyle())
                    .font(.callout)
                    .help("Giảm nhiễu hạt của video gốc, đồng thời giúp GIF nhẹ hơn")
            }
        }
    }

    private func alignButtons(_ titles: [String], set: @escaping (Double) -> Void) -> some View {
        HStack(spacing: 4) {
            Button(titles[0]) { set(1) }
            Button(titles[1]) { set(0) }
            Button(titles[2]) { set(-1) }
        }
        .controlSize(.small)
    }
}

// MARK: - Export settings

struct ExportInspector: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var sections: SectionState
    @StateObject private var advanced = UIFlag()

    var body: some View {
        let (w, h) = state.outputSize
        InspectorSection(title: "Kích thước", sections: sections) {
            HStack(spacing: 10) {
                RowLabel(text: "Chiều rộng")
                Picker("", selection: $state.width) {
                    ForEach(AppState.widthOptions, id: \.self) { Text("\($0) px").tag($0) }
                }
                .labelsHidden()
                .frame(width: 110)
                Spacer(minLength: 0)
            }
            HStack(spacing: 10) {
                RowLabel(text: "Đầu ra")
                Text("\(w) × \(h) px · tỉ lệ \(state.aspectLabel)").monospacedDigit()
            }
        }
        Divider()

        InspectorSection(title: "Chất lượng", sections: sections) {
            HStack(spacing: 10) {
                RowLabel(text: "FPS")
                Picker("", selection: $state.fps) {
                    ForEach(AppState.fpsOptions, id: \.self) { Text("\($0)").tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
            }
            HStack(spacing: 10) {
                RowLabel(text: "Phối màu")
                Picker("", selection: $state.dither) {
                    ForEach(Dither.allCases) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
            }
            Text(state.dither.note)
                .font(.caption)
                .foregroundStyle(.secondary)
                .padding(.leading, 98)
        }
        Divider()

        InspectorSection(title: "Dung lượng", sections: sections) {
            HStack(spacing: 10) {
                RowLabel(text: "Tối đa")
                ScrubField(value: $state.limitMB, range: 0.5...50, step: 0.5, decimals: 1, unit: " MB")
                    .frame(width: 80, height: 20)
                Spacer(minLength: 0)
            }
            HStack(spacing: 10) {
                RowLabel(text: "")
                Toggle("Tự động tối ưu khi vượt giới hạn", isOn: $state.autoFit)
                    .toggleStyle(BrandCheckboxStyle())
                    .font(.callout)
            }
            if state.autoFit {
                HStack(spacing: 10) {
                    RowLabel(text: "")
                    Toggle("Bắt buộc đạt dưới giới hạn", isOn: $state.forceLimit)
                        .toggleStyle(BrandCheckboxStyle())
                        .font(.callout)
                }
                Text(state.forceLimit
                     ? "App tự cân đối mức nén, FPS và chiều rộng. Nếu đã chạm mức tối thiểu mà vẫn vượt, app giảm thêm (thấp nhất \(GifEncoder.hardMinFps) fps · \(GifEncoder.hardMinWidth) px) cho đến khi đạt."
                     : "App tự cân đối mức nén, FPS và chiều rộng nhưng không xuống dưới mức tối thiểu; có thể vẫn vượt giới hạn.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.leading, 98)
                DisclosureGroup(isExpanded: $advanced.on) {
                    VStack(alignment: .leading, spacing: 9) {
                        HStack(spacing: 10) {
                            RowLabel(text: "Ưu tiên giữ")
                            Picker("", selection: $state.fitPriority) {
                                ForEach(FitPriority.allCases) { Text($0.rawValue).tag($0) }
                            }
                            .pickerStyle(.segmented)
                            .labelsHidden()
                        }
                        Text(state.fitPriority == .keepSize
                             ? "Giữ nguyên chiều rộng và FPS, nén mạnh dần (có thể thấy hạt nhiễu nhẹ). Chỉ giảm FPS rồi chiều rộng khi đã nén tối đa mà vẫn vượt."
                             : "Giảm FPS rồi chiều rộng trước để giữ ảnh sạch. Chỉ nén mạnh khi đã chạm mức tối thiểu.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                            .padding(.leading, 98)
                        HStack(spacing: 10) {
                            RowLabel(text: "FPS tối thiểu")
                            ScrubField(value: Binding(get: { Double(state.minFps) }, set: { state.minFps = Int($0.rounded()) }),
                                       range: 6...30, step: 1, decimals: 0, unit: " fps")
                                .frame(width: 80, height: 20)
                            Spacer(minLength: 0)
                        }
                        HStack(spacing: 10) {
                            RowLabel(text: "Rộng tối thiểu")
                            ScrubField(value: Binding(get: { Double(state.minWidth) }, set: { state.minWidth = Int($0.rounded()) / 2 * 2 }),
                                       range: 240...1200, step: 10, decimals: 0, unit: " px")
                                .frame(width: 80, height: 20)
                            Spacer(minLength: 0)
                        }
                    }
                    .padding(.top, 8)
                } label: {
                    Text("Tuỳ chọn nâng cao").font(.callout).foregroundStyle(.secondary)
                }
            }
        }
    }
}

// MARK: - Export status / result (always visible at the bottom)

struct ExportPanel: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        VStack(spacing: 10) {
            switch state.status {
            case .idle:
                EmptyView()
            case .encoding(let msg):
                VStack(alignment: .leading, spacing: 6) {
                    ProgressView().progressViewStyle(.linear)
                    HStack {
                        Text(msg).font(.callout).lineLimit(2)
                        Spacer()
                        Button("Huỷ") { state.cancelExport() }.controlSize(.small)
                    }
                }
            case .failed(let msg):
                HStack(alignment: .top, spacing: 6) {
                    Image(systemName: "xmark.octagon.fill").foregroundStyle(.red)
                    Text(msg).font(.callout).textSelection(.enabled)
                    Spacer(minLength: 0)
                    Button { state.status = .idle } label: { Image(systemName: "xmark") }
                        .buttonStyle(.plain)
                        .foregroundStyle(.secondary)
                }
            case .done(let r):
                ResultView(result: r)
            }

        }
    }
}

struct ResultView: View {
    @EnvironmentObject var state: AppState
    let result: GifResult

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            AnimatedImage(url: result.url)
                .aspectRatio(CGFloat(result.width) / CGFloat(max(result.height, 1)), contentMode: .fit)
                .frame(maxWidth: .infinity, maxHeight: 150)
                .clipShape(RoundedRectangle(cornerRadius: 6))
                .onDrag { NSItemProvider(contentsOf: result.url) ?? NSItemProvider() }
                .help("Kéo ảnh này thả thẳng vào CMS hoặc trình duyệt")
            HStack(spacing: 6) {
                Image(systemName: result.underLimit ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                    .foregroundStyle(result.underLimit ? .green : .orange)
                Text("\(Num.fmt(Double(result.bytes) / 1_000_000, 2)) MB").bold().monospacedDigit()
                Text("· \(result.width) × \(result.height) · \(result.fps) fps"
                     + (result.notes.isEmpty ? "" : " · " + result.notes.joined(separator: " · ")))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Spacer(minLength: 0)
                Button { state.status = .idle } label: { Image(systemName: "xmark") }
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                    .help("Đóng")
            }
            .font(.callout)
            if !result.underLimit {
                Text(result.belowFloor
                     ? "Vẫn vượt giới hạn \(Num.fmt(Double(result.limitBytes) / 1_000_000, 1)) MB dù đã nén tối đa và giảm tới \(GifEncoder.hardMinFps) fps · \(GifEncoder.hardMinWidth) px. Hãy rút ngắn thời lượng."
                     : "Vẫn vượt giới hạn \(Num.fmt(Double(result.limitBytes) / 1_000_000, 1)) MB dù đã nén tối đa ở mức FPS và chiều rộng tối thiểu. Hãy rút ngắn thời lượng, hoặc bật “Bắt buộc đạt dưới giới hạn”.")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
            Text(result.url.lastPathComponent)
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.middle)
            HStack {
                Button("Hiện trong Finder") { NSWorkspace.shared.activateFileViewerSelecting([result.url]) }
                Button("Sao chép tệp") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.writeObjects([result.url as NSURL])
                }
            }
            .controlSize(.small)
        }
    }
}

struct AnimatedImage: NSViewRepresentable {
    let url: URL
    func makeNSView(context: Context) -> NSImageView {
        let v = NSImageView()
        v.animates = true
        v.imageScaling = .scaleProportionallyUpOrDown
        v.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        v.setContentCompressionResistancePriority(.defaultLow, for: .vertical)
        return v
    }
    func updateNSView(_ v: NSImageView, context: Context) {
        if context.coordinator.url != url {
            context.coordinator.url = url
            v.image = NSImage(contentsOf: url)
        }
    }
    func makeCoordinator() -> Coordinator { Coordinator() }
    final class Coordinator { var url: URL? }
}
