import AVFoundation
import CoreImage
import Foundation

/// Per-clip edits. The same parameters drive both the live preview (Core Image)
/// and the export (ffmpeg filters), via the shared `EditMath.layout`.
struct ClipEdit: Equatable, Codable {
    // framing
    var fit = false            // false: fill frame (crop); true: fit inside, blurred background
    var zoom = 1.0             // 1...4
    var panX = 0.0             // -1...1 (positive = content moves right)
    var panY = 0.0             // -1...1 (positive = content moves down)
    // edge crop, fractions of the source (0...0.45)
    var cropL = 0.0, cropT = 0.0, cropR = 0.0, cropB = 0.0
    // orientation
    var rotation = 0           // 0, 90, 180, 270 clockwise
    var flipH = false
    var flipV = false
    // time
    var speed = 1.0            // 0.25...2
    // look
    var brightness = 0.0       // -0.3...0.3
    var contrast = 1.0         // 0.5...1.5
    var saturation = 1.0       // 0...2
    var sharpen = 0.0          // 0...1.5
    var denoise = false

    var hasColor: Bool { brightness != 0 || contrast != 1 || saturation != 1 }
    var hasEdgeCrop: Bool { cropL > 0 || cropT > 0 || cropR > 0 || cropB > 0 }

    mutating func resetFraming() { zoom = 1; panX = 0; panY = 0 }
    mutating func copyLook(from o: ClipEdit) {
        brightness = o.brightness; contrast = o.contrast; saturation = o.saturation
        sharpen = o.sharpen; denoise = o.denoise
    }
}

struct EditLayout {
    let iw, ih: Double     // size after edge crop + rotation (source px)
    let s: Double          // scale from that image into the frame
    let dw, dh: Double     // scaled size
    let tlx, tly: Double   // top-left of the scaled image inside the frame (top-down coords)
}

enum EditMath {
    static func layout(_ e: ClipEdit, src: CGSize, W: Double, H: Double) -> EditLayout {
        let cw = max(2, src.width * (1 - e.cropL - e.cropR))
        let ch = max(2, src.height * (1 - e.cropT - e.cropB))
        let rotated = e.rotation % 180 != 0
        let iw = rotated ? ch : cw, ih = rotated ? cw : ch
        let s0 = e.fit ? min(W / iw, H / ih) : max(W / iw, H / ih)
        let s = s0 * max(1, e.zoom)
        let dw = iw * s, dh = ih * s
        let tlx = (W - dw) / 2 + e.panX * abs(W - dw) / 2
        let tly = (H - dh) / 2 + e.panY * abs(H - dh) / 2
        return EditLayout(iw: iw, ih: ih, s: s, dw: dw, dh: dh, tlx: tlx, tly: tly)
    }

    private static func even(_ v: Double) -> Int { max(2, Int(v.rounded(.down)) / 2 * 2) }
    private static func f(_ v: Double) -> String { String(format: "%.4f", v) }

    /// ffmpeg filter graph parts for input `i`, ending in label `[v<i>]` (rgb24, W×H).
    static func ffmpegParts(index i: Int, edit e: ClipEdit, src: CGSize, W: Int, H: Int, fps: Int) -> [String] {
        // Edge crop in integer (even) source pixels so the following math is exact.
        let sw = Double(even(src.width)), sh = Double(even(src.height))
        let cx = even(sw * e.cropL) , cy = even(sh * e.cropT)
        let cw = min(Int(sw) - cx, even(sw * (1 - e.cropL - e.cropR)))
        let ch = min(Int(sh) - cy, even(sh * (1 - e.cropT - e.cropB)))

        // format=yuv420p: VideoToolbox hands over NV12, which boxblur mangles (chroma garbage).
        var pre = "[\(i):v]setpts=(PTS-STARTPTS)/\(f(e.speed)),fps=\(fps),format=yuv420p"
        if e.hasEdgeCrop { pre += ",crop=\(cw):\(ch):\(cx):\(cy)" }
        switch e.rotation {
        case 90: pre += ",transpose=clock"
        case 180: pre += ",hflip,vflip"
        case 270: pre += ",transpose=cclock"
        default: break
        }
        if e.flipH { pre += ",hflip" }
        if e.flipV { pre += ",vflip" }

        var post = ""
        if e.denoise { post += ",hqdn3d=4:3:6:4" }
        if e.hasColor { post += ",eq=brightness=\(f(e.brightness)):contrast=\(f(e.contrast)):saturation=\(f(e.saturation))" }
        if e.sharpen > 0.01 { post += ",unsharp=5:5:\(f(e.sharpen)):5:5:0" }
        post += ",setsar=1,format=rgb24[v\(i)]"

        var ee = e
        ee.cropL = Double(cx) / sw; ee.cropT = Double(cy) / sh
        ee.cropR = 1 - Double(cx + cw) / sw; ee.cropB = 1 - Double(cy + ch) / sh
        let L = layout(ee, src: CGSize(width: sw, height: sh), W: Double(W), H: Double(H))
        let iw = Int(L.iw.rounded()), ih = Int(L.ih.rounded())

        if !e.fit {
            // Visible window of the (cropped, rotated) image, then scale to the frame.
            let w0 = min(iw, even(Double(W) / L.s)), h0 = min(ih, even(Double(H) / L.s))
            let x0 = min(max(0, Int((-L.tlx / L.s).rounded())), iw - w0)
            let y0 = min(max(0, Int((-L.tly / L.s).rounded())), ih - h0)
            return [pre + ",crop=\(w0):\(h0):\(x0):\(y0),scale=\(W):\(H):flags=lanczos" + post]
        }

        let bw = max(2, W / 4 / 2 * 2), bh = max(2, H / 4 / 2 * 2)
        let blur = max(1, min(6, bh / 8))
        return [
            pre + ",split[f\(i)][g\(i)]",
            "[g\(i)]scale=\(bw):\(bh):force_original_aspect_ratio=increase,crop=\(bw):\(bh),"
                + "boxblur=\(blur):2,scale=\(W):\(H),eq=brightness=-0.07[b\(i)]",
            "[f\(i)]scale=\(even(L.dw)):\(even(L.dh)):flags=lanczos[k\(i)]",
            "[b\(i)][k\(i)]overlay=\(Int(L.tlx.rounded())):\(Int(L.tly.rounded()))" + post,
        ]
    }
}

