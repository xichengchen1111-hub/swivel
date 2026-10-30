import AppKit

/// Session-only measurements. Workers report in place; the window samples them without rebuilding its results.
final class DoctorProgress: @unchecked Sendable {
    @TaskLocal static var current: DoctorProgress?
    struct Snapshot {
        var title: String
        var completed: Int
        var total: Int
        var bytes: Bool
        var scanned: Int
        var elapsed: Double
        var fraction: Double?
        var remaining: Double?
    }
    private let lock = NSLock()
    private let started: Double
    private var stageStarted: Double
    private var lastAdvance: Double
    private var title: String
    private var estimates = true
    private var byteWork = false
    private var completed = 0, total = 0, items = 0
    let cleaning: Bool

    init(_ title: String, cleaning: Bool = false, now: Double = ProcessInfo.processInfo.systemUptime) {
        self.title = title
        self.cleaning = cleaning
        started = now; stageStarted = now; lastAdvance = now
    }

    func stage(_ title: String, completed: Int = 0, total: Int = 0, bytes: Bool = false, estimate: Bool = true,
               now: Double = ProcessInfo.processInfo.systemUptime) {
        lock.lock(); defer { lock.unlock() }
        if self.title != title || self.total != total || completed < self.completed { stageStarted = now }
        if self.completed != completed || self.title != title { lastAdvance = now }
        self.title = title
        estimates = estimate
        byteWork = bytes
        self.total = max(0, total)
        self.completed = min(max(0, completed), self.total)
    }

    func scanned(_ count: Int) {
        lock.lock(); defer { lock.unlock() }
        items += max(0, count)
    }

    func setScanned(_ count: Int) {
        lock.lock(); defer { lock.unlock() }
        items = max(0, count)
    }

    func snapshot(now: Double = ProcessInfo.processInfo.systemUptime) -> Snapshot {
        lock.lock(); defer { lock.unlock() }
        let elapsed = max(0, now - started), stageTime = max(0, now - stageStarted)
        let fraction = total > 0 ? Double(completed) / Double(total) : nil
        // Small samples and stalled work don't give a useful ETA. Never predict a scan's unknown file count.
        let remaining = estimates && completed >= 2 && completed < total && stageTime >= 3 && now - lastAdvance < 10
            ? stageTime * Double(total - completed) / Double(completed) : nil
        return Snapshot(title: title, completed: completed, total: total, bytes: byteWork, scanned: items, elapsed: elapsed,
                        fraction: fraction, remaining: remaining)
    }

    /// Detached filesystem workers need explicit task-local inheritance, including through nested size scans.
    static func detached<T: Sendable>(priority: TaskPriority = .utility,
                                     operation: @escaping @Sendable () async -> T) -> Task<T, Never> {
        let reporter = current
        return Task.detached(priority: priority) { await $current.withValue(reporter) { await operation() } }
    }
    static func run<T: Sendable>(priority: TaskPriority = .utility,
                                operation: @escaping @Sendable () async -> T) async -> T {
        let task = detached(priority: priority, operation: operation)
        return await withTaskCancellationHandler { await task.value } onCancel: { task.cancel() }
    }

}

@MainActor
final class DoctorProgressView: NSStackView {
    private let bar = NSProgressIndicator()
    private let title = NSTextField(labelWithString: "")
    private let timing = NSTextField(labelWithString: "")
    private let broom = DoctorBroomView(frame: .zero)
    private var refreshTask: Task<Void, Never>?
    var activity: (() -> DoctorProgress?)?

    init() {
        super.init(frame: .zero)
        orientation = .vertical; alignment = .leading; spacing = 5
        title.font = .systemFont(ofSize: 12, weight: .medium)
        title.lineBreakMode = .byTruncatingTail
        timing.font = .monospacedDigitSystemFont(ofSize: 11, weight: .regular)
        timing.textColor = .secondaryLabelColor
        timing.lineBreakMode = .byTruncatingTail
        bar.style = .bar; bar.controlSize = .small; bar.maxValue = 1
        bar.setAccessibilityLabel(L("Progress"))
        broom.widthAnchor.constraint(equalToConstant: 32).isActive = true
        broom.heightAnchor.constraint(equalToConstant: 30).isActive = true
        let labels = ToolUI.stack([title, timing], spacing: 3)
        let row = ToolUI.stack([broom, labels], vertical: false, spacing: 8)
        addArrangedSubview(row); addArrangedSubview(bar)
        row.widthAnchor.constraint(equalTo: widthAnchor).isActive = true
        labels.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        bar.widthAnchor.constraint(equalTo: widthAnchor).isActive = true
        isHidden = true
    }
    required init?(coder: NSCoder) { fatalError() }

