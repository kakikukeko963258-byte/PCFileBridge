import Foundation

struct BridgeItem: Codable, Identifiable, Hashable {
    let id: String
    let parentId: String
    let name: String
    let isDirectory: Bool
    let size: Int64
    let modifiedUtc: Date
    let version: String
}

struct ItemPage: Codable {
    let items: [BridgeItem]
    let next: String?
}

struct PairRequest: Encodable {
    let code: String
    let deviceName: String
}

struct PairResponse: Decodable {
    let deviceId: String
    let token: String
    let serverName: String
    let port: Int
}

struct CreateItemRequest: Encodable {
    let parentId: String
    let name: String
    let isDirectory: Bool
}

struct ModifyItemRequest: Encodable {
    let name: String?
    let parentId: String?
}

struct RemoteChange: Codable {
    let sequence: Int64
    let parentId: String
    let itemId: String
    let kind: String
}

struct ChangePage: Codable {
    let changes: [RemoteChange]
    let latestSequence: Int64
}

struct SavedServer: Codable, Identifiable, Hashable {
    var id: String
    var name: String
    var baseURL: String
    var deviceId: String
    var addedAt: Date
}

enum BridgeConstants {
    static var appGroup: String { Bundle.main.object(forInfoDictionaryKey: "PCFileBridgeAppGroup") as? String ?? "group.com.studyquiz.dailyquiz" }
    static let tokenService = "PCFileBridge.token.v1"
}
