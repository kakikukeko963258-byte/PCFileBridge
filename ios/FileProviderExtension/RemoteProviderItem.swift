import FileProvider
import UniformTypeIdentifiers

final class RemoteProviderItem: NSObject, NSFileProviderItemProtocol {
    let remote: BridgeItem

    init(_ remote: BridgeItem) { self.remote = remote }

    var itemIdentifier: NSFileProviderItemIdentifier { NSFileProviderItemIdentifier(rawValue: remote.id) }
    var parentItemIdentifier: NSFileProviderItemIdentifier {
        remote.parentId == "root" ? .rootContainer : NSFileProviderItemIdentifier(rawValue: remote.parentId)
    }
    var filename: String { remote.name }
    var contentType: UTType {
        if remote.isDirectory { return .folder }
        return UTType(filenameExtension: URL(fileURLWithPath: remote.name).pathExtension) ?? .data
    }
    var capabilities: NSFileProviderItemCapabilities {
        if remote.isDirectory {
            let base: NSFileProviderItemCapabilities = [.allowsReading, .allowsContentEnumerating, .allowsAddingSubItems]
            return remote.parentId == "root" ? base : base.union([.allowsDeleting, .allowsRenaming, .allowsReparenting])
        }
        return [.allowsReading, .allowsWriting, .allowsDeleting, .allowsRenaming, .allowsReparenting, .allowsEvicting]
    }
    var documentSize: NSNumber? { remote.isDirectory ? nil : NSNumber(value: remote.size) }
    var contentModificationDate: Date? { remote.modifiedUtc }
    var itemVersion: NSFileProviderItemVersion {
        NSFileProviderItemVersion(contentVersion: Data(remote.version.utf8), metadataVersion: Data(remote.modifiedUtc.timeIntervalSince1970.description.utf8))
    }
}