// MARK: - Live preview (Core Image, inside an AVVideoComposition)

enum PreviewRenderer {
    private static func norm(_ i: CIImage) -> CIImage {
        i.transformed(by: CGAffineTransform(translationX: -i.extent.minX, y: -i.extent.minY))
    }

    /// Renders `source` (display-oriented frame) into a W×H frame. Core Image is y-up.
    static func render(_ source: CIImage, edit e: ClipEdit, size R: CGSize, outWidth: Double) -> CIImage {
        let W = max(R.width, 2), H = max(R.height, 2)
        let frame = CGRect(x: 0, y: 0, width: W, height: H)
        // An undecodable/empty frame has a null or infinite extent; returning NaN geometry
        // makes AVFoundation throw (crash). Show black instead.
        let ext = source.extent
        guard !ext.isNull, !ext.isInfinite, ext.width >= 2, ext.height >= 2 else {
            return CIImage(color: .black).cropped(to: frame)
        }
        var img = norm(source)
        let sw = img.extent.width, sh = img.extent.height

        if e.hasEdgeCrop {
            img = norm(img.cropped(to: CGRect(x: e.cropL * sw, y: e.cropB * sh,
                                              width: sw * (1 - e.cropL - e.cropR),
                                              height: sh * (1 - e.cropT - e.cropB))))
        }
        if e.rotation != 0 {
            img = norm(img.transformed(by: CGAffineTransform(rotationAngle: -CGFloat(e.rotation) * .pi / 180)))
        }
        if e.flipH { img = norm(img.transformed(by: CGAffineTransform(scaleX: -1, y: 1))) }
        if e.flipV { img = norm(img.transformed(by: CGAffineTransform(scaleX: 1, y: -1))) }

        let L = EditMath.layout(e, src: CGSize(width: sw, height: sh), W: W, H: H)
        let placed = img
            .transformed(by: CGAffineTransform(scaleX: L.s, y: L.s))
            .transformed(by: CGAffineTransform(translationX: L.tlx, y: H - L.tly - L.dh))

        var out: CIImage
        if e.fit {
            let sc = max(W / L.iw, H / L.ih)
            let bg = img
                .transformed(by: CGAffineTransform(scaleX: sc, y: sc))
                .transformed(by: CGAffineTransform(translationX: (W - L.iw * sc) / 2, y: (H - L.ih * sc) / 2))
                .clampedToExtent()
                .applyingGaussianBlur(sigma: W / 24)
                .applyingFilter("CIColorControls", parameters: ["inputBrightness": -0.07])
                .cropped(to: frame)
            out = placed.composited(over: bg)
        } else {
            out = placed.composited(over: CIImage(color: .black).cropped(to: frame))
        }
        if e.denoise {
            out = out.applyingFilter("CINoiseReduction", parameters: ["inputNoiseLevel": 0.03, "inputSharpness": 0.4])
        }
        if e.hasColor {
            // ffmpeg's eq works on gamma-encoded values; Core Image works in linear light.
            // Convert to sRGB-encoded values around the adjustment so both look the same.
            out = out.applyingFilter("CILinearToSRGBToneCurve")
            out = out.applyingFilter("CIColorControls", parameters: [
                "inputBrightness": e.brightness, "inputContrast": e.contrast, "inputSaturation": e.saturation,
            ])
            out = out.applyingFilter("CISRGBToneCurveToLinear")
        }
        if e.sharpen > 0.01 {
            out = out.applyingFilter("CIUnsharpMask", parameters: ["inputRadius": 2.5 * W / outWidth, "inputIntensity": e.sharpen])
        }
        return out.cropped(to: frame)
    }

    /// Maps a track's preferredTransform to the Core Image orientation that displays the raw
    /// (unrotated) decoded frame upright.
    nonisolated static func orientation(for t: CGAffineTransform) -> CGImagePropertyOrientation {
        let deg = Int((atan2(t.b, t.a) * 180 / .pi).rounded())
        let mirrored = t.a * t.d - t.b * t.c < 0
        switch ((deg % 360) + 360) % 360 {
        case 90: return mirrored ? .rightMirrored : .right
        case 180: return mirrored ? .downMirrored : .down
        case 270: return mirrored ? .leftMirrored : .left
        default: return mirrored ? .upMirrored : .up
        }
    }
}
