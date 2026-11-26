// Renders the app icon (brand-red tile, white "E", "GIF" pill) into an .iconset folder.
// Build & run (see build.sh):
//   swiftc -O -parse-as-library -o .build/make-icon tools/make-icon.swift Sources/VNEGifMaker/BrandMark.swift
//   .build/make-icon <out.iconset>
import AppKit
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers


func rgb(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
    CGColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: a)
}

/// Draws the 1024-pt design into `ctx` (CoreGraphics, y-up).
func drawIcon(_ ctx: CGContext) {
    let canvas: CGFloat = 1024
    let tile = CGRect(x: 100, y: 100, width: 824, height: 824)
    let shape = CGPath(roundedRect: tile, cornerWidth: 185, cornerHeight: 185, transform: nil)

    // Drop shadow + brand gradient tile.
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -14), blur: 28, color: rgb(0x000000, 0.35))
    ctx.addPath(shape)
    ctx.setFillColor(rgb(0x9F224E))
    ctx.fillPath()
    ctx.restoreGState()

    ctx.saveGState()
    ctx.addPath(shape)
    ctx.clip()
    let grad = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB),
                          colors: [rgb(0xC0305F), rgb(0x9F224E), rgb(0x7E1739)] as CFArray,
                          locations: [0, 0.55, 1])!
    ctx.drawLinearGradient(grad, start: CGPoint(x: 0, y: tile.maxY), end: CGPoint(x: 0, y: tile.minY), options: [])
    ctx.restoreGState()

    // White "E" (same shape as the in-app mark), in the upper part of the tile.
    // Centered between the "GIF" pill (top at y 310) and the tile top (y 924).
    let eSide: CGFloat = 600
    ctx.addPath(BrandMark.ePath(origin: CGPoint(x: canvas / 2 - eSide / 2, y: 627 - eSide / 2), side: eSide))
    ctx.setFillColor(rgb(0xFFFFFF))
    ctx.fillPath()

    // "GIF" pill.
    let pill = CGRect(x: canvas / 2 - 150, y: canvas - 770 - 56, width: 300, height: 112)
    ctx.addPath(CGPath(roundedRect: pill, cornerWidth: 56, cornerHeight: 56, transform: nil))
    ctx.setFillColor(rgb(0xFFFFFF))
    ctx.fillPath()

    let font = NSFont.systemFont(ofSize: 86, weight: .heavy)
    let text = NSAttributedString(string: "GIF", attributes: [
        .font: font, .foregroundColor: NSColor(cgColor: rgb(0x9F224E))!, .kern: 4,
    ])
    let line = CTLineCreateWithAttributedString(text)
    let b = CTLineGetBoundsWithOptions(line, .useGlyphPathBounds)
    ctx.textPosition = CGPoint(x: pill.midX - b.width / 2 - b.minX, y: pill.midY - b.height / 2 - b.minY)
    CTLineDraw(line, ctx)
}

func render(px: Int) -> CGImage {
    let ctx = CGContext(data: nil, width: px, height: px, bitsPerComponent: 8, bytesPerRow: 0,
                        space: CGColorSpace(name: CGColorSpace.sRGB)!,
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    ctx.interpolationQuality = .high
    ctx.setAllowsAntialiasing(true)
    ctx.scaleBy(x: CGFloat(px) / 1024, y: CGFloat(px) / 1024)
    drawIcon(ctx)
    return ctx.makeImage()!
}

func writePNG(_ img: CGImage, _ name: String, to outDir: URL) {
    let url = outDir.appendingPathComponent(name)
    let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)!
    CGImageDestinationAddImage(dest, img, nil)
    CGImageDestinationFinalize(dest)
}

@main
struct MakeIcon {
    static func main() {
        let outDir = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "AppIcon.iconset")
        try? FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)
        for base in [16, 32, 128, 256, 512] {
            writePNG(render(px: base), "icon_\(base)x\(base).png", to: outDir)
            writePNG(render(px: base * 2), "icon_\(base)x\(base)@2x.png", to: outDir)
        }
        print("Wrote \(outDir.path)")
    }
}
