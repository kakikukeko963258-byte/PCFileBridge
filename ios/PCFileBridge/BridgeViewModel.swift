import Foundation
import FileProvider
import UIKit
import Combine

@MainActor
final class BridgeViewModel: ObservableObject {
    @Published var servers: [SavedServer] = []
    @Published var address = ""
    @Published var pairingCode = ""
    @Published var isWorking = false
    @Published var message: String?
    @Published var error: String?

    init() { servers = SharedConfiguration.loadServers() }

    func pair() async {
        guard !address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              pairingCode.count == 6 else { error = "PCのアドレスと6桁コードを入力してください。"; return }
        isWorking = true; error = nil; message = nil
        defer { isWorking = false }
        do {
            let response = try await BridgeClient.pair(baseURL: address, code: pairingCode, deviceName: UIDevice.current.name)
            let server = SavedServer(id: UUID().uuidString.lowercased(), name: response.serverName, baseURL: BridgeClient.normalize(address), deviceId: response.deviceId, addedAt: Date())
            try SharedTokenStore.save(response.token, for: server.id)
            servers.insert(server, at: 0)
            try SharedConfiguration.saveServers(servers)
            try await addDomain(for: server)
            address = ""; pairingCode = ""
            message = "\(server.name)をペアリングし、Filesに追加しました。"
        } catch { self.error = error.localizedDescription }
    }

    func test(_ server: SavedServer) async {
        isWorking = true; error = nil; message = nil
        defer { isWorking = false }
        do {
            let client = BridgeClient(server: server, token: try SharedTokenStore.load(for: server.id))
            message = "\(server.name): \(try await client.health())"
        } catch { self.error = error.localizedDescription }
    }

    func remove(_ server: SavedServer) async {
        isWorking = true; error = nil; message = nil
        defer { isWorking = false }
        do {
            let currentDomains = try await Self.domains()
            let domain = currentDomains.first { $0.identifier.rawValue == server.id }
            if let domain { try await NSFileProviderManager.remove(domain) }
            SharedTokenStore.delete(for: server.id)
            servers.removeAll { $0.id == server.id }
            try SharedConfiguration.saveServers(servers)
            message = "\(server.name)を削除しました。"
        } catch { self.error = error.localizedDescription }
    }

    private func addDomain(for server: SavedServer) async throws {
        let current = try await Self.domains()
        if current.contains(where: { $0.identifier.rawValue == server.id }) { return }
        let domain = NSFileProviderDomain(identifier: NSFileProviderDomainIdentifier(rawValue: server.id), displayName: server.name)
        try await NSFileProviderManager.add(domain)
    }

    private static func domains() async throws -> [NSFileProviderDomain] {
        try await withCheckedThrowingContinuation { continuation in
            NSFileProviderManager.getDomainsWithCompletionHandler { domains, error in
                if let error { continuation.resume(throwing: error) }
                else { continuation.resume(returning: domains) }
            }
        }
    }
}
