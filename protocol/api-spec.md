# PC File Bridge API v1

The Windows server listens on TCP port `8765`. All endpoints except `/api/health` and `/api/pair` require `Authorization: Bearer <token>`. Request and response bodies use UTF-8 JSON. Item IDs are opaque server-generated values; clients must not treat them as paths.

## Pairing and health

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/api/health` | Returns online status, PC name, port, and protocol version. |
| `POST` | `/api/pair` | Exchanges the PC's six-digit, one-time code for a long-lived device token. Body: `{"code":"123456","deviceName":"iPhone"}`. |

Pair response: `{"deviceId":"...","token":"...","serverName":"...","port":8765}`. The server stores only a SHA-256 token hash. A successful pairing rotates the displayed code.

## Items and content

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/api/items?parentId=root&limit=200&continuationToken=...` | Lists configured shared roots or direct children. `continuationToken` is opaque and omitted on the first page. |
| `GET` | `/api/items/{id}` | Reads item metadata. |
| `GET` | `/api/items/{id}/content` | Streams file bytes; supports HTTP `Range`. |
| `PUT` | `/api/items/{id}/content` | Streams replacement file bytes from the request body. |
| `POST` | `/api/items` | Creates a file or folder. Body: `{"parentId":"...","name":"notes","isDirectory":true}`. A new file is empty until its content is uploaded. |
| `PATCH` | `/api/items/{id}` | Renames or moves an item. Body: `{"name":"new-name","parentId":"destination-id"}`; either property may be `null`. |
| `DELETE` | `/api/items/{id}` | Deletes a file or directory recursively. |
| `GET` | `/api/changes?parentId={id}&after={sequence}` | Lists direct-child changes for an enumerated folder. |
| `GET` | `/api/changes?itemId={id}&after={sequence}` | Lists content changes for a document enumerator. |
| `GET` | `/api/changes?after={sequence}` | Lists changes used to update the materialized-item working set. |

`ItemPage` is `{ "items": [...], "next": "..." }`; `next` is `null` when the page is complete. Item metadata includes `id`, `parentId`, `name`, `isDirectory`, `size`, `modifiedUtc`, and a content `version` string. Change pages carry a monotonic `latestSequence` anchor.

## Access control

The server resolves IDs through its catalog and checks that each resolved path remains inside a configured root. Names must be single path components. Reparse-point links that lead outside a root are rejected. Root paths are selected in the Windows UI and are never accepted from an iPhone API request.
