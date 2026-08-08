# WPF UI・ウィンドウ技術スパイク

Phase 2 `P2-REM-04`向けに、再生エンジンへ依存しないWPF UI・ウィンドウ機能を確認する。
外部パッケージは使用せず、`MockPlaybackBackend`を`IPlaybackBackend`経由で接続する。

確認対象:

- メイン画面とモック再生コントロール
- 独立したプレイリストウィンドウ
- シークバーホバーのモックサムネイル`Popup`
- 現在のディスプレイでのフルスクリーンと復帰
- `Esc`、`Alt+Enter`、ダブルクリック、右クリックメニュー
- 複数ディスプレイへの移動、DPI取得、フォーカス・前面化

ビルド:

```powershell
dotnet build spikes/WpfWindowingSpike/WpfWindowingSpike.csproj -c Release
```

自動検証は実際のウィンドウを表示して移動するため、開発者へ事前に案内してから実行する。

```powershell
spikes/WpfWindowingSpike/bin/Release/net10.0-windows/win-x64/WpfWindowingSpike.exe `
  --validation-report .tmp/wpf-windowing/report.json
```

ディスプレイが1台だけの場合、別ディスプレイへの移動とDPI差確認は`Skipped`として記録する。
