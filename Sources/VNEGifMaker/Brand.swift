import AppKit
import SwiftUI

/// Brand colors and mark.
enum Brand {
    static let name = "VnExpress GIF Maker"
    static let author = "Lâm Nguyễn"
    static let team = "Ban Video"
    static let credit = "Phát triển bởi Lâm Nguyễn · Ban Video, VnExpress"

    /// VnExpress red (#9F224E) — fills, prominent buttons.
    static let red = Color(red: 0x9F / 255, green: 0x22 / 255, blue: 0x4E / 255)
    /// Lighter tint of the brand red for thin strokes and indicators on dark backgrounds.
    static let bright = Color(red: 0xE2 / 255, green: 0x42 / 255, blue: 0x7C / 255)

    static let nsRed = NSColor(srgbRed: 0x9F / 255, green: 0x22 / 255, blue: 0x4E / 255, alpha: 1)
    static let nsBright = NSColor(srgbRed: 0xE2 / 255, green: 0x42 / 255, blue: 0x7C / 255, alpha: 1)
}

/// The app mark: white "E" on a brand-red rounded square.
struct BrandMarkView: View {
    var body: some View {
        Canvas { ctx, size in
            let side = min(size.width, size.height)
            let o = CGPoint(x: (size.width - side) / 2, y: (size.height - side) / 2)
            ctx.fill(Path(roundedRect: CGRect(origin: o, size: CGSize(width: side, height: side)),
                          cornerRadius: side * 0.22), with: .color(Brand.red))
            // ePath is y-up; flip into the canvas (y-down).
            var flip = CGAffineTransform(translationX: 0, y: size.height).scaledBy(x: 1, y: -1)
            if let e = BrandMark.ePath(origin: o, side: side).copy(using: &flip) {
                ctx.fill(Path(e), with: .color(.white))
            }
        }
        .aspectRatio(1, contentMode: .fit)
        .accessibilityHidden(true)
    }
}

/// Toggle drawn as a button that fills with the brand red when on.
struct BrandToggleStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        if configuration.isOn {
            Button { configuration.isOn.toggle() } label: { configuration.label }
                .buttonStyle(.borderedProminent)
                .tint(Brand.red)
        } else {
            Button { configuration.isOn.toggle() } label: { configuration.label }
                .buttonStyle(.bordered)
        }
    }
}

/// Checkbox with a brand-red check.
struct BrandCheckboxStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        Button { configuration.isOn.toggle() } label: {
            HStack(spacing: 6) {
                Image(systemName: configuration.isOn ? "checkmark.square.fill" : "square")
                    .foregroundStyle(configuration.isOn ? Brand.red : Color.secondary)
                    .font(.system(size: 14))
                configuration.label
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}
