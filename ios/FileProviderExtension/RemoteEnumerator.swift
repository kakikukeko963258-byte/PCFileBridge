import FileProvider

final class RemoteEnumerator: NSObject, NSFileProviderEnumerator {
    private static let initialNamePage = NSFileProviderPage(rawValue: NSFileProviderPage.initialPageSortedByName as Data)
    private static let initialDatePage = NSFileProviderPage(rawValue: NSFileProviderPage.initialPageSortedByDate as Data)
    private let containerIdentifier: NSFileProviderItemIdentifier
    private let domain: NSFileProviderDomain

    init(containerIdentifier: NSFileProviderItemIdentifier, domain: NSFileProviderDomain) {
        self.containerIdentifier = containerIdentifier
        self.domain = domain
    }

    func enumerateItems(for observer: any NSFileProviderEnumerationObserver, startingAt page: NSFileProviderPage) {
        let isInitialPage = page == Self.initialNamePage || page == Self.initialDatePage
        let continuationToken = isInitialPage ? nil : String(data: page.rawValue, encoding: .utf8)
        Task {
            do {
                let client = try makeClient()
                let container = containerIdentifier.rawValue
                if container == NSFileProviderItemIdentifier.workingSet.rawValue {
                    let identifiers = MaterializedItemStore.load(domainID: domain.identifier.rawValue)
                    let offset = Int(continuationToken ?? "0") ?? 0
                    let suggestedPageSize = observer.suggestedPageSize ?? 200
                    let end = min(offset + min(max(suggestedPageSize, 50), 200), identifiers.count)
                    let requested = offset < end ? Array(identifiers[offset..<end]) : []
                    let remoteItems = await withTaskGroup(of: BridgeItem?.self) { group in
                        var iterator = requested.makeIterator()
                        for _ in 0..<min(12, requested.count) {
                            if let id = iterator.next() { group.addTask { try? await client.item(id: id) } }
                        }
                        var values: [BridgeItem] = []
                        while let result = await group.next() {
                            if let result { values.append(result) }
                            if let id = iterator.next() { group.addTask { try? await client.item(id: id) } }
                        }
                        return values
                    }
                    observer.didEnumerate(remoteItems.map(RemoteProviderItem.init))
                    let next = end < identifiers.count ? NSFileProviderPage(rawValue: Data("\(end)".utf8)) : nil
                    observer.finishEnumerating(upTo: next)
                    return
                }
                let parentId: String
                if containerIdentifier == .rootContainer { parentId = "root" }
                else {
                    let parent = try await client.item(id: container)
                    guard parent.isDirectory else { observer.finishEnumerating(upTo: nil); return }
                    parentId = parent.id
                }
                let suggestedPageSize = observer.suggestedPageSize ?? 200
                let result = try await client.items(parentId: parentId, continuationToken: continuationToken, limit: min(max(suggestedPageSize, 50), 500))
                observer.didEnumerate(result.items.map(RemoteProviderItem.init))
                let next = result.next.map { NSFileProviderPage(rawValue: Data($0.utf8)) }
                observer.finishEnumerating(upTo: next)
            } catch { observer.finishEnumeratingWithError(error) }
        }
    }

    func enumerateChanges(for observer: any NSFileProviderChangeObserver, from syncAnchor: NSFileProviderSyncAnchor) {
        Task {
            do {
                let client = try makeClient()
                let container = containerIdentifier.rawValue
                let anchor = Int64(String(data: syncAnchor.rawValue, encoding: .utf8) ?? "0") ?? 0
                let parentId: String?
                let itemId: String?
                let isWorkingSet = container == NSFileProviderItemIdentifier.workingSet.rawValue
                if containerIdentifier == .rootContainer {
                    parentId = "root"; itemId = nil
                } else if isWorkingSet {
                    parentId = nil; itemId = nil
                } else {
                    let remote = try await client.item(id: container)
                    if remote.isDirectory { parentId = remote.id; itemId = nil }
                    else { parentId = nil; itemId = remote.id }
                }
                let page = try await client.changes(parentId: parentId, itemId: itemId, after: anchor)
                let materialized = Set(MaterializedItemStore.load(domainID: domain.identifier.rawValue))
                var latestByID: [String: RemoteChange] = [:]
                for change in page.changes {
                    if !isWorkingSet || materialized.contains(change.itemId) { latestByID[change.itemId] = change }
                    if isWorkingSet, materialized.contains(change.parentId), change.kind != "deleted" {
                        latestByID[change.parentId] = RemoteChange(sequence: change.sequence, parentId: "root", itemId: change.parentId, kind: "updated")
                    }
                }
                let changes = latestByID.values.sorted { $0.sequence < $1.sequence }
                let deleted = changes.filter { $0.kind == "deleted" }.map { NSFileProviderItemIdentifier(rawValue: $0.itemId) }
                if !deleted.isEmpty { observer.didDeleteItems(withIdentifiers: deleted) }
                var updated: [any NSFileProviderItemProtocol] = []
                for change in changes where change.kind != "deleted" {
                    if let remote = try? await client.item(id: change.itemId) { updated.append(RemoteProviderItem(remote)) }
                }
                if !updated.isEmpty { observer.didUpdate(updated) }
                observer.finishEnumeratingChanges(upTo: NSFileProviderSyncAnchor(Data("\(page.latestSequence)".utf8)), moreComing: page.changes.count >= 2000)
            } catch { observer.finishEnumeratingWithError(error) }
        }
    }

    func currentSyncAnchor(completionHandler: @escaping (NSFileProviderSyncAnchor?) -> Void) {
        Task {
            do {
                let client = try makeClient()
                let container = containerIdentifier.rawValue
                let parentId: String?
                let itemId: String?
                let isWorkingSet = container == NSFileProviderItemIdentifier.workingSet.rawValue
                if containerIdentifier == .rootContainer {
                    parentId = "root"; itemId = nil
                } else if isWorkingSet {
                    parentId = nil; itemId = nil
                } else {
                    let remote = try await client.item(id: container)
                    if remote.isDirectory { parentId = remote.id; itemId = nil }
                    else { parentId = nil; itemId = remote.id }
                }
                let page = try await client.changes(parentId: parentId, itemId: itemId, after: 0)
                completionHandler(NSFileProviderSyncAnchor(Data("\(page.latestSequence)".utf8)))
            } catch { completionHandler(nil) }
        }
    }

    func invalidate() {}

    private func makeClient() throws -> BridgeClient {
        guard let server = SharedConfiguration.server(forDomain: domain.identifier.rawValue) else { throw BridgeError.sharedContainerUnavailable }
        return BridgeClient(server: server, token: try SharedTokenStore.load(for: server.id))
    }
}
