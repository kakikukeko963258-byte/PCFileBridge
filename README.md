# PC File Bridge

Windows 11で選択したフォルダーを、iPhoneのFilesアプリの「場所」に登録して使うためのプロジェクトです。Windows側は.NET 10/WPF、iPhone側はSwiftUIとiOS 16以降の`NSFileProviderReplicatedExtension`で構成しています。

## 現在の実装

- Windows GUIから共有ルートを追加・削除し、ランダムな6桁ペアリングコードを表示します。共有ルート外はAPIから指定できません。
- LAN内HTTP APIはペアリングコードを長いランダムBearerトークンに交換し、トークンのハッシュだけをPCに保存します。ペアリング試行は5回失敗で1分間停止します。
- mDNSで`_pcfilebridge._tcp`を広告し、iPhoneアプリが同じネットワーク上のPCを検出します。手入力アドレスも使えます。
- ファイルIDはカタログで保存し、API経由の名前変更・移動後も維持します。ファイル一覧は継続トークンでページングします。
- ダウンロードはHTTP Rangeに対応し、アップロードはディスクへストリーミングします。APIはWindows上の明示選択ルートだけを公開し、パストラバーサルと外部へ向くリンクを拒否します。
- iPhoneアプリはトークンをKeychainに保存し、共有App Groupを通じてFile Provider Extensionに設定を渡します。PC名のFile Provider DomainをFilesへ追加／削除できます。
- iPhoneアプリ内にもページング付きのファイル一覧を用意しています。接続確認とフォルダー閲覧に使えます。
- File Providerは閲覧、必要時のダウンロード、キャッシュ済みファイルの利用、作成、上書き、削除、名前変更、移動に対応します。Windows側FileSystemWatcherの差分履歴をFile Providerの変更列挙で返します。
- Replicated File ProviderがOS上で管理するmaterialized項目をWorking Setに保持し、Files側キャッシュにある内容をオフラインで利用できるようにします。
- iPhoneアプリが前面にある間は10秒間隔で差分APIを確認し、変更されたフォルダー、項目、Working Setの列挙をFile Providerへ通知します。差分だけを取得するため、全件の再取得を繰り返しません。
- Windows単体exeの公開と、iOS Simulator向けビルドをGitHub Actionsで行います。Apple署名用Secretsを設定した場合はAltStore用IPAもArtifactに出力します。

## 開発環境

- Windows 11: .NET 10 SDK
- iOS: Windows上でSwiftを編集し、GitHub ActionsのmacOS 15 runnerとXcodeでビルドします。ローカルMacは不要です。
- AltStoreへiPhoneに入れるには、Apple Development証明書と対象iPhoneを含むProvisioning Profileが必要です。GitHub Actions上で署名する場合は、後述のSecretsを登録してください。

## Windowsサーバーを起動する

```powershell
dotnet run --project windows/PCFileBridge.Windows/PCFileBridge.Windows.csproj
```

ウィンドウで「フォルダーを追加」を押し、公開するフォルダーだけを選びます。サーバーはTCP `8765`、mDNSはUDP `5353`を使います。Windows Defender Firewallの許可ダイアログが出た場合、自宅／プライベートネットワークに許可してください。PCの休止中は接続できません。

共有ルートやペアリング端末情報は`%LOCALAPPDATA%\PCFileBridge`に保存します。共有フォルダー内のファイルを読む・変更・削除する権限は、Windowsアプリを実行しているユーザーの権限に従います。

## iPhoneを接続する

1. Windowsサーバーを起動して共有フォルダーを登録します。
2. iPhoneアプリを起動し、見つかったPCを選ぶか、PCのIPv4アドレスと`:8765`を入力します。
3. Windows画面の6桁コードを入力してペアリングします。
4. iPhoneのFilesアプリで「ブラウズ」→「場所」を開き、PC名を選びます。

手動入力例: `192.168.1.20:8765`。ペアリング成功後、コードは再生成されます。iPhoneアプリからFilesのDomainを削除すると、対応するKeychainトークンも消します。

## ローカルでWindows単体exeを公開する

```powershell
dotnet publish windows/PCFileBridge.Windows/PCFileBridge.Windows.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o artifacts/windows
```

