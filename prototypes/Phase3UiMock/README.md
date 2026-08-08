# Phase 3 UIモック

レビュー単位1（メインウィンドウの基本レイアウトと主要メディア状態）と、レビュー単位2（ファイル導線）を確認するためのWPFモック。
実動画、LibVLC、音声出力、ファイル選択、永続化は使用しない。

## 起動

```powershell
dotnet run --project prototypes/Phase3UiMock/Phase3UiMock.csproj
```

右上の「レビュー操作」から、未選択、読み込み中、再生中、一時停止、エラーを切り替えられる。
「模擬オープン」または空状態の「ファイルを開く」は、約900msの読み込み表示後にモック再生へ移る。

レビュー単位2では次を操作できる。

- 「ファイル」→「開く」のWindows標準ダイアログ
- メインウィンドウへのMP4/WMVのドラッグ&ドロップ
- 複数ファイル・非対応拡張子の拒否通知
- 「最近開いたファイル」のモック履歴、欠損表示、個別削除、全消去
- 「データフォルダーを開く」の模擬通知

## 自動検証

```powershell
dotnet run --project prototypes/Phase3UiMock/Phase3UiMock.csproj -- --validate .tmp/phase3-ui-mock-validation.json
```

自動検証はウィンドウを表示せず、状態遷移、シーク境界、時刻表示を確認する。
