# 爆裂おさかな工房

Windows 11向けの動画プレイヤーです。初回リリースでは、MP4/WMVのオープン、自動再生、
再生・一時停止、シーク、現在時刻・全体時間表示を提供します。

## 使い方

1. Windows 11 x64へ[.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0)をインストールします。
2. GitHub Releaseから`BakuretsuOsakanaKobo-win-x64.zip`をダウンロードし、フォルダーへすべて展開します。
3. 展開した`BakuretsuOsakanaKobo.exe`を起動します。
4. 「ファイル」→「開く」からMP4またはWMVを1本選択します。読み込みに成功すると自動再生します。
5. 画面下部のボタンで再生・一時停止を切り替え、シークバーで再生位置を変更します。

設定と診断ログは実行ファイルと同じ場所の`data`フォルダーへ保存します。書き込み可能な場所へ
フォルダーごと展開してください。`Program Files`など通常利用者が書き込めない場所は推奨しません。

配布物内のLibVLCおよびLibVLCSharpは別ファイルのまま同梱されます。第三者ライセンス、除外した
プラグイン、対応ソースの情報は`THIRD-PARTY-NOTICES.md`、`CORRESPONDING-SOURCE.md`、
`release-manifest.json`を参照してください。対応ソースZIPはバイナリZIPと同じGitHub Releaseに
別アセットとして掲載されます。

## 対応形式

初回リリースで保証するMP4/WMVの内部コーデック、解像度、fps、および保証外の扱いは、
[`docs/supported-media-formats.md`](docs/supported-media-formats.md)を参照してください。

## Development

Windows上で、`global.json`に固定された.NET 10 SDKを使用します。

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
