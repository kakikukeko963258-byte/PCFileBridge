import Foundation

struct BridgeClient {
    let server: SavedServer
    let token: String?

    private func makeURL(_ path: String, query: [URLQueryItem] = []) throws -> URL {
        guard var components = URLComponents(string: server.baseURL) else { throw BridgeError.invalidServerAddress }
        components.path = path
        if !query.isEmpty { components.queryItems = query }
        guard let url = components.url else { throw BridgeError.invalidServerAddress }
        return url
    }

    private func request(_ path: String, method: String = "GET", query: [URLQueryItem] = [], body: Data? = nil, contentType: String? = nil) throws -> URLRequest {
        var request = URLRequest(url: try makeURL(path, query: query), timeoutInterval: 120)
        request.httpMethod = method
        if let token { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        if let body { request.httpBody = body }
        if let contentType { request.setValue(contentType, forHTTPHeaderField: "Content-Type") }
        return request
    }

    func health() async throws -> String {
        let (data, response) = try await URLSession.shared.data(for: request("/api/health"))
        try Self.validate(response, data: data)
        return "接続できました"
    }

    static func pair(baseURL: String, code: String, deviceName: String) async throws -> PairResponse {
        let cleaned = normalize(baseURL)
        let server = SavedServer(id: UUID().uuidString.lowercased(), name: "PC", baseURL: cleaned, deviceId: "", addedAt: Date())
        let client = BridgeClient(server: server, token: nil)
        let body = try JSONEncoder.bridge.encode(PairRequest(code: code, deviceName: deviceName))
        let req = try client.request("/api/pair", method: "POST", body: body, contentType: "application/json")
        let (data, response) = try await URLSession.shared.data(for: req)
        try validate(response, data: data)
        return try JSONDecoder.bridge.decode(PairResponse.self, from: data)
    }

    static func normalize(_ address: String) -> String {
        let value = address.trimmingCharacters(in: .whitespacesAndNewlines)
        if value.hasPrefix("http://") || value.hasPrefix("https://") { return value.trimmingCharacters(in: CharacterSet(charactersIn: "/")) }
        return "http://\(value)"
    }

    func items(parentId: String, continuationToken: String? = nil, limit: Int = 200) async throws -> ItemPage {
        var query = [URLQueryItem(name: "parentId", value: parentId), URLQueryItem(name: "limit", value: "\(limit)")]
        if let continuationToken { query.append(URLQueryItem(name: "continuationToken", value: continuationToken)) }
        let req = try request("/api/items", query: query)
        let (data, response) = try await URLSession.shared.data(for: req)
        try Self.validate(response, data: data)
        return try JSONDecoder.bridge.decode(ItemPage.self, from: data)
    }

    func item(id: String) async throws -> BridgeItem {
        let req = try request("/api/items/\(id.urlPathComponent)")
        let (data, response) = try await URLSession.shared.data(for: req)
        try Self.validate(response, data: data)
        return try JSONDecoder.bridge.decode(BridgeItem.self, from: data)
    }

    func changes(parentId: String?, itemId: String? = nil, after: Int64) async throws -> ChangePage {
        var query = [URLQueryItem(name: "after", value: "\(after)")]
        if let parentId { query.append(URLQueryItem(name: "parentId", value: parentId)) }
        if let itemId { query.append(URLQueryItem(name: "itemId", value: itemId)) }
        let req = try request("/api/changes", query: query)
        let (data, response) = try await URLSession.shared.data(for: req)
        try Self.validate(response, data: data)
        return try JSONDecoder.bridge.decode(ChangePage.self, from: data)
    }

    func download(id: String, to destination: URL) async throws {
        let req = try request("/api/items/\(id.urlPathComponent)/content")
        let (temp, response) = try await URLSession.shared.download(for: req)
        try Self.validate(response, data: Data())
        try? FileManager.default.removeItem(at: destination)
        try FileManager.default.copyItem(at: temp, to: destination)
    }

    func create(parentId: String, name: String, isDirectory: Bool) async throws -> BridgeItem {
        let body = try JSONEncoder.bridge.encode(CreateItemRequest(parentId: parentId, name: name, isDirectory: isDirectory))
        let req = try request("/api/items", method: "POST", body: body, contentType: "application/json")
        let (data, response) = try await URLSession.shared.data(for: req)
        try Self.validate(response, data: data)
        return try JSONDecoder.bridge.decode(BridgeItem.self, from: data)
    }

    func modify(id: String, name: String?, parentId: String?) async throws -> BridgeItem {
        let body = try JSONEncoder.bridge.encode(ModifyItemRequest(name: name, parentId: parentId))
        let req = try request("/api/items/\(id.urlPathComponent)", method: "PATCH", body: body, contentType: "application/json")
        let (data, response) = try await URLSession.shared.data(for: req)
        try Self.validate(response, data: data)
        return try JSONDecoder.bridge.decode(BridgeItem.self, from: data)
    }

    func upload(id: String, from file: URL) async throws -> BridgeItem {
        let req = try request("/api/items/\(id.urlPathComponent)/content", method: "PUT")
        let (data, response) = try await URLSession.shared.upload(for: req, fromFile: file)
        try Self.validate(response, data: data)
        return try JSONDecoder.bridge.decode(BridgeItem.self, from: data)
    }

    func delete(id: String) async throws {
        let req = try request("/api/items/\(id.urlPathComponent)", method: "DELETE")
        let (data, response) = try await URLSession.shared.data(for: req)
        try Self.validate(response, data: data)
    }

    private static func validate(_ response: URLResponse, data: Data) throws {
        guard let http = response as? HTTPURLResponse else { throw BridgeError.invalidResponse }
        guard (200..<300).contains(http.statusCode) else {
            let message = (try? JSONSerialization.jsonObject(with: data) as? [String: String])?["error"] ?? String(data: data.prefix(300), encoding: .utf8) ?? ""
            throw BridgeError.http(http.statusCode, message)
        }
    }
}

private extension String {
    var urlPathComponent: String { addingPercentEncoding(withAllowedCharacters: .urlPathAllowed.subtracting(CharacterSet(charactersIn: "/?#"))) ?? self }
}
