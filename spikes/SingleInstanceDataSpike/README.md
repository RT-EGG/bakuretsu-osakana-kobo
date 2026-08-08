# 単一起動・ポータブルJSON技術スパイク

Phase 2 `P2-REM-05`向けに、次を検証するWPFスパイク。

- Named Mutexで同一利用者セッションのプロセスを1つに制限
- Current-user-only Named Pipeで0個または1個のファイル引数を既存プロセスへ転送
- 2個以上のファイル引数は既存状態を維持したまま無視
- IPC受信時の既存ウィンドウ復元・前面化
- 日本語と空白を含むWindowsパス
- 実行ファイル相当ディレクトリ直下の`data`へバージョン付きJSONを保存
- 同一ディレクトリの一時ファイルをflushしてから`File.Replace`する原子的更新
- 不完全な一時ファイル、壊れたJSON、書込不可時の安全な継続

外部パッケージは使用しない。

```powershell
dotnet build spikes/SingleInstanceDataSpike/SingleInstanceDataSpike.csproj -c Release
```

データ検証モードはWPF画面を表示しない。

```powershell
SingleInstanceDataSpike.exe `
  --data-validation-report .tmp/single-instance-data/data-report.json `
  '.tmp/single-instance-data/ポータブル 配置'
```

単一起動検証では最初のプロセスだけがWPF画面を表示する。検証ごとに一意な`--instance-id`を使う。

```powershell
./scripts/test-phase2-single-instance.ps1
```

自動検証はIPC受信と前面化処理の実行までを判定する。`GetForegroundWindow`の観測値は
テストランナーがフォーカスを取り返す場合があるため診断情報に留め、実際の前面化は手動で確認する。
