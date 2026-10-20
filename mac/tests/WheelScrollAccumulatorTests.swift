@main struct WheelScrollAccumulatorTests {
    static func main() {
        var wheel = WheelScrollAccumulator()
        func check(_ actual: Int32, _ expected: Int32, _ message: String) {
            precondition(actual == expected, "\(message): expected \(expected), got \(actual)")
        }
        check(wheel.lines(delta: 120, horizontal: false), 3, "Normal wheel notch")
        check(wheel.lines(delta: -120, horizontal: false), -3, "Reverse wheel notch")
        var total: Int32 = 0
        for _ in 0..<120 { total += wheel.lines(delta: 1, horizontal: false) }
        check(total, 3, "One-unit events must preserve a full notch")
        total = 0
        for _ in 0..<8 { total += wheel.lines(delta: -15, horizontal: false) }
        check(total, -3, "Small negative events must preserve a full notch")
        check(wheel.lines(delta: 30, horizontal: false), 0, "Partial vertical line")
        check(wheel.lines(delta: 30, horizontal: true), 0, "Axes must not share residuals")
        check(wheel.lines(delta: 10, horizontal: false), 1, "Complete vertical residual")
        check(wheel.lines(delta: 10, horizontal: true), 1, "Complete horizontal residual")
        check(wheel.lines(delta: 39, horizontal: false), 0, "Positive residual")
        check(wheel.lines(delta: -39, horizontal: false), 0, "Opposite movements cancel")
        check(wheel.lines(delta: -1, horizontal: false), 0, "Cancellation leaves no latent movement")
        wheel.reset()
        check(wheel.lines(delta: 39, horizontal: true), 0, "Residual before target change")
        wheel.reset()
        check(wheel.lines(delta: 1, horizontal: true), 0, "Target change clears residuals")
        wheel.reset()
        check(wheel.lines(delta: 32768, horizontal: false), 819, "Largest accepted delta")
        check(wheel.lines(delta: -32768, horizontal: false), -819, "Signed residual preserves net distance")
        check(wheel.lines(delta: 0, horizontal: false), 0, "Zero delta")
        print("Wheel scroll accumulator regression tests passed.")
    }
}