    func refresh() {
        guard let work = activity?(), window?.isVisible == true, window?.isMiniaturized == false else {
            isHidden = true; bar.stopAnimation(nil); broom.setActive(false)
            refreshTask?.cancel(); refreshTask = nil
            return
        }
        isHidden = false
        let state = work.snapshot()
        let counts = state.bytes ? DoctorFormat.bytes(Int64(state.completed)) + " / " + DoctorFormat.bytes(Int64(state.total))
            : "\(state.completed)/\(state.total)"
        title.stringValue = L(state.title) + (state.total > 0 ? " · " + counts : "")
        var parts = [L("Elapsed %@", Self.duration(state.elapsed))]
        if let remaining = state.remaining {
            parts.append(L("About %@ remaining in this step", Self.duration(remaining)))
        } else {
            parts.append(state.total == 0 ? L("Remaining time is not yet known") : L("Estimating remaining time…"))
        }
        if state.scanned > 0 { parts.append(L("%d entries scanned", state.scanned)) }
        timing.stringValue = parts.joined(separator: " · ")
        timing.toolTip = timing.stringValue
        title.toolTip = title.stringValue
        bar.isIndeterminate = state.fraction == nil
        if let fraction = state.fraction { bar.stopAnimation(nil); bar.doubleValue = fraction }
        else { bar.startAnimation(nil) }
        bar.setAccessibilityValue(state.fraction.map { L("%d%%", Int($0 * 100)) } ?? L("Working…"))
        broom.isHidden = !work.cleaning
        broom.setActive(work.cleaning)
        guard refreshTask == nil else { return }
        refreshTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(250))
                guard !Task.isCancelled, let self else { return }
                self.refresh()
            }
        }
    }

    func stop() { refreshTask?.cancel(); refreshTask = nil; broom.setActive(false); bar.stopAnimation(nil) }

    override func viewDidMoveToWindow() { super.viewDidMoveToWindow(); refresh() }
    static func duration(_ seconds: Double) -> String {
        let seconds = Int(min(Double(Int.max / 2), max(0, seconds.rounded(.up))))
        return seconds < 60 ? L("%d sec", seconds) : L("%d min %d sec", seconds / 60, seconds % 60)
    }
}

/// A small monochrome broom, drawn like a toolbar glyph. Gentle, damped sweeps use the app's frame driver.
@MainActor
private final class DoctorBroomView: NSView {
    private lazy var driver = FrameDriver(view: self)
    private var sweep = Spring(-1, response: 1.1, damping: 1)
    private var active = false
    func setActive(_ active: Bool) {
        self.active = active
        guard active, !Motion.reduced, window?.isVisible == true else {
            driver.stop(); sweep.snap(0); needsDisplay = true; return
        }
        driver.onFrame = { [weak self] dt in
            guard let self, self.active, !Motion.reduced, self.window?.isVisible == true,
                  self.window?.isMiniaturized == false else { return false }
            if self.sweep.isSettled { self.sweep.target = self.sweep.target > 0 ? -1 : 1 }
            self.sweep.step(dt); self.needsDisplay = true
            return true
        }
        driver.start()
    }
    override func draw(_ dirtyRect: NSRect) {
        NSGraphicsContext.saveGraphicsState(); defer { NSGraphicsContext.restoreGraphicsState() }
        let transform = AffineTransform(translationByX: bounds.midX + sweep.value * 4, byY: bounds.midY)
        (transform as NSAffineTransform).concat()
        let rotation = NSAffineTransform(); rotation.rotate(byDegrees: -22 + sweep.value * 8); rotation.concat()
        NSColor.controlAccentColor.setStroke()
        let handle = NSBezierPath(); handle.lineWidth = 2.2; handle.lineCapStyle = .round
        handle.move(to: NSPoint(x: 0, y: -1)); handle.line(to: NSPoint(x: 0, y: 12)); handle.stroke()
        let head = NSBezierPath(); head.lineWidth = 1.5; head.lineJoinStyle = .round
        head.move(to: NSPoint(x: -3, y: -1)); head.line(to: NSPoint(x: 3, y: -1))
        head.line(to: NSPoint(x: 8, y: -11)); head.line(to: NSPoint(x: -8, y: -11)); head.close(); head.stroke()
        for x in [-3.0, 0, 3] {
            let bristle = NSBezierPath(); bristle.lineWidth = 1
            bristle.move(to: NSPoint(x: x / 2, y: -4)); bristle.line(to: NSPoint(x: x, y: -10)); bristle.stroke()
        }
    }
}
