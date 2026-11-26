import CoreGraphics

/// The app's mark: a white geometric "E" on a brand-red rounded square.
/// Drawn from simple bars (not taken from any logo artwork). Shared with tools/make-icon.swift.
enum BrandMark {
    /// Path of the "E" inside a square of side `s` whose origin is `o` (y-up coordinates).
    static func ePath(origin o: CGPoint, side s: CGFloat) -> CGPath {
        let w = s * 0.44, h = s * 0.54, t = s * 0.115
        let x = o.x + (s - w) / 2 + s * 0.01, y = o.y + (s - h) / 2
        let p = CGMutablePath()
        p.addRect(CGRect(x: x, y: y, width: t, height: h))                       // spine
        p.addRect(CGRect(x: x, y: y + h - t, width: w, height: t))               // top bar
        p.addRect(CGRect(x: x, y: y + (h - t) / 2, width: w * 0.82, height: t))  // middle bar
        p.addRect(CGRect(x: x, y: y, width: w, height: t))                       // bottom bar
        return p
    }
}
