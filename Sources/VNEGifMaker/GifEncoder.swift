import Foundation
import CoreGraphics
import CryptoKit

enum Aspect: String, CaseIterable, Identifiable {
    case r21x9 = "21:9", r2x1 = "2:1", r191 = "1.91:1", r16x9 = "16:9", r5x3 = "5:3", r3x2 = "3:2"
    case r4x3 = "4:3", r5x4 = "5:4", r1x1 = "1:1", r4x5 = "4:5", r3x4 = "3:4", r2x3 = "2:3", r9x16 = "9:16"
    case original = "Gốc", custom = "Tuỳ chỉnh"
    var id: String { rawValue }

    static let landscape: [Aspect] = [.r21x9, .r2x1, .r191, .r16x9, .r5x3, .r3x2, .r4x3, .r5x4]
    static let squareAndPortrait: [Aspect] = [.r1x1, .r4x5, .r3x4, .r2x3, .r9x16]

    /// Display label with a Vietnamese decimal comma.
    var label: String { rawValue.replacingOccurrences(of: ".", with: ",") }

    /// Fixed ratio (w/h); nil for "Gốc" and "Tuỳ chỉnh", which the caller resolves.
    var ratio: Double? {
        switch self {
        case .original, .custom: return nil
        default: return Aspect.parse(rawValue)
        }
    }

    /// Parses "W:H" (dot or comma decimals) into w/h.
    static func parse(_ s: String) -> Double? {
        let p = s.replacingOccurrences(of: ",", with: ".").split(separator: ":")
        guard p.count == 2, let w = Double(p[0]), let h = Double(p[1]), w > 0, h > 0 else { return nil }
        return w / h
    }
}

enum Dither: String, CaseIterable, Identifiable {
    case smooth = "Mịn", balanced = "Cân bằng", light = "Gọn nhẹ"
    var id: String { rawValue }
    var note: String {
        switch self {
        case .smooth: "Chuyển màu mượt nhất, dung lượng lớn nhất."
        case .balanced: "Cân bằng giữa chất lượng và dung lượng."
        case .light: "Dung lượng nhỏ nhất, có thể thấy vân màu."
        }
    }
    var filter: String {
        switch self {
        case .smooth: "dither=sierra2_4a"
        case .balanced: "dither=bayer:bayer_scale=3"
        case .light: "dither=bayer:bayer_scale=5"
        }
    }
}

enum CropMath {
    /// Crop rect in normalized (0...1) source coordinates, origin top-left.
    static func normalized(source: CGSize, ratio: Double?, offset: Double) -> CGRect {
        guard let r = ratio, source.width > 0, source.height > 0 else {
            return CGRect(x: 0, y: 0, width: 1, height: 1)
        }
        let o = min(max(offset, 0), 1)
        let srcR = source.width / source.height
        if srcR > r {
            let w = r / srcR
            return CGRect(x: (1 - w) * o, y: 0, width: w, height: 1)
        } else {
            let h = srcR / r
            return CGRect(x: 0, y: (1 - h) * o, width: 1, height: h)
        }
    }
}

struct ClipSpec {
    var url: URL
    var start: Double         // source seconds
    var duration: Double      // source seconds (output = duration / edit.speed)
    var sourceSize: CGSize    // display-oriented
    var edit: ClipEdit
}

/// What to sacrifice first when the GIF is over the size limit.
enum FitPriority: String, CaseIterable, Identifiable {
    case keepSize = "Kích thước & FPS"     // compress harder first, keep width/fps
    case keepSharpness = "Độ nét ảnh"      // lower fps/width first, compress harder last
    var id: String { rawValue }
}

struct GifRequest {
    var clips: [ClipSpec]
    var aspect: Double        // output w/h
    var width: Int
    var fps: Int
    var dither: Dither
    var limitBytes: Int
    var autoFit: Bool
    var priority: FitPriority = .keepSize
    var minFps: Int = 10
    var minWidth: Int = 600
    /// If still over the limit at the floors, keep lowering fps/width (down to hardMinFps/hardMinWidth).
    var force: Bool = true
}

