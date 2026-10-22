import CoreGraphics
import Foundation

/// A committed brush stroke, in upright image coordinates (bottom-left, 0...1).
struct RetouchStroke: Sendable, Equatable {
    var points: [CGPoint]
    var diameter: Double // fraction of the image's longer side
}

/// Original exemplar synthesis, informed by Barnes et al. (2009), randomized patch correspondence, and
/// Wexler et al. (2007), coarse-to-fine completion. No implementation or assets from another project are used.
/// Only fully known patches may donate pixels. A small pyramid supplies a weak guide in large missing areas;
/// the fine pass matches the real boundary and copies texture, rather than averaging its colours away.
enum Retouch {
    struct Raster {
        let width: Int
        let height: Int
        var bytes: [UInt8] // top-down premultiplied RGBA

        init(_ image: CGImage) throws {
            width = image.width; height = image.height
            bytes = [UInt8](repeating: 0, count: width * height * 4)
            let ok = bytes.withUnsafeMutableBytes { buffer -> Bool in
                guard let ctx = CGContext(data: buffer.baseAddress, width: width, height: height, bitsPerComponent: 8,
                                          bytesPerRow: width * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return false }
                ctx.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
                return true
            }
            guard ok else { throw ToolError(L("Couldn't repair this area.")) }
        }

        init(width: Int, height: Int, bytes: [UInt8]) {
            self.width = width; self.height = height; self.bytes = bytes
        }

