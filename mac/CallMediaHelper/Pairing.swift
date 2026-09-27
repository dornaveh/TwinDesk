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
