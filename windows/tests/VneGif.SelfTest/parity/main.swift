import Foundation
// Prints EditMath.ffmpegParts for fixed cases; the C# self-test prints the same and compares.
func cases() -> [(ClipEdit, CGSize, Int, Int)] {
    var out: [(ClipEdit, CGSize, Int, Int)] = []
    var e = ClipEdit(); out.append((e, CGSize(width: 1920, height: 1080), 680, 408))
    e = ClipEdit(); e.fit = true; out.append((e, CGSize(width: 1080, height: 1920), 680, 408))
    e = ClipEdit(); e.zoom = 1.7; e.panX = -0.4; e.panY = 0.3; e.cropL = 0.1; e.cropT = 0.05; e.cropR = 0.02; e.cropB = 0.13
    e.rotation = 90; e.flipH = true; e.speed = 0.75; e.brightness = 0.1; e.contrast = 1.2; e.saturation = 0.8; e.sharpen = 0.6; e.denoise = true
    out.append((e, CGSize(width: 1281, height: 721), 800, 450))
    e = ClipEdit(); e.fit = true; e.zoom = 1.3; e.panX = 1; e.rotation = 270; e.flipV = true; e.speed = 2
    out.append((e, CGSize(width: 3840, height: 2160), 600, 600))
    e = ClipEdit(); e.rotation = 180; e.panY = -1; e.zoom = 4
    out.append((e, CGSize(width: 720, height: 1280), 1200, 514))
    return out
}
for (i, c) in cases().enumerated() {
    print(EditMath.ffmpegParts(index: i, edit: c.0, src: c.1, W: c.2, H: c.3, fps: 12).joined(separator: ";"))
}
