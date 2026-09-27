import Foundation
import Network
import CryptoKit
import Security

enum BridgeError: LocalizedError {
    case message(String)
    var errorDescription: String? { if case let .message(text) = self { return text }; return nil }
}
struct Pairing: Codable {
    let version: Int
    let host: String
    let port: UInt16
    let token: String
    let fingerprint: String
    static func parse(_ code: String) throws -> Pairing {
        guard let bytes = Data(base64Encoded: code.trimmingCharacters(in: .whitespacesAndNewlines)), bytes.count < 2048 else { throw BridgeError.message("Paste a complete pairing code from the Windows app.") }
        let value = try JSONDecoder().decode(Pairing.self, from: bytes)
        guard value.version == 1, IPv4Address(value.host) != nil, value.port > 0,
              value.token.range(of: #"^[0-9A-Fa-f]{64}$"#, options: .regularExpression) != nil,
              value.fingerprint.range(of: #"^[0-9A-Fa-f]{64}$"#, options: .regularExpression) != nil else { throw BridgeError.message("This pairing code is not supported.") }
        return value
    }
}

enum PairingStore {
    private static let base: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "TwinDesk", kSecAttrAccount as String: "paired-pc"]
    static func load() -> String {
        var query = base; query[kSecReturnData as String] = true; query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let bytes = result as? Data else { return "" }
        return String(data: bytes, encoding: .utf8) ?? ""
    }
    static func save(_ code: String) throws {
        let changes = [kSecValueData as String: Data(code.utf8)]
        var status = SecItemUpdate(base as CFDictionary, changes as CFDictionary)
        if status == errSecItemNotFound {
            var item = base; changes.forEach { item[$0.key] = $0.value }
            item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            status = SecItemAdd(item as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw BridgeError.message("Could not save pairing in the Mac Keychain.") }
    }
}

final class Connection {
    let queue = DispatchQueue(label: "TwinDesk.link", qos: .userInteractive)
    var report: ((String) -> Void)?
    var state: ((Bool) -> Void)?
    private var connection: NWConnection?
    private var timer: DispatchSourceTimer?
    private var pairing: Pairing?
    private var routes = [DisplayRoute]()
    private let input = InputReceiver()
    private var receiving = Data()
    private var wanted = false
    private var ready = false
    private var supportsReturnToWindows = false
    private var returnRequest: String?
    private var restoreDisplays = false
    private var recovering = false
    private var lastPacket = ProcessInfo.processInfo.systemUptime
    private var queuedAudio = 0

    func start(_ pairing: Pairing, routes: [DisplayRoute]) {
        queue.async {
            guard !self.wanted else { return }
            self.pairing = pairing; self.routes = routes; self.wanted = true; self.connect()
        }
    }
    func stop() { queue.async { self.wanted = false; self.end("Disconnected.") } }
    func returnToWindows() {
        queue.async {
            guard self.ready else { self.report?("Connect to the PC before switching back to Windows."); return }
            guard self.supportsReturnToWindows else { self.report?("Update TwinDesk on Windows to use the Mac return menu."); return }
            guard self.returnRequest == nil else { return }
            let id = UUID().uuidString
            self.returnRequest = id
            self.report?("Requesting control back on Windows…")
            self.sendJSON(3, ["id": id, "name": "returnToWindows"])
            self.queue.asyncAfter(deadline: .now() + 8) {
                guard self.returnRequest == id else { return }
                self.returnRequest = nil
                self.report?("Windows did not acknowledge the return request. Try again.")
            }
        }
    }
    private func connect() {
        guard wanted, connection == nil, let pairing else { return }
        if recovering { queue.asyncAfter(deadline: .now() + 2) { self.connect() }; return }
        let tls = NWProtocolTLS.Options()
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
        sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, trust, complete in
            let ref = sec_trust_copy_ref(trust).takeRetainedValue()
            guard let chain = SecTrustCopyCertificateChain(ref) as? [SecCertificate], let certificate = chain.first else { complete(false); return }
            let hash = SHA256.hash(data: SecCertificateCopyData(certificate) as Data).map { String(format: "%02X", $0) }.joined()
            // Trust only the exact certificate fingerprint copied from this PC.
            complete(hash == pairing.fingerprint.uppercased())
        }, queue)
        let tcp = NWProtocolTCP.Options(); tcp.noDelay = true
        let parameters = NWParameters(tls: tls, tcp: tcp)
        let link = NWConnection(host: NWEndpoint.Host(pairing.host), port: NWEndpoint.Port(rawValue: pairing.port)!, using: parameters)
        connection = link; receiving.removeAll(); ready = false; queuedAudio = 0
        link.stateUpdateHandler = { [weak self, weak link] status in
            guard let self, let link, self.connection === link else { return }
            switch status {
            case .ready:
                self.lastPacket = ProcessInfo.processInfo.systemUptime
                self.sendJSON(1, ["version": 1, "token": pairing.token]); self.receive(link); self.startTimer()
            case .failed: self.end("Connection failed. Check the PC address and Ethernet connection.")
            case .waiting: self.end("PC not reachable. Reconnecting…")
            default: break
            }
        }
        link.start(queue: queue)
        queue.asyncAfter(deadline: .now() + 10) { [weak self, weak link] in
            guard let self, let link, self.connection === link, !self.ready else { return }
            self.end("Pairing timed out. Check that TwinDesk is running on the PC.")
        }
    }
    private func startTimer() {
        timer?.cancel(); let t = DispatchSource.makeTimerSource(queue: queue); timer = t
        t.schedule(deadline: .now() + 2, repeating: 2)
        t.setEventHandler { [weak self] in
            guard let self else { return }
            if ProcessInfo.processInfo.systemUptime - self.lastPacket > 8 { self.end("PC connection lost. Returning displays to Windows."); return }
            if self.ready { self.send(5, Data()) }
        }; t.resume()
    }
    private func receive(_ link: NWConnection) {
        link.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self, weak link] bytes, _, done, error in
            guard let self, let link, self.connection === link else { return }
            do {
                if let bytes { self.receiving.append(bytes) }
                while self.receiving.count >= 4 {
                    let length = self.receiving.prefix(4).reduce(0) { ($0 << 8) | Int($1) }
                    guard length >= 1 && length <= 65536 else { throw BridgeError.message("Invalid network frame.") }
                    if self.receiving.count < length + 4 { break }
                    let packet = self.receiving.subdata(in: 4..<(length + 4)); self.receiving = Data(self.receiving.dropFirst(length + 4))
                    self.lastPacket = ProcessInfo.processInfo.systemUptime
                    try self.handle(packet.first!, Data(packet.dropFirst()))
                }
                if done || error != nil { self.end("PC disconnected. Returning control to Windows.") } else { self.receive(link) }
            } catch { self.end(error.localizedDescription) }
        }
    }
    private func handle(_ type: UInt8, _ data: Data) throws {
        if type == 6 && !ready {
            guard let p = try JSONSerialization.jsonObject(with: data) as? [String: Any], p["version"] as? Int == 1, p["sampleRate"] as? Int == 48000, p["channels"] as? Int == 2, p["bits"] as? Int == 16 else { throw BridgeError.message("Unsupported audio protocol.") }
            supportsReturnToWindows = p["supportsReturnToWindows"] as? Bool == true
            ready = true; report?("Connected to the PC · encrypted"); state?(true); return
        }
        guard ready else { throw BridgeError.message("Pairing is not complete.") }
        switch type {
        case 5: break
        case 7: try input.handle(data)
        case 4:
            guard let p = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let id = p["id"] as? String, let ok = p["ok"] as? Bool else { throw BridgeError.message("Invalid command reply.") }
            guard id == returnRequest else { return }
            returnRequest = nil
            if !ok { report?(p["error"] as? String ?? "Windows could not accept the return request.") }
        case 3:
            guard let p = try JSONSerialization.jsonObject(with: data) as? [String: String], let id = p["id"], id.count <= 64, let name = p["name"] else { throw BridgeError.message("Invalid command.") }
            if recovering { reply(id, false, "Monitor recovery is still running."); return }
            switch name {
            case "activate", "activateWithDisplays":
                do {
                    if name == "activateWithDisplays" && routes.count != 2 { throw BridgeError.message("Refresh monitors in the Mac app; both monitors must be connected.") }
                    try input.activate(); restoreDisplays = name == "activateWithDisplays"
                    reply(id, true); report?("This Mac is controlled by your PC keyboard and mouse.")
                } catch { reply(id, false, error.localizedDescription) }
            case "deactivate":
                input.releaseAll()
                recover { [weak self] error in self?.reply(id, error == nil, error ?? ""); self?.report?("Control returned to PC. Audio continues.") }
            default: reply(id, false, "Unsupported command.")
            }
        default: throw BridgeError.message("Unexpected network packet.")
        }
    }
    private func reply(_ id: String, _ ok: Bool, _ error: String = "") { sendJSON(4, ["id": id, "ok": ok, "error": error]) }
    private func sendJSON(_ type: UInt8, _ object: [String: Any]) { if let data = try? JSONSerialization.data(withJSONObject: object) { send(type, data) } }
    private func send(_ type: UInt8, _ data: Data) {
        guard let connection, data.count < 65536 else { return }
        var length = UInt32(data.count + 1).bigEndian
        var frame = Data(bytes: &length, count: 4); frame.append(type); frame.append(data)
        connection.send(content: frame, completion: .contentProcessed { [weak self, weak connection] error in
            guard let self, let connection, self.connection === connection else { return }
            if type == 2 { self.queuedAudio = max(0, self.queuedAudio - data.count) }
            if error != nil { self.end("Network send failed.") }
        })
    }
    func sendAudio(_ data: Data) {
        queue.async {
            guard self.ready, self.queuedAudio + data.count <= 19200 else { return }
            self.queuedAudio += data.count; self.send(2, data)
        }
    }
    private func recover(_ completion: @escaping (String?) -> Void) {
        guard restoreDisplays else { completion(nil); return }
        restoreDisplays = false; recovering = true; let saved = routes
        DispatchQueue.global(qos: .userInitiated).async {
            var failure: String?
            do { try Displays.restore(saved) } catch { failure = error.localizedDescription }
            self.queue.async { self.recovering = false; completion(failure) }
        }
    }
    private func end(_ message: String) {
        let old = connection; connection = nil; old?.cancel(); timer?.cancel(); timer = nil
        ready = false; supportsReturnToWindows = false; returnRequest = nil
        input.releaseAll(); state?(false); report?(message)
        recover { [weak self] error in
            guard let self else { return }
            if let error { self.report?(error) }
            if self.wanted { self.queue.asyncAfter(deadline: .now() + 3) { self.connect() } }
        }
    }
}
