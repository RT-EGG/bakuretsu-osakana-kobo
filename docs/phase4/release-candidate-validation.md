# Phase 4 初回リリース候補ローカル検証

実施日: 2026-08-09
対象: `codex/phase4-app-foundation`の初回リリース候補

## 結論

製品のframework-dependent `win-x64`フォルダーとバイナリZIPを生成し、ローカル品質ゲートに合格した。
GitHub Releaseへの公開とクリーンなWindows 11環境での受け入れ確認は、このローカル検証には含めない。

## 自動品質ゲート

`scripts/build-release-candidate.ps1`を検証済み対応ソースZIP付きの`-ValidationOnly`で実行した。

- locked restore: 成功
- Release build: 警告0、エラー0
- 自動テスト: 45/45合格
- 配布形式: .NET 10 Desktop Runtime x64を前提とするframework-dependent folder
- 配布台帳: 441ファイル、104,055,217 bytes（`release-manifest.json`自身を除く）
- LibVLCプラグイン: 319件、ライセンス読取不能0件、GPL/AGPL 0件
- 既知のGPL-onlyプラグイン4件: 0件
- 必須文書: README、MIT本文、第三者通知、LGPL本文、対応ソース案内、対応形式、依存一覧をZIP内で確認

| アセット | bytes | SHA-256 |
| --- | ---: | --- |
| `BakuretsuOsakanaKobo-win-x64.zip` | 47,691,246 | `FB85E3CD13C81961B7FC1A28DC7F2E6356CDEBCB598B332CE02807EA92F73098` |
| `bakuretsu-osakana-kobo-libvlc-sources-3.0.23.1.zip` | 450,885,351 | `E72CC13E5B0D0015B8B1944D4353E4D1645C1870B38F616DB95FD9DC28927F29` |

ローカル成果物は`.tmp/phase4-release-candidate-reviewed`に生成した。`.tmp`成果物はGit管理しないため、
公開時は同じスクリプトを`-ValidationOnly`なしで再実行し、生成された2アセットを同じGitHub Releaseへ置く。

## 製品性能

`scripts/measure-phase4-product-performance.ps1 -Runs 5`により、製品`MainWindow`、
`LibVlcPlaybackBackend`、製品シークバーを別プロセスで実行した。音声はメディアを開く前にミュートし、
オープン準備はLibVLC Voutと再生時計100ms到達の遅い方とした。詳細値は
`docs/phase4/results/product-performance-measurement.json`を正とする。

| 条件 | 実行 | 起動最大 | オープン最大 | 50%シーク最大 | 90%シーク最大 |
| --- | ---: | ---: | ---: | ---: | ---: |
| H.264/AAC MP4 1080p/60fps | 5/5 | 1,060.416 ms | 667.701 ms | 488.427 ms | 363.541 ms |
| H.264/AAC MP4 4K/60fps | 5/5 | 923.212 ms | 807.766 ms | 490.894 ms | 998.667 ms |
| VC-1/WMA WMV 1080p/60fps | 5/5 | 816.359 ms | 557.952 ms | 933.536 ms | 942.316 ms |

目標は起動1,500ms、オープン1,000ms、各シーク1,500msであり、15/15回すべて合格した。
初回オープンで既存再生を保護する必要がない場合は一時プレイヤー検査を省き、解析・コーデック検査後に
製品プレイヤーへ直接渡す。既存動画を維持すべき動画切替時は一時プレイヤー検査を維持する。

## 実ウィンドウ

最終配布フォルダーの`BakuretsuOsakanaKobo.exe`を起動し、タイトル、ウィンドウハンドル、応答状態を
確認後、通常のウィンドウ終了要求で5秒以内に終了コード0となることを確認した。性能検証15回も
製品WPF実ウィンドウとD3D11VA経路で完了した。音声はミュート固定であり、実音声・聴感品質は
Phase 2の合格証跡と区別する。

## 残るリリース判定

- クリーンなWindows 11 x64環境へ.NET 10 Desktop Runtime x64を導入し、ZIP展開から必須シナリオを確認する。
- 実際のGitHub ReleaseでバイナリZIPと対応ソースZIPを同時公開し、公開アセットのハッシュを再確認する。
- 開発者が初回リリース候補を受け入れる。
