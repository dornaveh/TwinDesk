import Foundation

// Queue-confined pixel accumulator. A 120-unit wheel notch becomes 36 pixels.
// Keep subpixel input instead of truncating high-resolution wheel packets.
struct ScrollAxis {
    private var remaining = 0.0
    private var fraction = 0.0
    private var lastStep = 0.0
    private var deadline = 0.0
    private var direction = 0
    var isMoving: Bool { remaining != 0 }

    mutating func add(_ delta: Int, now: Double) {
        guard delta != 0 else { return }
        let sign = delta > 0 ? 1 : -1
        if direction != sign { remaining = 0; fraction = 0 }
        if !isMoving { lastStep = now - 0.008 }
        direction = sign
        remaining += Double(delta) * 0.3
        deadline = now + 0.1
    }

    mutating func step(now: Double) -> Int32 {
        guard isMoving else { return 0 }
        let elapsed = max(0, now - lastStep)
        lastStep = now
        let movement = now >= deadline ? remaining : remaining * (1 - exp(-elapsed / 0.025))
        remaining -= movement
        fraction += movement
        let pixels = fraction.rounded()
        fraction -= pixels
        return Int32(pixels)
    }
}
