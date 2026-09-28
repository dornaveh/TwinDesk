import Foundation
import Network
import CryptoKit
import Security

// One independently authenticated TLS stream per medium. Large video frames never
// share a socket or serial queue with input, call microphone, or speaker audio.
final class CallMediaConnection {
    enum Medium: String { case camera, microphone, speakers }
    let medium: Medium
    var report: ((String) -> Void)?
    private let queue: DispatchQueue
    private let camera = VirtualCamera()
    private let microphone = MicrophoneOutput()
    private let audio = AudioTap()
    private let audioSlots = DispatchSemaphore(value: 4)
    private var connection: NWConnection?
    private var timer: DispatchSourceTimer?
    private var pairing: Pairing?
    private var session = ""
    private var demand = false
    private var wanted = false
    private var ready = false
    private var enabled = false
    private var receiving = Data()
    private var lastPacket = 0.0
    private var lastMedia = 0.0
    private var announced = false
    private var generation = 0
    private var heartbeatPending = false
    private var bootstrapping = false
    private var lastMicrophoneLevelReport = 0.0

    init(_ medium: Medium) {
        self.medium = medium
        queue = DispatchQueue(label: "TwinDesk.\(medium.rawValue)", qos: .userInitiated)
        audio.send = { [weak self] bytes in self?.sendAudio(bytes) }
    }
    func start(_ pairing: Pairing, session: String) {
        queue.async {
            guard session.isEmpty || (session.count == 32 && session.allSatisfy({ $0.isHexDigit })) else { self.report?("PC sent an invalid call session."); return }
            if self.wanted { return }
            self.wanted = false; self.generation += 1; self.close()
            self.pairing = pairing; self.session = ""; self.wanted = true; self.connect()
        }
    }
    func stop() {
        queue.async {
            self.wanted = false; self.generation += 1; self.close(); self.pairing = nil; self.session = ""
            self.report?("\(self.medium.rawValue.capitalized) forwarding is off.")
        }
    }
    func setDemand(_ value: Bool) {
        queue.async {
            guard self.medium != .speakers, self.demand != value else { return }
            self.demand = value
            if !value {
                self.stopOutput(); self.announced = false
                self.report?("\(self.medium.rawValue.capitalized) idle · waiting for a calling app.")
            }
            if self.ready { self.sendDemand() }
        }
    }
    private func sendDemand() {
        guard medium != .speakers else { return }
        send(9, try! JSONSerialization.data(withJSONObject: ["demand": demand]))
    }
    private func connect() {
        guard wanted, connection == nil, let pairing else { return }
        let tls = NWProtocolTLS.Options()
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
        sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, trust, complete in
            let ref = sec_trust_copy_ref(trust).takeRetainedValue()
            guard let chain = SecTrustCopyCertificateChain(ref) as? [SecCertificate], let certificate = chain.first else { complete(false); return }
            let fingerprint = SHA256.hash(data: SecCertificateCopyData(certificate) as Data).map { String(format: "%02X", $0) }.joined()
            complete(fingerprint == pairing.fingerprint.uppercased())
        }, queue)
        let tcp = NWProtocolTCP.Options(); tcp.noDelay = true
        let link = NWConnection(host: NWEndpoint.Host(pairing.host), port: NWEndpoint.Port(rawValue: pairing.port)!, using: NWParameters(tls: tls, tcp: tcp))
        connection = link; lastPacket = ProcessInfo.processInfo.systemUptime
        bootstrapping = session.isEmpty
        report?("Connecting \(medium.rawValue) to the PC…")
        link.stateUpdateHandler = { [weak self, weak link] state in
            guard let self, let link, self.connection === link else { return }
            switch state {
            case .ready:
                self.send(1, try! JSONSerialization.data(withJSONObject: ["version": 1, "token": pairing.token, "role": self.bootstrapping ? "media-session" : self.medium.rawValue, "cameraSession": self.session]))
                self.receive(link)
            case .waiting, .failed: self.failed("PC \(self.medium.rawValue) connection unavailable. Reconnecting…")
            default: break
            }
        }
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 2, repeating: 2)
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            let now = ProcessInfo.processInfo.systemUptime
            if now - self.lastPacket > 8 { self.failed("PC \(self.medium.rawValue) timed out. Reconnecting…"); return }
            if self.ready && !self.heartbeatPending { self.heartbeatPending = true; self.send(5, Data()) }
            if self.announced && now - self.lastMedia > 2 {
                self.stopOutput(); self.announced = false; self.report?("Waiting for PC \(self.medium.rawValue)…")
            }
        }
        self.timer = timer; timer.resume(); link.start(queue: queue)
    }
    private func receive(_ link: NWConnection) {
        link.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self, weak link] bytes, _, done, error in
            guard let self, let link, self.connection === link else { return }
            do {
                if let bytes { self.receiving.append(bytes) }
                while self.receiving.count >= 5 {
                    let length = self.receiving.prefix(4).reduce(0) { ($0 << 8) | Int($1) }
                    let kind = self.receiving[4]
                    let limit = self.medium == .camera && kind == 8 ? 4_194_305 : 65536
                    guard length >= 1, length <= limit else { throw BridgeError.message("Invalid call-media packet size.") }
                    guard self.receiving.count >= length + 4 else { break }
                    let payload = self.receiving.subdata(in: 5..<(length + 4))
                    self.receiving = Data(self.receiving.dropFirst(length + 4))
                    self.lastPacket = ProcessInfo.processInfo.systemUptime
                    try self.handle(kind, payload)
                    guard self.connection === link else { return }
                }
                if done || error != nil { self.failed("PC \(self.medium.rawValue) disconnected. Reconnecting…") }
                else { self.receive(link) }
            } catch {
                // Invalid media or a missing local driver must not cause an endless retry loop.
                self.wanted = false; self.generation += 1; self.close(); self.report?(error.localizedDescription + " Turn forwarding off and on to retry.")
            }
        }
    }
    private func handle(_ kind: UInt8, _ data: Data) throws {
        if bootstrapping {
            guard kind == 6, let hello = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  hello["version"] as? Int == 1, hello["role"] as? String == "media-session",
                  let current = hello["cameraSession"] as? String, current.count == 32,
                  current.allSatisfy({ $0.isHexDigit }) else { throw BridgeError.message("Invalid call-session response.") }
            close(); session = current; connect(); return
        }
        if !ready {
            guard kind == 6, let hello = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  hello["version"] as? Int == 1, hello["role"] as? String == medium.rawValue else { throw BridgeError.message("Unsupported call-media handshake.") }
            if medium == .camera {
                guard hello["codec"] as? String == "jpeg", let width = hello["width"] as? Int, let height = hello["height"] as? Int,
                      (width == 1920 && height == 1080 || width == 1280 && height == 720),
                      let fps = hello["fps"] as? Double, fps > 0, fps <= 30 else { throw BridgeError.message("PC camera must provide 720p or 1080p JPEG at up to 30 fps.") }
                camera.framesPerSecond = fps
                camera.inputWidth = width; camera.inputHeight = height
            } else {
                guard hello["sampleRate"] as? Int == 48000, hello["channels"] as? Int == 2, hello["bits"] as? Int == 16 else { throw BridgeError.message("Unsupported microphone format.") }
            }
            ready = true; sendDemand(); return
        }
        if kind == 5 { guard data.isEmpty else { throw BridgeError.message("Invalid heartbeat.") }; return }
        if kind == 9 {
            guard let status = try JSONSerialization.jsonObject(with: data) as? [String: Any], let enabled = status["enabled"] as? Bool,
                  let message = status["message"] as? String, message.count <= 2048 else { throw BridgeError.message("Invalid media status.") }
            self.enabled = enabled
            if !enabled { stopOutput(); announced = false }
            if enabled && medium == .speakers { _ = try audio.start() }
            report?(enabled && medium != .speakers && !demand
                ? "\(medium.rawValue.capitalized) idle · waiting for a calling app." : message)
            return
        }
        if medium != .speakers && !demand { return } // Discard packets already in flight after demand stops.
        guard enabled else { throw BridgeError.message("PC sent media while forwarding was disabled.") }
        if medium == .camera && kind == 8 { try camera.send(jpeg: data) }
        else if medium == .microphone && kind == 2 {
            let levels = try microphone.receive(data)
            let now = ProcessInfo.processInfo.systemUptime
            if now - lastMicrophoneLevelReport >= 1 {
                lastMicrophoneLevelReport = now
                func level(_ peak: Float) -> String {
                    peak > 0 ? "\(Int((20 * log10(peak)).rounded())) dBFS" : "silence"
                }
                let bufferedMilliseconds = levels.bufferedFrames * 1000 / 48_000
                report?("PC mic \(level(levels.received)) · TwinDesk Microphone \(levels.writtenFrames) frames · \(bufferedMilliseconds) ms buffered")
            }
        }
        else { throw BridgeError.message("Unexpected media packet.") }
        lastMedia = ProcessInfo.processInfo.systemUptime
        if !announced {
            announced = true
            report?(medium == .camera ? "Receiving \(camera.inputHeight)p video · select OBS Virtual Camera in your calling app." : "Receiving PC microphone · select TwinDesk Microphone in your calling app.")
        }
    }
    private func send(_ kind: UInt8, _ payload: Data) {
        guard let link = connection else { return }
        var size = UInt32(payload.count + 1).bigEndian
        var packet = Data(bytes: &size, count: 4); packet.append(kind); packet.append(payload)
        link.send(content: packet, completion: .contentProcessed { [weak self, weak link] error in
            guard let self, let link, self.connection === link else { return }
            if kind == 5 { self.heartbeatPending = false }
            if error != nil { self.failed("Call-media send failed. Reconnecting…") }
        })
    }
    private func sendAudio(_ data: Data) {
        guard !data.isEmpty, data.count <= 1920, data.count % 4 == 0,
              audioSlots.wait(timeout: .now()) == .success else { return }
        queue.async { [weak self] in
            guard let self else { return }
            guard self.medium == .speakers, self.ready, self.enabled, let link = self.connection else {
                self.audioSlots.signal(); return
            }
            var size = UInt32(data.count + 1).bigEndian
            var packet = Data(bytes: &size, count: 4); packet.append(2); packet.append(data)
            link.send(content: packet, completion: .contentProcessed { [weak self, weak link] error in
                guard let self else { return }
                self.audioSlots.signal()
                if error != nil, let link, self.connection === link { self.failed("Speaker audio disconnected. Reconnecting…") }
            })
        }
    }
    private func stopOutput() { camera.stop(); microphone.stop(); audio.stop() }
    private func close() {
        let link = connection; connection = nil; link?.cancel(); timer?.cancel(); timer = nil
        ready = false; enabled = false; announced = false; heartbeatPending = false; receiving.removeAll(); stopOutput()
    }
    private func failed(_ message: String) {
        close(); session = ""; report?(message)
        let generation = self.generation
        if wanted { queue.asyncAfter(deadline: .now() + 2) { [weak self] in
            guard let self, self.wanted, self.generation == generation else { return }; self.connect()
        } }
    }
}
