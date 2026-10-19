import Foundation
import CoreGraphics
import IOKit.pwr_mgt

protocol DisplayWakeSystem {
    func preventDisplaySleep() throws -> UInt32
    func declareUserActivity() throws -> UInt32
    func release(_ assertion: UInt32)
    func onlineDisplayIDs() -> Set<UInt32>
    func awakeDisplayIDs() -> Set<UInt32>
}

private struct DisplayWakeError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

private final class MacDisplayWakeSystem: DisplayWakeSystem {
    func preventDisplaySleep() throws -> UInt32 {
        var assertion: IOPMAssertionID = 0
        let result = IOPMAssertionCreateWithName(kIOPMAssertPreventUserIdleDisplaySleep as CFString,
            IOPMAssertionLevel(kIOPMAssertionLevelOn), "TwinDesk active Mac control" as CFString, &assertion)
        guard result == kIOReturnSuccess else {
            throw DisplayWakeError(message: "Could not keep the Mac displays awake (\(result)).")
        }
        return assertion
    }
    func declareUserActivity() throws -> UInt32 {
        var assertion: IOPMAssertionID = 0
        // The user is at the shared physical screens. This wakes their output;
        // it neither posts an input event nor unlocks their macOS session.
        let result = IOPMAssertionDeclareUserActivity("TwinDesk switch to Mac" as CFString,
            kIOPMUserActiveLocal, &assertion)
        guard result == kIOReturnSuccess else {
            throw DisplayWakeError(message: "Could not wake the Mac displays (\(result)).")
        }
        return assertion
    }
    func release(_ assertion: UInt32) { IOPMAssertionRelease(assertion) }
    private func onlineDisplays() -> [CGDirectDisplayID] {
        var displays = [CGDirectDisplayID](repeating: 0, count: 32)
        var count: UInt32 = 0
        guard CGGetOnlineDisplayList(UInt32(displays.count), &displays, &count) == .success else { return [] }
        return Array(displays.prefix(Int(count)))
    }
    func onlineDisplayIDs() -> Set<UInt32> { Set(onlineDisplays()) }
    func awakeDisplayIDs() -> Set<UInt32> {
        Set(onlineDisplays().filter {
            CGDisplayIsOnline($0) != 0 && CGDisplayIsActive($0) != 0 && CGDisplayIsAsleep($0) == 0
        })
    }
}

// All power calls and readiness polling run on a dedicated queue, leaving the
// authenticated connection queue free to process heartbeats and cancellation.
// Assertions cover pending activation, then active input; stop releases both.
final class DisplayWake {
    private let system: DisplayWakeSystem
    private let queue = DispatchQueue(label: "TwinDesk.display-wake", qos: .userInitiated)
    private var generation = 0
    private var displayAssertion: UInt32?
    private var activityAssertion: UInt32?

    init(system: DisplayWakeSystem? = nil) { self.system = system ?? MacDisplayWakeSystem() }

    func activate(minimumDisplays: Int, timeout: TimeInterval = 3,
                  completion: @escaping (Result<Void, Error>) -> Void) {
        queue.async {
            self.generation += 1
            let generation = self.generation
            self.releaseAssertions()
            let deadline = ProcessInfo.processInfo.systemUptime + min(3, max(0, timeout))
            let expectedDisplays = self.system.onlineDisplayIDs()
            do {
                self.displayAssertion = try self.system.preventDisplaySleep()
                self.activityAssertion = try self.system.declareUserActivity()
                self.poll(minimumDisplays: max(1, minimumDisplays), expectedDisplays: expectedDisplays, deadline: deadline,
                    generation: generation, completion: completion)
            } catch {
                self.releaseAssertions()
                completion(.failure(error))
            }
        }
    }

    private func poll(minimumDisplays: Int, expectedDisplays: Set<UInt32>, deadline: TimeInterval, generation: Int,
                      completion: @escaping (Result<Void, Error>) -> Void) {
        guard generation == self.generation else { return }
        let awake = system.awakeDisplayIDs()
        if ProcessInfo.processInfo.systemUptime >= deadline {
            releaseAssertions()
            completion(.failure(DisplayWakeError(message:
                "Mac display outputs did not wake in time. Wake or unlock the Mac, then try again.")))
        } else if awake.count >= minimumDisplays && expectedDisplays.isSubset(of: awake) {
            completion(.success(()))
        } else {
            queue.asyncAfter(deadline: .now() + .milliseconds(100)) {
                self.poll(minimumDisplays: minimumDisplays, expectedDisplays: expectedDisplays, deadline: deadline,
                    generation: generation, completion: completion)
            }
        }
    }

    func stop() {
        queue.async { self.generation += 1; self.releaseAssertions() }
    }
    private func releaseAssertions() {
        if let activityAssertion { system.release(activityAssertion) }
        if let displayAssertion { system.release(displayAssertion) }
        activityAssertion = nil; displayAssertion = nil
    }
    deinit { releaseAssertions() }
}