struct GifResult: Equatable {
    let url: URL
    let bytes: Int
    let width: Int
    let height: Int
    let fps: Int
    let attempts: Int
    let limitBytes: Int
    /// Lossy compression step applied (0 = lossless optimization only).
    var squeezeLevel: Int = 0
    /// fps/width had to go below the user's minimums to fit.
    var belowFloor = false
    var underLimit: Bool { bytes <= limitBytes }

    /// Human-readable summary of what the optimizer did.
    var notes: [String] {
        var n: [String] = []
        if squeezeLevel > 0 { n.append("nén mức \(squeezeLevel)/\(GifEncoder.squeezeSteps.count)") }
        if belowFloor { n.append("dưới mức tối thiểu") }
        return n
    }
}

enum EncodeError: LocalizedError {
    case noFFmpeg
    case ffmpeg(String)
    var errorDescription: String? {
        switch self {
        case .noFFmpeg: "Không tìm thấy bộ mã hoá ffmpeg. Vui lòng cài lại ứng dụng."
        case .ffmpeg(let msg): msg
        }
    }
}

enum GifEncoder {
    static let gifsiclePath: String? = {
        var candidates = ["/opt/homebrew/bin/gifsicle", "/usr/local/bin/gifsicle"]
        if let bundled = Bundle.main.path(forResource: "gifsicle", ofType: nil) { candidates.insert(bundled, at: 0) }
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0) }
    }()

    /// Lossy steps, mildest first: gifsicle --lossy amount and palette size. Fewer colors are
    /// produced by ffmpeg's palettegen (better color choice and dithering than gifsicle --colors,
    /// ~5% smaller at equal look). A stronger step (lossy 200 / 96 colors) posterizes faces
    /// visibly, so dropping 1–2 fps is preferred beyond step 5.
    static let squeezeSteps: [(lossy: Int, colors: Int)] = [
        (40, 256), (80, 256), (120, 256), (120, 192), (160, 128),
    ]
    /// Size of each step relative to the lossless-optimized GIF (measured on handheld 680×408 footage).
    static let squeezeFactors: [Double] = [0.889, 0.858, 0.83, 0.75, 0.63]
    /// Absolute floors used when `force` is on.
    static let hardMinFps = 8
    static let hardMinWidth = 360

    static let ffmpegPath: String? = {
        var candidates = ["/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg"]
        if let bundled = Bundle.main.path(forResource: "ffmpeg", ofType: nil) { candidates.insert(bundled, at: 0) }
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0) }
    }()

    static func size(width: Int, aspect: Double) -> (Int, Int) {
        let w = max(2, width / 2 * 2)
        let h = max(2, Int((Double(w) / max(aspect, 0.01) / 2).rounded()) * 2)
        return (w, h)
    }

    static func arguments(_ r: GifRequest, width W: Int, height H: Int, fps: Int, output: URL) -> [String] {
        arguments(r, width: W, height: H, fps: fps, outputs: [(256, output)])
    }

    /// One decode pass, one GIF per palette size.
    static func arguments(_ r: GifRequest, width W: Int, height H: Int, fps: Int,
                          outputs: [(colors: Int, url: URL)]) -> [String] {
        func f(_ v: Double) -> String { String(format: "%.6f", v) }
        var args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y"]
        var parts: [String] = []
        for (i, c) in r.clips.enumerated() {
            args += ["-hwaccel", "videotoolbox", "-ss", f(c.start), "-t", f(c.duration), "-i", c.url.path]
            parts += EditMath.ffmpegParts(index: i, edit: c.edit, src: c.sourceSize, W: W, H: H, fps: fps)
        }
        let inputs = r.clips.indices.map { "[v\($0)]" }.joined()
        let k = outputs.count
        parts.append(inputs + "concat=n=\(r.clips.count):v=1:a=0,split=\(2 * k)"
                     + (0..<k).map { "[a\($0)][b\($0)]" }.joined())
        for (i, o) in outputs.enumerated() {
            parts.append("[a\(i)]palettegen=stats_mode=full" + (o.colors < 256 ? ":max_colors=\(o.colors)" : "") + "[p\(i)]")
            parts.append("[b\(i)][p\(i)]paletteuse=\(r.dither.filter):diff_mode=rectangle[o\(i)]")
        }
        args += ["-filter_complex", parts.joined(separator: ";")]
        for (i, o) in outputs.enumerated() { args += ["-map", "[o\(i)]", "-an", "-loop", "0", o.url.path] }
        return args
    }

    /// Encodes, and if `autoFit` is on, re-encodes with smaller fps/width until under the limit.
    /// Encodes, then — if over the limit and `autoFit` — optimizes in the order set by `priority`:
    ///  • keepSize:      lossless optimize → lossy steps → lower fps (≥ minFps) → lower width (≥ minWidth)
    ///  • keepSharpness: lossless optimize → lower fps → lower width → lossy steps
    /// Never goes below the floors; if still over, returns the smallest result (underLimit == false).
    static func makeGif(
        _ req: GifRequest,
        output: URL,
        onProgress: @escaping @MainActor (String) -> Void
    ) async throws -> GifResult {
        guard let ffmpeg = ffmpegPath else { throw EncodeError.noFFmpeg }
        guard !req.clips.isEmpty else { throw EncodeError.ffmpeg("Chưa có đoạn video nào.") }
        let tmpDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("vnegif-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: tmpDir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: tmpDir) }

        func bytes(_ u: URL) -> Int { (try? FileManager.default.attributesOfItem(atPath: u.path)[.size] as? Int) ?? 0 }
        var n = 0
        func tmp() -> URL { n += 1; return tmpDir.appendingPathComponent("t\(n).gif") }

        /// Base encodes for the current width/fps, keyed by palette size (made on demand).
        var bases: [Int: URL] = [:]
        var baseW = 0, baseH = 0, baseFps = 0
        func base(colors: Int) async throws -> URL {
            if let u = bases[colors] { return u }
            try await prepare([colors])
            return bases[colors]!
        }
        /// Encodes the missing palette sizes in a single ffmpeg run.
        func prepare(_ colors: [Int]) async throws {
            let missing = Array(Set(colors)).sorted(by: >).filter { bases[$0] == nil }
            guard !missing.isEmpty else { return }
            let outs = missing.map { (colors: $0, url: tmp()) }
            try await run(ffmpeg, arguments(req, width: baseW, height: baseH, fps: baseFps, outputs: outs))
            for o in outs { bases[o.colors] = o.url }
        }
        /// gifsicle pass: level 0 = lossless -O2, 1… = squeezeSteps[level-1].
        func squeeze(level: Int) async throws -> URL {
            let step = level > 0 ? squeezeSteps[level - 1] : (lossy: 0, colors: 256)
            let src = try await base(colors: step.colors)
            guard let gs = gifsiclePath else { return src }
            let out = tmp()
            var args = ["-O2", "--no-warnings"]
            if step.lossy > 0 { args.append("--lossy=\(step.lossy)") }
            try await run(gs, args + ["-o", out.path, src.path])
            return bytes(out) < bytes(src) ? out : src
        }

        var minFps = min(req.minFps, req.fps)
        var minWidth = min(req.minWidth, req.width)
        var belowFloor = false
        let limit = req.limitBytes
        var width = req.width, fps = req.fps
        var attempt = 0
        var lastLevel = 0
        var best: (url: URL, size: Int, w: Int, h: Int, fps: Int, level: Int, below: Bool)?

        func finish(_ r: (url: URL, size: Int, w: Int, h: Int, fps: Int, level: Int, below: Bool)) throws -> GifResult {
            if FileManager.default.fileExists(atPath: output.path) { try FileManager.default.removeItem(at: output) }
            try FileManager.default.moveItem(at: r.url, to: output)
            return GifResult(url: output, bytes: r.size, width: r.w, height: r.h, fps: r.fps,
                             attempts: attempt, limitBytes: limit, squeezeLevel: r.level, belowFloor: r.below)
        }

        while true {
            try Task.checkCancellation()
            attempt += 1
            let (w, h) = size(width: width, aspect: req.aspect)
            await onProgress(attempt == 1 ? "Đang xuất GIF (\(w) px · \(fps) fps)…"
                                          : "Đang xuất lại ở \(w) px · \(fps) fps…")
            bases = [:]; baseW = w; baseH = h; baseFps = fps
            // The previous attempt needed the strongest step and this one is sized for it:
            // skip the uncompressed encode and go straight to the two strongest steps.
            let deep = lastLevel >= squeezeSteps.count && gifsiclePath != nil
            let deepFirst = squeezeSteps.count - 1
            if lastLevel > 0 {
                // One ffmpeg pass for every palette this attempt will need.
                let from = deep ? deepFirst : max(1, lastLevel - 1)
                try await prepare((deep ? [] : [256]) + (from...squeezeSteps.count).map { squeezeSteps[$0 - 1].colors })
            }

            var cur = deep ? bases[squeezeSteps[deepFirst - 1].colors]! : try await squeeze(level: 0)
            var level = 0
            func record() {
                let sz = bytes(cur)
                if best == nil || sz < best!.size || sz <= limit { best = (cur, sz, w, h, fps, level, belowFloor) }
            }
            if !deep {
                record()
                if bytes(cur) <= limit || !req.autoFit { return try finish(best!) }
            }

            let atFloor = fps <= minFps && width <= minWidth
            if gifsiclePath != nil && (req.priority == .keepSize || atFloor || deep) {
                // Jump to the mildest step expected to fit (with a little margin); if none is
                // expected to, go straight to the strongest to learn how far off we are.
                let need = Double(limit) * 0.97 / Double(max(bytes(cur), 1))
                let guess = (squeezeFactors.firstIndex { $0 <= need } ?? (squeezeFactors.count - 1)) + 1
                let first = deep ? deepFirst : max(guess, lastLevel > 0 ? lastLevel - 1 : 1)
                try await prepare((first...squeezeSteps.count).map { squeezeSteps[$0 - 1].colors })
                // gifsicle is single-threaded: run the candidate levels side by side, then take
                // the mildest one that fits.
                let levels = Array(first...squeezeSteps.count)
                await onProgress("Vượt giới hạn — đang nén thêm (giữ \(w) px · \(fps) fps)…")
                let jobs = levels.map { (level: $0, src: bases[squeezeSteps[$0 - 1].colors]!, out: tmp()) }
                if let gs = gifsiclePath {
                    try await withThrowingTaskGroup(of: Void.self) { g in
                        for j in jobs {
                            let args = ["-O2", "--no-warnings", "--lossy=\(squeezeSteps[j.level - 1].lossy)",
                                        "-o", j.out.path, j.src.path]
                            g.addTask { try await run(gs, args) }
                        }
                        try await g.waitForAll()
                    }
                }
                for j in jobs {
                    cur = bytes(j.out) > 0 && bytes(j.out) < bytes(j.src) ? j.out : j.src
                    level = j.level
                    lastLevel = j.level
                    record()
                    if bytes(cur) <= limit { return try finish(best!) }
                }
            }

            // Lower fps first, then width — never below the floors.
            let curSize = Double(bytes(cur))
            var remaining = Double(limit) * 0.95 / max(curSize, 1)
            let oldW = width, oldFps = fps
            if fps > minFps {
                let nf = max(minFps, Int((Double(fps) * remaining.squareRoot()).rounded(.down)))
                remaining *= Double(fps) / Double(max(nf, 1))
                fps = nf
            }
            if remaining < 1 && width > minWidth {
                width = max(minWidth, Int(Double(width) * remaining.squareRoot()) / 2 * 2)
            }
            if width == oldW && fps == oldFps {
                if fps > minFps { fps -= 1 }
                else if width > minWidth { width = max(minWidth, width - 40) }
                else if req.force && (minFps > hardMinFps || minWidth > hardMinWidth) {
                    // At the user's floors and fully compressed: lower both a bit more, evenly.
                    minFps = min(minFps, hardMinFps)
                    minWidth = min(minWidth, hardMinWidth)
                    belowFloor = true
                    let r = (Double(limit) * 0.95 / max(curSize, 1)).squareRoot()
                    fps = max(minFps, min(fps - 1, Int((Double(fps) * r.squareRoot()).rounded(.down))))
                    width = max(minWidth, min(width - 20, Int(Double(width) * r.squareRoot()) / 2 * 2))
                } else { return try finish(best!) }    // at the floors and fully compressed
            }
            if attempt >= (req.force ? 12 : 8) { return try finish(best!) }
        }
    }

    /// Stable key for per-file caches (path + size + modification date).
    static func cacheKey(for src: URL) -> String {
        let attrs = try? FileManager.default.attributesOfItem(atPath: src.path)
        let key = "\(src.path)|\(attrs?[.size] ?? 0)|\((attrs?[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0)"
        return SHA256.hash(data: Data(key.utf8)).prefix(10).map { String(format: "%02x", $0) }.joined()
    }

    /// H.264 preview proxy for sources AVFoundation can't decode (e.g. AV1 on M1/M2).
    /// Cached in ~/Library/Caches/VNEGifMaker/proxies. Export still uses the original.
    static func makeProxy(for src: URL) async throws -> URL {
        guard let ffmpeg = ffmpegPath else { throw EncodeError.noFFmpeg }
        let hash = cacheKey(for: src)
        let dir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("VNEGifMaker/proxies", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let out = dir.appendingPathComponent("\(hash).mp4")
        if FileManager.default.fileExists(atPath: out.path) { return out }
        let tmp = dir.appendingPathComponent("\(hash).part.mp4")
        try await run(ffmpeg, [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", src.path,
            "-map", "0:v:0",
            "-vf", "scale=1280:1280:force_original_aspect_ratio=decrease:force_divisible_by=2",
            "-c:v", "h264_videotoolbox", "-b:v", "6M", "-g", "15", "-an", tmp.path,
        ])
        try FileManager.default.moveItem(at: tmp, to: out)
        return out
    }

    private final class Box: @unchecked Sendable {
        var data = Data()
        let lock = NSLock()
        func append(_ d: Data) { lock.lock(); data.append(d); lock.unlock() }
    }

    static func run(_ path: String, _ args: [String]) async throws {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: path)
        p.arguments = args
        let err = Pipe()
        p.standardError = err
        p.standardOutput = FileHandle.nullDevice
        let box = Box()
        err.fileHandleForReading.readabilityHandler = { h in box.append(h.availableData) }

        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
                p.terminationHandler = { proc in
                    err.fileHandleForReading.readabilityHandler = nil
                    box.append(err.fileHandleForReading.readDataToEndOfFile())
                    if proc.terminationStatus == 0 {
                        c.resume()
                    } else if proc.terminationReason == .uncaughtSignal {
                        c.resume(throwing: CancellationError())
                    } else {
                        let msg = String(data: box.data, encoding: .utf8)?
                            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                        c.resume(throwing: EncodeError.ffmpeg(String(msg.suffix(400))))
                    }
                }
                do { try p.run() } catch { c.resume(throwing: error) }
            }
        } onCancel: {
            if p.isRunning { p.terminate() }
        }
    }
}
