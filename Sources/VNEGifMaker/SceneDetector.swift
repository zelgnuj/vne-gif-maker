import Foundation

/// Finds scene cuts with ffmpeg's `scdet` filter. Results stream in as they are found
/// and are cached per file in ~/Library/Caches/VNEGifMaker/scenes.
enum SceneDetector {
    static let threshold = 10.0          // scdet score (0–100); lower = more sensitive
    // scdet runs on RGB so cuts between scenes of similar brightness (e.g. red → green) are found.
    static let minGap = 0.25             // drop cuts closer than this (flashes, fades)

    private static var cacheDir: URL {
        FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("VNEGifMaker/scenes", isDirectory: true)
    }

    static func cached(for src: URL) -> [Double]? {
        let u = cacheDir.appendingPathComponent(GifEncoder.cacheKey(for: src) + ".json")
        guard let d = try? Data(contentsOf: u) else { return nil }
        return try? JSONDecoder().decode([Double].self, from: d)
    }

    static func store(_ cuts: [Double], for src: URL) {
        try? FileManager.default.createDirectory(at: cacheDir, withIntermediateDirectories: true)
        let u = cacheDir.appendingPathComponent(GifEncoder.cacheKey(for: src) + ".json")
        if let d = try? JSONEncoder().encode(cuts) { try? d.write(to: u) }
    }

    private final class State: @unchecked Sendable {
        let lock = NSLock()
        var buffer = ""
        var cuts: [Double] = []
        var progress = 0.0
        var lastPush = Date.distantPast
        var stderrTail = ""
    }

    /// Analyzes the whole video. `onUpdate(cuts, progress 0…1)` is called on the main actor.
    static func detect(src: URL, duration: Double,
                       onUpdate: @escaping @MainActor ([Double], Double) -> Void) async throws -> [Double] {
        guard let ffmpeg = GifEncoder.ffmpegPath else { throw EncodeError.noFFmpeg }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: ffmpeg)
        p.arguments = [
            "-hide_banner", "-nostdin", "-loglevel", "info", "-stats_period", "0.5",
            "-i", src.path, "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", "scale=256:-2:flags=fast_bilinear,format=rgb24,scdet=threshold=\(threshold)",
            "-f", "null", "-",
        ]
        p.standardOutput = FileHandle.nullDevice
        let err = Pipe()
        p.standardError = err
        let st = State()

        func push(force: Bool) {
            st.lock.lock()
            let now = Date()
            guard force || now.timeIntervalSince(st.lastPush) > 0.25 else { st.lock.unlock(); return }
            st.lastPush = now
            let cuts = st.cuts, prog = st.progress
            st.lock.unlock()
            DispatchQueue.main.async { MainActor.assumeIsolated { onUpdate(cuts, prog) } }
        }

        func handle(line: String) {
            if let r = line.range(of: "lavfi.scd.time: ") {
                let num = line[r.upperBound...].prefix { "0123456789.".contains($0) }
                if let t = Double(num) {
                    st.lock.lock()
                    if t > minGap, t < duration - 0.05, (st.cuts.last.map { t - $0 >= minGap } ?? true) {
                        st.cuts.append(t)
                    }
                    st.lock.unlock()
                    push(force: true)
                }
            } else if let r = line.range(of: "time=") {
                let s = line[r.upperBound...].prefix { "0123456789:.".contains($0) }
                let parts = s.split(separator: ":").compactMap { Double($0) }
                if parts.count == 3, duration > 0 {
                    st.lock.lock()
                    st.progress = min(1, (parts[0] * 3600 + parts[1] * 60 + parts[2]) / duration)
                    st.lock.unlock()
                    push(force: false)
                }
            }
        }

        err.fileHandleForReading.readabilityHandler = { h in
            let d = h.availableData
            guard !d.isEmpty, let chunk = String(data: d, encoding: .utf8) else { return }
            st.lock.lock()
            st.buffer += chunk
            st.stderrTail = String((st.stderrTail + chunk).suffix(600))
            var lines: [String] = []
            while let i = st.buffer.firstIndex(where: { $0 == "\n" || $0 == "\r" }) {
                lines.append(String(st.buffer[..<i]))
                st.buffer.removeSubrange(...i)
            }
            st.lock.unlock()
            lines.forEach(handle)
        }

        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
                p.terminationHandler = { proc in
                    err.fileHandleForReading.readabilityHandler = nil
                    if proc.terminationStatus == 0 {
                        c.resume()
                    } else if proc.terminationReason == .uncaughtSignal {
                        c.resume(throwing: CancellationError())
                    } else {
                        st.lock.lock(); let tail = st.stderrTail; st.lock.unlock()
                        c.resume(throwing: EncodeError.ffmpeg("Phân tích cảnh thất bại. " + tail))
                    }
                }
                do { try p.run() } catch { c.resume(throwing: error) }
            }
        } onCancel: {
            if p.isRunning { p.terminate() }
        }
        st.lock.lock()
        st.progress = 1
        let cuts = st.cuts
        st.lock.unlock()
        push(force: true)
        return cuts
    }
}
