import Foundation
import FileProvider
import Combine

@MainActor
final class ChangeMonitor: ObservableObject {
    private var task: Task<Void, Never>?

    func start(servers: [SavedServer]) {
        stop()
        guard !servers.isEmpty else { return }
        task = Task {
            while !Task.isCancelled {
                for server in servers {
                    if Task.isCancelled { return }
                    await poll(server)
                }
                try? await Task.sleep(nanoseconds: 10_000_000_000)
            }
        }
    }

    func stop() {
        task?.cancel()
        task = nil
    }

    private func poll(_ server: SavedServer) async {
        let anchorKey = "PCFileBridge.changeAnchor.\(server.id)"
        do {
            let client = BridgeClient(server: server, token: try SharedTokenStore.load(for: server.id))
            let previous = UserDefaults.standard.object(forKey: anchorKey) as? Int64
            let page = try await client.changes(parentId: nil, after: previous ?? 0)
            if previous == nil {
                UserDefaults.standard.set(page.latestSequence, forKey: anchorKey)
                return
            }
            guard !page.changes.isEmpty else {
                UserDefaults.standard.set(page.latestSequence, forKey: anchorKey)
                return
            }

            let domains = try await Self.domains()
            guard let domain = domains.first(where: { $0.identifier.rawValue == server.id }),
                  let manager = NSFileProviderManager(for: domain) else { return }

            let parents = Set(page.changes.map(\.parentId))
            let changedItems = Set(page.changes.map(\.itemId))
            for parent in parents {
                let identifier = parent == "root" ? NSFileProviderItemIdentifier.rootContainer : NSFileProviderItemIdentifier(rawValue: parent)
                await Self.signal(manager, identifier: identifier)
            }
            for item in changedItems {
                await Self.signal(manager, identifier: NSFileProviderItemIdentifier(rawValue: item))
            }
            await Self.signal(manager, identifier: .workingSet)
            UserDefaults.standard.set(page.latestSequence, forKey: anchorKey)
        } catch {
            // Keep the last anchor and retry on the next foreground poll.
        }
    }

    private static func domains() async throws -> [NSFileProviderDomain] {
        try await withCheckedThrowingContinuation { continuation in
            NSFileProviderManager.getDomainsWithCompletionHandler { domains, error in
                if let error { continuation.resume(throwing: error) }
                else { continuation.resume(returning: domains) }
            }
        }
    }

    private static func signal(_ manager: NSFileProviderManager, identifier: NSFileProviderItemIdentifier) async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            manager.signalEnumerator(for: identifier) { _ in continuation.resume() }
        }
    }
}
