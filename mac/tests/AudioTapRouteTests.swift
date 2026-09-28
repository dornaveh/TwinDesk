@main enum AudioTapRouteTests {
    static func main() {
        let route = AudioTapRoute(outputID: 10, outputUID: "speaker", tapUID: "tap-1", aggregateUID: "aggregate-1")
        precondition(route.matches(outputID: 10, outputUID: "speaker", tapUID: "tap-1", aggregateUID: "aggregate-1"))
        // Default output selection changed, including re-enumeration of the same UID.
        precondition(!route.matches(outputID: 20, outputUID: "hdmi", tapUID: "tap-1", aggregateUID: "aggregate-1"))
        precondition(!route.matches(outputID: 20, outputUID: "speaker", tapUID: "tap-1", aggregateUID: "aggregate-1"))
        // Core Audio restarted: missing objects and recycled IDs must rebuild.
        precondition(!route.matches(outputID: 10, outputUID: "speaker", tapUID: nil, aggregateUID: nil))
        precondition(!route.matches(outputID: 10, outputUID: "speaker", tapUID: "tap-2", aggregateUID: "aggregate-1"))
        precondition(!route.matches(outputID: 10, outputUID: "speaker", tapUID: "tap-1", aggregateUID: "aggregate-2"))
        precondition(!route.matches(outputID: 10, outputUID: "other", tapUID: "tap-1", aggregateUID: "aggregate-1"))
        print("Audio route reset, output change and ID reuse tests passed.")
    }
}