        func image() throws -> CGImage {
            guard let provider = CGDataProvider(data: Data(bytes) as CFData),
                  let image = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32,
                                      bytesPerRow: width * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
                                      provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)
            else { throw ToolError(L("Couldn't repair this area.")) }
            return image
        }
    }

    /// A cancellation predicate is also used by previews: a new stroke invalidates even an in-flight synthesis.
    static func render(_ image: CGImage, strokes: [RetouchStroke], cancelled: () -> Bool = { false }) throws -> CGImage {
        guard !strokes.isEmpty else { return image }
        let w = image.width, h = image.height
        let side = Double(max(w, h))
        var area = CGRect.null
        for stroke in strokes where stroke.diameter.isFinite && stroke.diameter > 0 {
            let radius = stroke.diameter * side / 2
            for p in stroke.points where p.x.isFinite && p.y.isFinite {
                area = area.union(CGRect(x: p.x * Double(w) - radius, y: (1 - p.y) * Double(h) - radius,
                                         width: radius * 2 + 1, height: radius * 2 + 1))
            }
        }
        guard !area.isNull else { return image }
        // Decode/composite once; the expensive search never visits the rest of the photograph.
        let margin = min(160.0, max(32, max(area.width, area.height) * 0.6))
        let roi = area.insetBy(dx: -margin, dy: -margin).integral.intersection(CGRect(x: 0, y: 0, width: w, height: h))
        guard !roi.isEmpty, let crop = image.cropping(to: roi) else { return image }
        let raster = try Raster(crop)
        let rw = raster.width, rh = raster.height
        var mask = [Bool](repeating: false, count: rw * rh)
        for stroke in strokes where stroke.diameter.isFinite && stroke.diameter > 0 {
            let r = max(0.5, stroke.diameter * side / 2)
            let points = stroke.points.filter { $0.x.isFinite && $0.y.isFinite }.map {
                CGPoint(x: $0.x * Double(w) - roi.minX, y: (1 - $0.y) * Double(h) - roi.minY)
            }
            for (i, b) in points.enumerated() {
                let a = points[max(0, i - 1)]
                let box = CGRect(x: min(a.x, b.x) - r, y: min(a.y, b.y) - r,
                                 width: abs(b.x - a.x) + 2 * r + 1, height: abs(b.y - a.y) + 2 * r + 1)
                    .integral.intersection(CGRect(x: 0, y: 0, width: rw, height: rh))
                guard !box.isEmpty else { continue }
                let dx = b.x - a.x, dy = b.y - a.y, length = dx * dx + dy * dy
                for y in Int(box.minY)..<Int(box.maxY) {
                    for x in Int(box.minX)..<Int(box.maxX) {
                        let px = Double(x) + 0.5, py = Double(y) + 0.5
                        let t = length > 0 ? min(1, max(0, ((px - a.x) * dx + (py - a.y) * dy) / length)) : 0
                        if pow(px - a.x - t * dx, 2) + pow(py - a.y - t * dy, 2) <= r * r { mask[y * rw + x] = true }
                    }
                }
            }
        }
        let repaired = try complete(raster, mask: mask, cancelled: cancelled).image()
        if cancelled() { throw Cancelled() }
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw ToolError(L("Couldn't repair this area."))
        }
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        ctx.setBlendMode(.copy)
        ctx.draw(repaired, in: CGRect(x: roi.minX, y: Double(h) - roi.maxY, width: roi.width, height: roi.height))
        guard let result = ctx.makeImage() else { throw ToolError(L("Couldn't repair this area.")) }
        return result
    }

    static func save(_ source: URL, strokes: [RetouchStroke]) throws -> URL {
        guard !strokes.isEmpty else { throw ToolError(L("Nothing to retouch. Paint over an object first.")) }
        let result = try render(Images.load(source), strokes: strokes, cancelled: { Task.isCancelled })
        try Task.checkCancellation()
        let (type, ext) = Images.writableType(for: source)
        return try Images.save(result, as: Paths.preferred(for: source, ext: ext, suffix: " retouched"), type: type, quality: 0.95)
    }

    /// Boundary-first patch completion. The mask is immutable for donor validation; newly completed pixels can
    /// guide a match but can never become source material. Thus the object itself cannot leak back into the hole.
    static func complete(_ input: Raster, mask: [Bool], cancelled: () -> Bool = { false }) throws -> Raster {
        let w = input.width, h = input.height
        guard mask.count == w * h else { throw ToolError(L("Couldn't repair this area.")) }
        guard mask.contains(true) else { return input }
        let known = mask.indices.filter { !mask[$0] }
        guard !known.isEmpty else { throw ToolError(L("Leave some background unpainted to repair this area.")) }
        if cancelled() { throw Cancelled() }
        var guide: Raster?
        if max(w, h) > 180, min(w, h) > 16 {
            let nw = max(1, (w + 1) / 2), nh = max(1, (h + 1) / 2)
            var b = [UInt8](repeating: 0, count: nw * nh * 4)
            var m = [Bool](repeating: false, count: nw * nh)
            for y in 0..<nh { for x in 0..<nw {
                var sum = [Int](repeating: 0, count: 4), count = 0
                for yy in (y * 2)..<min(h, y * 2 + 2) { for xx in (x * 2)..<min(w, x * 2 + 2) {
                    let p = yy * w + xx
                    if mask[p] { m[y * nw + x] = true; continue }
                    count += 1
                    for c in 0..<4 { sum[c] += Int(input.bytes[p * 4 + c]) }
                } }
                if count > 0 { for c in 0..<4 { b[(y * nw + x) * 4 + c] = UInt8(sum[c] / count) } }
            } }
            // At very high coverage a coarse mask can swallow every donor. Keep the fine-level boundary instead.
            if m.contains(false) { guide = try complete(Raster(width: nw, height: nh, bytes: b), mask: m, cancelled: cancelled) }
        }
        var output = input
        var filled = mask.map { !$0 }
        var donor = [Int](repeating: -1, count: w * h)
        var random: UInt64 = 0x8e73c9b5
        func next(_ upper: Int) -> Int {
            random = random &* 6364136223846793005 &+ 1442695040888963407
            return Int((random >> 32) % UInt64(max(1, upper)))
        }
        // Summed mask makes "is this entire source patch known?" an O(1) check.
        var integral = [Int](repeating: 0, count: (w + 1) * (h + 1))
        for y in 0..<h { var row = 0; for x in 0..<w {
            row += mask[y * w + x] ? 1 : 0
            integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + row
        } }
        var radius = min(3, (min(w, h) - 1) / 2)
        var sources: [Int] = []
        while true {
            for p in known {
                let x = p % w, y = p / w
                guard x >= radius, x + radius < w, y >= radius, y + radius < h else { continue }
                let a = (y - radius) * (w + 1), b = (y + radius + 1) * (w + 1)
                let l = x - radius, r = x + radius + 1
                if integral[b + r] - integral[b + l] - integral[a + r] + integral[a + l] == 0 { sources.append(p) }
            }
            if !sources.isEmpty || radius == 0 { break }
            radius -= 1
        }
        // Queue each boundary once. A copied patch reveals more of the frontier; no full-image repeated scans.
        var queue: [Int] = [], enqueued = [Bool](repeating: false, count: w * h)
        func neighbours(_ p: Int) -> [Int] {
            let x = p % w, y = p / w
            return [(x > 0 ? p - 1 : -1), (x + 1 < w ? p + 1 : -1),
                    (y > 0 ? p - w : -1), (y + 1 < h ? p + w : -1)].filter { $0 >= 0 }
        }
        for p in mask.indices where mask[p] && neighbours(p).contains(where: { filled[$0] }) {
            queue.append(p); enqueued[p] = true
        }
        var head = 0
        while head < queue.count {
            if head % 32 == 0, cancelled() { throw Cancelled() }
            let p = queue[head]; head += 1
            if filled[p] { continue }
            let x = p % w, y = p / w
            // Match only known context plus a low-weight coarse guide. Never compare to the erased object.
            var context: [(Int, Int, Double, [UInt8])] = []
            for dy in -radius...radius { for dx in -radius...radius {
                let tx = x + dx, ty = y + dy
                guard tx >= 0, tx < w, ty >= 0, ty < h else { continue }
                let t = ty * w + tx
                if filled[t] {
                    context.append((dx, dy, mask[t] ? 0.6 : 1, Array(output.bytes[(t * 4)..<(t * 4 + 4)])))
                } else if let guide {
                    let g = ((ty / 2) * guide.width + tx / 2) * 4
                    context.append((dx, dy, 0.03, Array(guide.bytes[g..<(g + 4)])))
                }
            } }
            var best = sources[next(sources.count)], score = Double.infinity
            func consider(_ q: Int) {
                guard q >= 0, q < w * h else { return }
                let sx = q % w, sy = q / w
                guard sx >= radius, sx + radius < w, sy >= radius, sy + radius < h else { return }
                let a = (sy - radius) * (w + 1), b = (sy + radius + 1) * (w + 1)
                let l = sx - radius, r = sx + radius + 1
                guard integral[b + r] - integral[b + l] - integral[a + r] + integral[a + l] == 0 else { return }
                var distance = 0.0
                for (dx, dy, weight, value) in context {
                    let j = ((sy + dy) * w + sx + dx) * 4
                    for c in 0..<4 { let d = Double(Int(input.bytes[j + c]) - Int(value[c])); distance += weight * d * d }
                    if distance >= score { return }
                }
                if distance < score { score = distance; best = q }
            }
            // Spatial propagation preserves a stripe/grid phase across an advancing frontier.
            for n in neighbours(p) where filled[n] {
                if donor[n] >= 0 { consider(donor[n] + p - n) }
            }
            for _ in 0..<64 { consider(sources[next(sources.count)]) }
            var reach = max(w, h)
            while reach > 0 {
                let bx = best % w, by = best / w
                let sx = min(w - 1, max(0, bx + next(2 * reach + 1) - reach))
                let sy = min(h - 1, max(0, by + next(2 * reach + 1) - reach))
                consider(sy * w + sx)
                reach /= 2
            }
            // Copy a small patch, retaining every originally unmasked pixel verbatim.
            let copyRadius = max(0, radius - 1)
            for dy in -copyRadius...copyRadius { for dx in -copyRadius...copyRadius {
                let tx = x + dx, ty = y + dy
                guard tx >= 0, tx < w, ty >= 0, ty < h else { continue }
                let t = ty * w + tx
                guard !filled[t] else { continue }
                let s = best + dy * w + dx
                for c in 0..<4 { output.bytes[t * 4 + c] = input.bytes[s * 4 + c] }
                filled[t] = true; donor[t] = s
                for n in neighbours(t) where !filled[n] && !enqueued[n] { queue.append(n); enqueued[n] = true }
            } }
        }
        return output
    }
}
