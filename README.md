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
- Windows単体exe、iOS Simulator向けビルド、AltStore向け未署名IPAの作成をGitHub Actionsで行います。

## 開発環境

- Windows 11: .NET 10 SDK
- iOS: Windows上でSwiftを編集し、GitHub ActionsのmacOS 15 runnerとXcodeでビルドします。ローカルMacは不要です。
- AltStore ClassicはIPAをApple IDの開発証明書で再署名してiPhoneへ入れます。GitHub Actionsでは未署名IPAを作るため、Apple署名用Secretsは不要です。

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

`.github/workflows/build.yml`はWindowsサーバーを単体exeへ公開し、macOS runnerでXcodeGenからXcodeプロジェクトを生成して、アプリとFile Provider ExtensionをiOS Simulator向けとiPhone向けにビルドします。iPhone向けビルドは未署名IPAとしてArtifactに出力します。

Workflow実行後に`PCFileBridge-AltStore-Unsigned-IPA` Artifactをダウンロードし、AltStoreでインストールしてください。AltStoreがApple IDを使って再署名するため、Apple Development証明書をGitHubへ登録する必要はありません。無料のApple IDには署名可能なアプリ数や利用可能な権限の制約があります。File Provider extensionが使うApp Group権限をAltStoreのチームが許可できない場合、「ファイル」アプリへの追加やアプリ間共有が動作しません。

指定されたStudyQuiz IPAと同じアプリBundle ID (`com.studyquiz.dailyquiz`) を使っています。このIDでAltStoreからインストールすると、端末上のStudyQuizアプリを置き換えます。別アプリとして併存させる場合は、Apple Developer側で別のApp IDを用意し、`IOS_APP_BUNDLE_ID`と関連設定を変更してください。

## 接続と同期の制約

初期版のLAN APIはHTTPです。Bearerトークンは暗号化されないため、信頼できる自宅LANでのみ使用してください。公衆Wi-Fiへ直接公開しないでください。外出先ではTailscale等のVPN内アドレスを手入力できますが、インターネットへポート転送する構成は提供していません。HTTPSを有効化する場合は、証明書の配布とiOS側の信頼／ピン留めを追加する必要があります。

Replicated File ProviderがFilesで取得したファイルはiOSのローカルキャッシュに残り、オフラインでもその取得済みコピーを開けます。未取得ファイルはオンライン接続が必要です。PC上の外部変更はFileSystemWatcherで差分記録し、アプリ前面中は10秒間隔で変更列挙を通知します。APNsや常時バックグラウンド接続は含まれないため、アプリ終了中の即時更新は保証しません。Filesでフォルダーを再表示／更新すると同期要求が発生します。

共有ルート直下のフォルダーは公開対象そのものなので、iPhoneから名前変更・移動・削除を許可しません。各共有ルートの中では通常のファイル操作を行えます。Working SetはOSがmaterializeした項目を追跡します。最近使った項目、タグ、お気に入りは別途管理していません。

## API

## PC全体の表示

Windows版の「全ドライブを表示」で、現在利用できるCドライブ・外付けドライブ等を読み取り専用で登録できます。起動時に `--share-all-drives` を指定しても登録できます。設定は保存されるので次回の起動でも引き継がれます。新しいドライブを接続した場合はもう一度このボタンを押してください。

ファイル一覧はフォルダーを開くたびに取得します。Windowsの権限で読めない場所や、切断されたドライブは利用できません。全ドライブから追加した共有では、書き込みを許可したフォルダー以外への作成・上書き・移動・削除をPC側でも拒否します。表示を止めるにはWindows側の一覧でドライブを選択して削除します（実際のファイルは削除しません）。

### 指定フォルダーだけ書き込みを許可

「書き込みフォルダーを追加」で選んだフォルダーとその配下だけ、保存・上書き・名前変更・移動・削除を許可できます。設定はローカルの `writable-folders.json` に保存します。「書き込み許可を解除」で追加した許可を取り消せます。起動時に `--allow-write-folder "フォルダーの絶対パス"` を指定することもできます。

フォルダー外への移動や、リンクを通じた許可範囲外への書き込みは拒否します。許可の起点フォルダー自体は移動・削除しません。Windowsのアクセス権限とファイルのロックも引き続き適用されます。

## APIの詳細

エンドポイント、JSON形式、変更アンカーは[protocol/api-spec.md](protocol/api-spec.md)を参照してください。


## Tailscaleと入力不要の接続

### QRコードで登録

1. Windows版の「接続QRを表示」を押します。
2. iPhoneでmain → 1 →「QRコードで接続」を開き、カメラを許可します。
3. PC画面のQRを読み取ると登録し、接続情報を保存します。次回からの手入力は不要です。

QRにはプライベートアドレスと期限付きの初回コードだけを含めます。QR画像や初回コードをサーバーの公開APIへ出力せず、画面表示用にPC内のメモリで生成します。期限切れや登録後は表示中のQRを自動で更新します。iPhone側は期限と接続先のプライベートアドレス範囲を確認します。

QRの生成には[QRCoder 1.7.0](https://www.nuget.org/packages/QRCoder/1.7.0)（MITライセンス）を使用しています。

PCとiPhoneで同じTailscaleへ接続すると、PC画面の「外出先」アドレスから利用できます。PCの起動とサーバーの実行、Tailscaleの接続が必要です。接続後のトークンをiPhoneが保存するため、毎回コードを入力する必要はありません。

Windows版の「初回の接続設定を書き出す」、または`--export-connection 出力先.json`で、10分以内の初回ペアリング設定を作れます。CLIは現在のWindowsユーザーだけが接続できる名前付きパイプ経由で、起動済みサーバーから設定を受け取ります。設定には期限付きコードを含むため公開しないでください。main側の配布補助スクリプトは、改造版で署名する直前にPC内だけで設定をIPAへ追加します。
