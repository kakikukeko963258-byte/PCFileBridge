import Foundation
import FileProvider

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

enum ProviderConfiguration {
    static func userInfo(for server: SavedServer, token: String) -> [AnyHashable: Any] {
        [
            "serverID": server.id,
            "serverName": server.name,
            "baseURL": server.baseURL,
            "deviceID": server.deviceId,
            "token": token
        ]
    }

    static func client(for domain: NSFileProviderDomain) throws -> BridgeClient {
        guard let userInfo = domain.userInfo,
              let serverID = userInfo["serverID"] as? String,
              serverID == domain.identifier.rawValue,
              let serverName = userInfo["serverName"] as? String,
              let baseURL = userInfo["baseURL"] as? String,
              let deviceID = userInfo["deviceID"] as? String,
              let token = userInfo["token"] as? String else {
            throw BridgeError.providerConfigurationUnavailable
        }

        let server = SavedServer(id: serverID, name: serverName, baseURL: baseURL, deviceId: deviceID, addedAt: Date())
        return BridgeClient(server: server, token: token)
    }
}