生成される`PCFileBridge.Windows.exe`は.NETランタイムを別途インストールせずに起動できます。初回起動時はWindows FirewallのTCP `8765`とUDP `5353`を許可してください。

## GitHub ActionsとIPA

`.github/workflows/build.yml`はWindowsサーバーを単体exeへ公開し、macOS runnerでXcodeGenからXcodeプロジェクトを生成して、アプリとFile Provider ExtensionをiOS Simulator向けにビルドします。署名Secretsがない場合も、シミュレータ用ビルドはArtifactに出ます。

AltStore用IPAを作る場合、GitHub repository secretsに次を登録して`workflow_dispatch`を実行してください。

| Secret | 内容 |
| --- | --- |
| `IOS_CERTIFICATE_BASE64` | Apple Development証明書入り`.p12`のBase64 |
| `IOS_CERTIFICATE_PASSWORD` | `.p12`のパスワード |
| `IOS_APP_PROVISIONING_PROFILE_BASE64` | iOSアプリ用Development Provisioning ProfileのBase64 |
| `IOS_EXTENSION_PROVISIONING_PROFILE_BASE64` | File Provider Extension用ProfileのBase64 |
| `IOS_TEAM_ID` | Apple Developer Team ID |
| `IOS_APP_BUNDLE_ID` | アプリ用Bundle ID。添付IPAに合わせた初期値: `com.studyquiz.dailyquiz` |
| `IOS_EXTENSION_BUNDLE_ID` | Extension用Bundle ID。初期値: `com.studyquiz.dailyquiz.FileProvider` |
| `IOS_APP_GROUP` | 両Profileに含めたApp Group。初期値: `group.com.studyquiz.dailyquiz` |
| `IOS_KEYCHAIN_GROUP` | 両Profileに含めた共有Keychain access group。初期値: `<TeamID>.com.studyquiz.dailyquiz.shared` |
| `IOS_APP_PROFILE_NAME` | アプリProvisioning Profile内の`Name` |
| `IOS_EXTENSION_PROFILE_NAME` | Extension Provisioning Profile内の`Name` |

Provisioning Profile、Bundle ID、App Group、Keychain groupの値は署名設定で一致させてください。ExtensionのProfileにもApp Groupと共有Keychain groupが必要です。作成された`PCFileBridge-AltStore-IPA` Artifactをダウンロードし、AltStoreでインストールします。Development ProfileにはiPhoneのUDIDを登録してください。

指定されたStudyQuiz IPAと同じアプリBundle ID (`com.studyquiz.dailyquiz`) を使っています。このIDでAltStoreからインストールすると、端末上のStudyQuizアプリを置き換えます。別アプリとして併存させる場合は、Apple Developer側で別のApp IDを用意し、`IOS_APP_BUNDLE_ID`と関連設定を変更してください。

## 接続と同期の制約

初期版のLAN APIはHTTPです。Bearerトークンは暗号化されないため、信頼できる自宅LANでのみ使用してください。公衆Wi-Fiへ直接公開しないでください。外出先ではTailscale等のVPN内アドレスを手入力できますが、インターネットへポート転送する構成は提供していません。HTTPSを有効化する場合は、証明書の配布とiOS側の信頼／ピン留めを追加する必要があります。

Replicated File ProviderがFilesで取得したファイルはiOSのローカルキャッシュに残り、オフラインでもその取得済みコピーを開けます。未取得ファイルはオンライン接続が必要です。PC上の外部変更はFileSystemWatcherで差分記録し、アプリ前面中は10秒間隔で変更列挙を通知します。APNsや常時バックグラウンド接続は含まれないため、アプリ終了中の即時更新は保証しません。Filesでフォルダーを再表示／更新すると同期要求が発生します。

共有ルート直下のフォルダーは公開対象そのものなので、iPhoneから名前変更・移動・削除を許可しません。各共有ルートの中では通常のファイル操作を行えます。Working SetはOSがmaterializeした項目を追跡します。最近使った項目、タグ、お気に入りは別途管理していません。

## API

エンドポイント、JSON形式、変更アンカーは[protocol/api-spec.md](protocol/api-spec.md)を参照してください。
