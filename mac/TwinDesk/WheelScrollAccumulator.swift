// Keep the existing three lines per 120-unit Windows wheel notch, including
// small deltas produced by high-resolution wheels and mouse utilities.
struct WheelScrollAccumulator {
    private var vertical = 0
    private var horizontal = 0

    mutating func lines(delta: Int, horizontal isHorizontal: Bool) -> Int32 {
        if isHorizontal {
            horizontal += delta
            let lines = horizontal / 40
            horizontal %= 40
            return Int32(lines)
        }
        vertical += delta
        let lines = vertical / 40
        vertical %= 40
        return Int32(lines)
    }

    mutating func reset() {
        vertical = 0
        horizontal = 0
    }
}
