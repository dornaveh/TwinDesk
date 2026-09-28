import Foundation

// Object IDs may be recycled when Core Audio restarts. Match identities as well
// as the selected device ID before reusing a route or destroying its objects.
struct AudioTapRoute {
    let outputID: UInt32
    let outputUID: String
    let tapUID: String
    let aggregateUID: String

    func matches(outputID: UInt32, outputUID: String?, tapUID: String?, aggregateUID: String?) -> Bool {
        self.outputID == outputID && self.outputUID == outputUID &&
            self.tapUID == tapUID && self.aggregateUID == aggregateUID
    }
}
