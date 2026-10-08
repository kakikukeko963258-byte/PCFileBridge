import Foundation
import Security

enum SharedConfiguration {
    private static var container: URL? { FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: BridgeConstants.appGroup) }
    private static var serversFile: URL? { container?.appendingPathComponent("servers.json") }

    static func loadServers() -> [SavedServer] {
        guard let file = serversFile, let data = try? Data(contentsOf: file) else { return [] }
        return (try? JSONDecoder.bridge.decode([SavedServer].self, from: data)) ?? []
    }

    static func saveServers(_ servers: [SavedServer]) throws {
        guard let file = serversFile else { throw BridgeError.sharedContainerUnavailable }
        let data = try JSONEncoder.bridge.encode(servers)
        try data.write(to: file, options: .atomic)
    }

    static func server(forDomain id: String) -> SavedServer? { loadServers().first { $0.id == id } }
}

enum MaterializedItemStore {
    private static let lock = NSLock()
    private static var fileURL: URL? {
        FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: BridgeConstants.appGroup)?.appendingPathComponent("materialized-items.json")
    }

    static func load(domainID: String) -> [String] {
        lock.lock(); defer { lock.unlock() }
        guard let fileURL, let data = try? Data(contentsOf: fileURL),
              let map = try? JSONDecoder().decode([String: [String]].self, from: data) else { return [] }
        return map[domainID] ?? []
    }

    static func save(_ identifiers: Set<String>, domainID: String) {
        lock.lock(); defer { lock.unlock() }
        guard let fileURL else { return }
        var map = (try? Data(contentsOf: fileURL)).flatMap { try? JSONDecoder().decode([String: [String]].self, from: $0) } ?? [:]
        map[domainID] = Array(identifiers).sorted()
        if let data = try? JSONEncoder().encode(map) { try? data.write(to: fileURL, options: .atomic) }
    }
}

enum KeychainTokenStore {
    private static var sharedAccessGroup: String? {
        Bundle.main.object(forInfoDictionaryKey: "PCFileBridgeKeychainGroup") as? String
    }

    static func save(_ token: String, for serverId: String) throws {
        let data = Data(token.utf8)
        var base: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                  kSecAttrService as String: BridgeConstants.tokenService,
                                  kSecAttrAccount as String: serverId]
        if let group = sharedAccessGroup { base[kSecAttrAccessGroup as String] = group }
        SecItemDelete(base as CFDictionary)
        var query = base
        query[kSecValueData as String] = data
        query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else { throw BridgeError.keychain(status) }
    }

    static func load(for serverId: String) throws -> String {
        var query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                   kSecAttrService as String: BridgeConstants.tokenService,
                                   kSecAttrAccount as String: serverId,
                                   kSecReturnData as String: true,
                                   kSecMatchLimit as String: kSecMatchLimitOne]
        if let group = sharedAccessGroup { query[kSecAttrAccessGroup as String] = group }
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess, let data = result as? Data, let token = String(data: data, encoding: .utf8) else { throw BridgeError.keychain(status) }
        return token
    }

    static func delete(for serverId: String) {
        var query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                   kSecAttrService as String: BridgeConstants.tokenService,
                                   kSecAttrAccount as String: serverId]
        if let group = sharedAccessGroup { query[kSecAttrAccessGroup as String] = group }
        SecItemDelete(query as CFDictionary)
    }
}

enum BridgeError: LocalizedError {
    case sharedContainerUnavailable
    case invalidServerAddress
    case invalidResponse
    case http(Int, String)
    case keychain(OSStatus)

    var errorDescription: String? {
        switch self {
        case .sharedContainerUnavailable: return "共有コンテナを利用できません。署名設定を確認してください。"
        case .invalidServerAddress: return "サーバーアドレスを確認してください。"
        case .invalidResponse: return "サーバーからの応答を読み取れませんでした。"
        case .http(let code, let message): return "サーバーエラー (\(code): \(message))"
        case .keychain(let status): return "Keychainエラー (\(status))"
        }
    }
}

extension JSONDecoder {
    static var bridge: JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer(); let value = try container.decode(String.self)
            let normalized = value.replacingOccurrences(
                of: #"(\.\d{1,3})\d+(?=Z|[+-]\d{2}:\d{2}$)"#,
                with: "$1",
                options: .regularExpression
            )
            let fractional = ISO8601DateFormatter(); fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            if let date = fractional.date(from: value) ?? fractional.date(from: normalized) { return date }
            let plain = ISO8601DateFormatter(); plain.formatOptions = [.withInternetDateTime]
            if let date = plain.date(from: value) ?? plain.date(from: normalized) { return date }
            throw DecodingError.dataCorruptedError(in: container, debugDescription: "Invalid ISO-8601 date")
        }
        return decoder
    }
}
extension JSONEncoder {
    static var bridge: JSONEncoder { let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601; return encoder }
}
