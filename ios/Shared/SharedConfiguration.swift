import Foundation

enum SharedConfiguration {
    static func loadServers() -> [SavedServer] {
        guard let file = try? AppStorageDirectory.file("servers.json"),
              let data = try? Data(contentsOf: file) else { return [] }
        return (try? JSONDecoder.bridge.decode([SavedServer].self, from: data)) ?? []
    }

    static func saveServers(_ servers: [SavedServer]) throws {
        let file = try AppStorageDirectory.file("servers.json")
        let data = try JSONEncoder.bridge.encode(servers)
        try data.write(to: file, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
    }
}

enum MaterializedItemStore {
    private static let lock = NSLock()
    private static var fileURL: URL? {
        try? AppStorageDirectory.file("materialized-items.json")
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
        if let data = try? JSONEncoder().encode(map) {
            try? data.write(to: fileURL, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
        }
    }
}

enum AppStorageDirectory {
    static func file(_ name: String) throws -> URL {
        let support = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
        let directory = support.appendingPathComponent("PCFileBridge", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory.appendingPathComponent(name)
    }
}

enum TokenStore {
    private static let lock = NSLock()
    private static var tokensFile: URL? {
        try? AppStorageDirectory.file("tokens.json")
    }

    static func save(_ token: String, for serverId: String) throws {
        lock.lock(); defer { lock.unlock() }
        guard let file = tokensFile else { throw BridgeError.privateStorageUnavailable }
        var tokens = readTokens(from: file)
        tokens[serverId] = token
        try writeTokens(tokens, to: file)
    }

    static func load(for serverId: String) throws -> String {
        lock.lock(); defer { lock.unlock() }
        guard let file = tokensFile else { throw BridgeError.privateStorageUnavailable }
        guard let token = readTokens(from: file)[serverId] else { throw BridgeError.tokenNotFound }
        return token
    }

    static func delete(for serverId: String) {
        lock.lock(); defer { lock.unlock() }
        guard let file = tokensFile else { return }
        var tokens = readTokens(from: file)
        tokens.removeValue(forKey: serverId)
        try? writeTokens(tokens, to: file)
    }

    private static func readTokens(from file: URL) -> [String: String] {
        guard let data = try? Data(contentsOf: file),
              let tokens = try? JSONDecoder().decode([String: String].self, from: data) else { return [:] }
        return tokens
    }

    private static func writeTokens(_ tokens: [String: String], to file: URL) throws {
        let data = try JSONEncoder().encode(tokens)
        try data.write(to: file, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
    }
}

enum BridgeError: LocalizedError {
    case privateStorageUnavailable
    case providerConfigurationUnavailable
    case invalidServerAddress
    case invalidResponse
    case http(Int, String)
    case tokenNotFound

    var errorDescription: String? {
        switch self {
        case .privateStorageUnavailable: return "アプリの保護領域を利用できません。iPhoneを再起動して、もう一度お試しください。"
        case .providerConfigurationUnavailable: return "Filesの接続設定が見つかりません。アプリでPCと再ペアリングしてください。"
        case .invalidServerAddress: return "サーバーアドレスを確認してください。"
        case .invalidResponse: return "サーバーからの応答を読み取れませんでした。"
        case .http(let code, let message): return "サーバーエラー (\(code): \(message))"
        case .tokenNotFound: return "ペアリング情報が見つかりません。PCと再ペアリングしてください。"
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
