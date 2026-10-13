import FileProvider

final class FileProviderExtension: NSObject, NSFileProviderReplicatedExtension {
    let domain: NSFileProviderDomain
    private var materializedScan: MaterializedItemsObserver?

    init(domain: NSFileProviderDomain) { self.domain = domain; super.init() }

    func invalidate() {}

    func enumerator(for containerItemIdentifier: NSFileProviderItemIdentifier, request: NSFileProviderRequest) throws -> any NSFileProviderEnumerator {
        if containerItemIdentifier == .trashContainer { throw NSError(domain: NSCocoaErrorDomain, code: NSFeatureUnsupportedError) }
        return RemoteEnumerator(containerIdentifier: containerItemIdentifier, domain: domain)
    }

    func item(for identifier: NSFileProviderItemIdentifier, request: NSFileProviderRequest, completionHandler: @escaping (NSFileProviderItem?, (any Error)?) -> Void) -> Progress {
        let progress = Progress(totalUnitCount: -1)
        let task = Task {
            do {
                let client = try makeClient()
                completionHandler(RemoteProviderItem(try await client.item(id: identifier.rawValue)), nil)
            } catch { completionHandler(nil, error) }
        }
        progress.cancellationHandler = { task.cancel() }
        return progress
    }

    func fetchContents(for identifier: NSFileProviderItemIdentifier, version: NSFileProviderItemVersion?, request: NSFileProviderRequest, completionHandler: @escaping (URL?, NSFileProviderItem?, (any Error)?) -> Void) -> Progress {
        let progress = Progress(totalUnitCount: -1)
        let task = Task {
            do {
                let client = try makeClient()
                let item = try await client.item(id: identifier.rawValue)
                let manager = NSFileProviderManager(for: domain)
                let folder = try manager?.temporaryDirectoryURL() ?? FileManager.default.temporaryDirectory
                let target = folder.appendingPathComponent(UUID().uuidString + "-" + item.name)
                try await client.download(id: item.id, to: target)
                completionHandler(target, RemoteProviderItem(try await client.item(id: item.id)), nil)
            } catch { completionHandler(nil, nil, error) }
        }
        progress.cancellationHandler = { task.cancel() }
        return progress
    }

    func createItem(basedOn itemTemplate: NSFileProviderItem, fields: NSFileProviderItemFields, contents: URL?, options: NSFileProviderCreateItemOptions, request: NSFileProviderRequest, completionHandler: @escaping (NSFileProviderItem?, NSFileProviderItemFields, Bool, (any Error)?) -> Void) -> Progress {
        let progress = Progress(totalUnitCount: -1)
        let task = Task {
            do {
                let client = try makeClient()
                let parent = itemTemplate.parentItemIdentifier == .rootContainer ? "root" : itemTemplate.parentItemIdentifier.rawValue
                let isDirectory = itemTemplate.contentType?.conforms(to: .folder) ?? false
                let created = try await client.create(parentId: parent, name: itemTemplate.filename, isDirectory: isDirectory)
                if let contents { _ = try await client.upload(id: created.id, from: contents) }
                let fresh = try await client.item(id: created.id)
                signalEnumerator(for: parent)
                signalWorkingSet()
                completionHandler(RemoteProviderItem(fresh), [], false, nil)
            } catch { completionHandler(nil, [], false, error) }
        }
        progress.cancellationHandler = { task.cancel() }
        return progress
    }

