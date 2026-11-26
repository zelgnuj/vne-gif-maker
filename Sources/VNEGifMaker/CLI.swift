import AVFoundation
import CoreImage
import Foundation
import ImageIO
import UniformTypeIdentifiers

/// Headless modes (for scripting and tests):
///   VNEGifMaker --cli out.gif a.mp4@1-3.5 b.mov@0-2:fit:speed=0.5 [--width 680] [--fps 12] [--aspect 5:3]
///   VNEGifMaker --preview-frame out.png a.mp4@1.5:rot=90:crop=0.1,0,0.1,0 [--aspect 5:3]
/// Segment options (":"-separated): fit, dn, rot=90, flipx, flipy, zoom=1.5, pan=x,y,
/// crop=l,t,r,b, speed=0.5, b=0.1, c=1.2, s=1.3, sharp=0.8
enum CLI {
    struct Seg { var url: URL; var a: Double; var b: Double?; var edit: ClipEdit }

    static func parseSeg(_ s: String) -> Seg {
        var opts = s.components(separatedBy: ":")
        let head = opts.removeFirst()
        let at = head.lastIndex(of: "@")
        let path = at.map { String(head[..<$0]) } ?? head
        let nums = at.map { head[head.index(after: $0)...].split(separator: "-").compactMap { Double($0) } } ?? []
        var e = ClipEdit()
        for o in opts {
            let kv = o.split(separator: "=", maxSplits: 1).map(String.init)
            let v = kv.count > 1 ? kv[1] : ""
            let ds = v.split(separator: ",").compactMap { Double($0) }
            switch kv[0] {
            case "fit": e.fit = true
            case "dn": e.denoise = true
            case "flipx": e.flipH = true
            case "flipy": e.flipV = true
            case "rot": e.rotation = Int(v) ?? 0
            case "zoom": e.zoom = Double(v) ?? 1
            case "pan" where ds.count == 2: e.panX = ds[0]; e.panY = ds[1]
            case "crop" where ds.count == 4: e.cropL = ds[0]; e.cropT = ds[1]; e.cropR = ds[2]; e.cropB = ds[3]
            case "speed": e.speed = Double(v) ?? 1
            case "b": e.brightness = Double(v) ?? 0
            case "c": e.contrast = Double(v) ?? 1
            case "s": e.saturation = Double(v) ?? 1
            case "sharp": e.sharpen = Double(v) ?? 0
            default: break
            }
        }
        return Seg(url: URL(fileURLWithPath: path), a: nums.first ?? 0, b: nums.count > 1 ? nums[1] : nil, edit: e)
    }

    static func displaySize(_ asset: AVAsset) async throws -> CGSize {
        guard let track = try await asset.loadTracks(withMediaType: .video).first else {
            throw EncodeError.ffmpeg("no video track")
        }
        let (size, t) = try await track.load(.naturalSize, .preferredTransform)
        let r = CGRect(origin: .zero, size: size).applying(t)
        return CGSize(width: abs(r.width), height: abs(r.height))
    }

    static func run(_ args: [String], previewFrame: Bool) -> Never {
        var width = 680, fps = 12, aspectRatio: Double? = 5.0 / 3.0, priority = FitPriority.keepSize
        var limitMB = 8.0, force = true
        var segs: [String] = []
        var it = args.dropFirst().makeIterator()
        let output = URL(fileURLWithPath: args[0])
        while let a = it.next() {
            switch a {
            case "--width": width = Int(it.next() ?? "") ?? width
            case "--fps": fps = Int(it.next() ?? "") ?? fps
            case "--aspect":
                let v = it.next() ?? ""
                aspectRatio = v == "orig" || v == "Gốc" ? nil : Aspect.parse(v) ?? aspectRatio
            case "--keep-sharp": priority = .keepSharpness
            case "--limit": limitMB = Double(it.next() ?? "") ?? limitMB
            case "--no-force": force = false
            default: segs.append(a)
            }
        }

        Task { @MainActor in
            do {
                var specs: [ClipSpec] = []
                var firstAspect: Double?
                for s in segs {
                    let seg = parseSeg(s)
                    let src = try await displaySize(AVURLAsset(url: seg.url))
                    if firstAspect == nil { firstAspect = src.width / src.height }
                    specs.append(ClipSpec(url: seg.url, start: seg.a, duration: (seg.b ?? seg.a + 3) - seg.a,
                                          sourceSize: src, edit: seg.edit))
                }
                let ratio = aspectRatio ?? firstAspect ?? 16 / 9

                if previewFrame {
                    // Render one frame exactly like the live preview: raw decoded frame →
                    // orientation from preferredTransform → PreviewRenderer → CIContext.
                    let c = specs[0]
                    let asset = AVURLAsset(url: c.url)
                    guard let track = try await asset.loadTracks(withMediaType: .video).first else {
                        throw EncodeError.ffmpeg("no video track")
                    }
                    let transform = try await track.load(.preferredTransform)
                    let reader = try AVAssetReader(asset: asset)
                    let out = AVAssetReaderTrackOutput(track: track, outputSettings: [
                        kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
                    ])
                    reader.timeRange = CMTimeRange(start: CMTime(seconds: c.start, preferredTimescale: 90_000),
                                                   duration: CMTime(seconds: 1, preferredTimescale: 90_000))
                    reader.add(out)
                    reader.startReading()
                    guard let sb = out.copyNextSampleBuffer(), let pb = CMSampleBufferGetImageBuffer(sb) else {
                        throw EncodeError.ffmpeg("cannot decode frame")
                    }
                    let src = CIImage(cvPixelBuffer: pb).oriented(PreviewRenderer.orientation(for: transform))
                    let size = CGSize(width: width, height: GifEncoder.size(width: width, aspect: ratio).1)
                    let rendered = PreviewRenderer.render(src, edit: c.edit, size: size, outWidth: Double(width))
                    guard let img = CIContext().createCGImage(rendered, from: CGRect(origin: .zero, size: size)) else {
                        throw EncodeError.ffmpeg("render failed")
                    }
                    guard let dest = CGImageDestinationCreateWithURL(output as CFURL, UTType.png.identifier as CFString, 1, nil) else {
                        throw EncodeError.ffmpeg("cannot write png")
                    }
                    CGImageDestinationAddImage(dest, img, nil)
                    CGImageDestinationFinalize(dest)
                    print("OK preview \(img.width)x\(img.height)")
                    exit(0)
                }

                let req = GifRequest(clips: specs, aspect: ratio, width: width,
                                     fps: fps, dither: .balanced, limitBytes: Int(limitMB * 1_000_000), autoFit: true,
                                     priority: priority, force: force)
                let t0 = Date()
                let res = try await GifEncoder.makeGif(req, output: output) { msg in print(msg) }
                print(String(format: "OK %@ %.2f MB %dx%d %dfps attempts=%d squeeze=%d below=\(res.belowFloor) time=%.2fs",
                             res.url.path, Double(res.bytes) / 1e6, res.width, res.height,
                             res.fps, res.attempts, res.squeezeLevel, Date().timeIntervalSince(t0)))
                exit(0)
            } catch {
                print("ERROR: \(error.localizedDescription)")
                exit(1)
            }
        }
        RunLoop.main.run()
        exit(0)
    }
}
