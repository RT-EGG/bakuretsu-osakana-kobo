# 爆裂おさかな工房

Windows 11向けのMP4/WMV動画プレイヤーです。基本再生に加え、最大500%の音量、速度変更、
フルスクリーン、動画別設定、プレイリスト、シークサムネイルをポータブルなフォルダー構成で提供します。

## 主な機能

- MP4/WMVのファイル選択、ドラッグ&ドロップ、起動引数・Windows関連付け経由のオープン
- 再生・一時停止、5秒シーク、時刻表示、登録した開始位置からの再生
- 0～500%音量、ミュート、0.25/0.5/1.0/1.5/2.0倍速、長押し中の一時2.0倍速
- フルスクリーンと3秒後に隠れる再生コントローラー
- 最大10件の最近開いたファイルと、単一プレイリストの保存・並べ替え・ループ再生
- 設定間隔で生成するシークサムネイルとホバー位置の優先表示
- 欠損・非対応・破損動画の案内、診断ログ、壊れた設定データからの自動復旧
- 同一利用者での単一起動と、二次起動から既存ウィンドウへの動画引き渡し

## 使い方

1. Windows 11 x64へ[.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0)をインストールします。
2. GitHub Releaseから`BakuretsuOsakanaKobo-win-x64.zip`をダウンロードし、フォルダーへすべて展開します。
3. 展開した`BakuretsuOsakanaKobo.exe`を起動します。
4. 「ファイル」→「開く」からMP4またはWMVを1本選択します。メイン画面への1ファイルドロップや、
   Windowsのファイル関連付けから開くこともできます。読み込みに成功すると自動再生します。
5. 画面下部で再生、一時停止、シーク、音量、ミュート、速度を操作します。動画面の右クリックから、
   再生速度、開始位置の登録、フルスクリーンも選択できます。
6. 「表示」メニューからプレイリストとサムネイル設定を開けます。

### キーボード操作

| キー | 動作 |
| --- | --- |
| `Space` | 再生・一時停止 |
| `←` / `→` | 5秒戻る・進む |
| `↑` / `↓` | 再生速度を1段階上げる・下げる |
| `Alt+Enter` | フルスクリーン切替 |
| `Esc` | フルスクリーン終了 |

設定と診断ログは実行ファイルと同じ場所の`data`フォルダーへ保存します。書き込み可能な場所へ
フォルダーごと展開してください。`Program Files`など通常利用者が書き込めない場所は推奨しません。

配布物内のLibVLCおよびLibVLCSharpは別ファイルのまま同梱されます。第三者ライセンス、除外した
プラグイン、対応ソースの情報は`THIRD-PARTY-NOTICES.md`、`CORRESPONDING-SOURCE.md`、
`release-manifest.json`を参照してください。対応ソースZIPはバイナリZIPと同じGitHub Releaseに
別アセットとして掲載されます。

## 対応形式

保証するMP4/WMVの内部コーデック、解像度、fps、および保証外の扱いは、
[`docs/supported-media-formats.md`](docs/supported-media-formats.md)を参照してください。

## Development

Windows上で、`global.json`に完全固定された.NET SDK 10.0.400を使用します。

```powershell
dotnet restore BakuretsuOsakanaKobo.slnx --locked-mode
dotnet build BakuretsuOsakanaKobo.slnx --configuration Release --no-restore
dotnet test BakuretsuOsakanaKobo.slnx --configuration Release --no-build
```

依存関係を変更した場合は、通常の`dotnet restore BakuretsuOsakanaKobo.slnx`で
各`packages.lock.json`を更新し、内容をレビューしてください。

## Release validation

検証済み対応ソースZIPを用意したうえで、製品のローカルリリース候補を生成します。

```powershell
.\scripts\build-release-candidate.ps1 `
  -OutputDirectory .tmp\release-candidate `
  -CorrespondingSourceArchive .tmp\bakuretsu-osakana-kobo-libvlc-sources-3.0.23.1.zip `
  -ValidationOnly

.\scripts\measure-phase4-product-performance.ps1 -Runs 5
```

前者はlocked restore、Release build・test、framework-dependent publish、LibVLCライセンス監査、
対応ソース再検証、ファイル台帳、バイナリZIPを生成します。`-ValidationOnly`を外す公開用実行では、
対応ソースZIPの指定が必須です。後者は製品WPFを音声ミュート固定で別プロセス起動し、代表3動画を
各5回測定します。