    func modifyItem(_ item: NSFileProviderItem, baseVersion: NSFileProviderItemVersion, changedFields: NSFileProviderItemFields, contents: URL?, options: NSFileProviderModifyItemOptions, request: NSFileProviderRequest, completionHandler: @escaping (NSFileProviderItem?, NSFileProviderItemFields, Bool, (any Error)?) -> Void) -> Progress {
        let progress = Progress(totalUnitCount: -1)
        let task = Task {
            do {
                let client = try makeClient()
                let oldParent = item.parentItemIdentifier.rawValue
                let parent = item.parentItemIdentifier == .rootContainer ? nil : item.parentItemIdentifier.rawValue
                _ = try await client.modify(id: item.itemIdentifier.rawValue, name: item.filename, parentId: parent)
                if let contents { _ = try await client.upload(id: item.itemIdentifier.rawValue, from: contents) }
                let fresh = try await client.item(id: item.itemIdentifier.rawValue)
                signalEnumerator(for: oldParent)
                signalEnumerator(for: fresh.parentId)
                signalWorkingSet()
                completionHandler(RemoteProviderItem(fresh), changedFields, false, nil)
            } catch { completionHandler(nil, [], false, error) }
        }
        progress.cancellationHandler = { task.cancel() }
        return progress
    }

    func deleteItem(identifier: NSFileProviderItemIdentifier, baseVersion: NSFileProviderItemVersion, options: NSFileProviderDeleteItemOptions, request: NSFileProviderRequest, completionHandler: @escaping ((any Error)?) -> Void) -> Progress {
        let progress = Progress(totalUnitCount: -1)
        let task = Task {
            do {
                let client = try makeClient()
                let item = try? await client.item(id: identifier.rawValue)
                try await client.delete(id: identifier.rawValue)
                if let item { signalEnumerator(for: item.parentId) }
                signalWorkingSet()
                completionHandler(nil)
            } catch { completionHandler(error) }
        }
        progress.cancellationHandler = { task.cancel() }
        return progress
    }

    func materializedItemsDidChange(completionHandler: @escaping () -> Void) {
        guard let manager = NSFileProviderManager(for: domain) else { completionHandler(); return }
        let observer = MaterializedItemsObserver(enumerator: manager.enumeratorForMaterializedItems()) { [weak self] identifiers in
            MaterializedItemStore.save(identifiers, domainID: self?.domain.identifier.rawValue ?? "")
            self?.materializedScan = nil
            self?.signalWorkingSet()
            completionHandler()
        }
        materializedScan = observer
        observer.start()
    }
    func pendingItemsDidChange(completionHandler: @escaping () -> Void) { completionHandler() }
    func importDidFinish(completionHandler: @escaping () -> Void) { completionHandler() }

    private func makeClient() throws -> BridgeClient {
        try ProviderConfiguration.client(for: domain)
    }

    private func signalEnumerator(for parentId: String) {
        guard let manager = NSFileProviderManager(for: domain) else { return }
        let identifier = parentId == "root" ? NSFileProviderItemIdentifier.rootContainer : NSFileProviderItemIdentifier(rawValue: parentId)
        manager.signalEnumerator(for: identifier) { _ in }
    }

    private func signalWorkingSet() { signalEnumerator(for: NSFileProviderItemIdentifier.workingSet.rawValue) }
}

private final class MaterializedItemsObserver: NSObject, NSFileProviderEnumerationObserver {
    private static let initialNamePage = NSFileProviderPage(rawValue: NSFileProviderPage.initialPageSortedByName as Data)
    private let enumerator: any NSFileProviderEnumerator
    private let completion: (Set<String>) -> Void
    private var identifiers = Set<String>()
    private var completed = false

    init(enumerator: any NSFileProviderEnumerator, completion: @escaping (Set<String>) -> Void) {
        self.enumerator = enumerator; self.completion = completion
    }

    var suggestedPageSize: Int { 500 }

    func start() { enumerator.enumerateItems(for: self, startingAt: Self.initialNamePage) }

    func didEnumerate(_ updatedItems: [any NSFileProviderItemProtocol]) {
        identifiers.formUnion(updatedItems.map { $0.itemIdentifier.rawValue })
    }

    func finishEnumerating(upTo page: NSFileProviderPage?) {
        if let page { enumerator.enumerateItems(for: self, startingAt: page) }
        else { finish() }
    }

    func finishEnumeratingWithError(_ error: any Error) { finish() }

    private func finish() {
        guard !completed else { return }
        completed = true
        completion(identifiers)
    }
}
