import Foundation

struct DisplayRoute: Codable, Identifiable {
    var id: String
    var name: String
    var pcInput: Int = 15
}

enum Displays {
    static func run(_ arguments: [String]) throws -> String {
        let helper = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/display-control")
        guard FileManager.default.isExecutableFile(atPath: helper.path) else { throw BridgeError.message("The built-in monitor component is missing. Rebuild TwinDesk.") }
        let process = Process(); let pipe = Pipe()
        process.executableURL = helper; process.arguments = arguments
        process.standardOutput = pipe; process.standardError = pipe
        let finished = DispatchSemaphore(value: 0)
        process.terminationHandler = { _ in finished.signal() }
        try process.run()
        if finished.wait(timeout: .now() + 6) == .timedOut { process.terminate(); throw BridgeError.message("Monitor command timed out. Use the monitor's input button to return to DisplayPort.") }
        let text = String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        guard process.terminationStatus == 0, !text.lowercased().contains("failure") else { throw BridgeError.message("Monitor command failed: " + String(text.prefix(300))) }
        return text
    }
    static func scan() throws -> [DisplayRoute] {
        let text = try run(["display", "list"])
        let regex = try NSRegularExpression(pattern: #"^\[\d+\] (.*?) \(([0-9A-Fa-f-]{36})\)$"#, options: .anchorsMatchLines)
        let string = text as NSString
        return regex.matches(in: text, range: NSRange(location: 0, length: string.length)).map {
            DisplayRoute(id: string.substring(with: $0.range(at: 2)), name: string.substring(with: $0.range(at: 1)))
        }
    }
    static func restore(_ routes: [DisplayRoute]) throws {
        guard !routes.isEmpty else { throw BridgeError.message("Refresh monitors in the Mac app before enabling display switching.") }
        // Launch both commands together while both displays are still on the Mac.
        let group = DispatchGroup(); let lock = NSLock(); var errors = [String]()
        for route in routes {
            group.enter()
            DispatchQueue.global(qos: .userInitiated).async {
                defer { group.leave() }
                do { _ = try run(["display", route.id, "set", "input", String(route.pcInput)]) }
                catch { lock.lock(); errors.append(error.localizedDescription); lock.unlock() }
            }
        }
        group.wait()
        if !errors.isEmpty { throw BridgeError.message(errors.joined(separator: "; ")) }
    }
}
