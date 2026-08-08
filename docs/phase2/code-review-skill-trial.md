# コードレビュースキル作成・試行

実施日: 2026-08-05  
対象: `P2-REM-08`  
結果: 合格

## 作成物

プロジェクト固有スキルを`.agents/skills/review-dotnet-media-app`へ作成した。

- `SKILL.md`: レビュー準備、状態・所有権の追跡、優先度、報告形式
- `references/project-review-constraints.md`: 固定バージョン、ライセンス・発行、WPF、
  LibVLC/NAudio、永続化、IPC、保証範囲の不変条件
- `agents/openai.yaml`: Codex UI向けの表示名、説明、既定プロンプト

`skill-creator`の`quick_validate.py`で構造、frontmatter、命名を検証し、合格した。

## 試行対象

`spikes/SingleInstanceDataSpike`を、作成したスキルの手順でレビューした。成功系だけでなく、
起動直後の順序、不正長、無効JSON、途中切断、待受継続、終了処理を追跡した。

## 検出・反映した改善

### P2: 不正クライアント1件でIPC待受全体が停止する

`SingleInstanceCoordinator.RunServerAsync`は、不正なpayload長、JSON例外、途中切断をクライアント
単位で処理していなかった。そのため同一利用者の壊れた二次プロセス1件でサーバータスクがfaultし、
以後の正常な二次起動も5秒後に失敗する状態だった。

各クライアントを例外境界で分離し、不正要求はACK 0と診断イベントを返して次の接続待受へ戻すよう
修正した。`FileArguments: null`も不正要求として拒否する。

### P2: 初回状態と二次起動要求の処理順が未定義

一次プロセスは初回の`HandleLaunchRequestAsync`完了前にNamed Pipeを開始していたため、起動直後の
二次要求と初回状態更新が競合し、後から初回状態が二次要求の表示を上書きする可能性があった。

初回状態をDispatcherへ反映した後に待受を開始する順序へ変更した。二次プロセス側の接続再試行は
維持されるため、起動中の要求も待受開始後に処理される。

この試行を受け、スキルのIPC制約へ「クライアント障害の分離」「拒否後の正常要求」「初回状態と
二次要求の順序」を追加した。

## 回帰検証

- スキル構造検証: 合格
- Releaseビルド: 警告0、エラー0
- `scripts/test-phase2-single-instance.ps1`: 合格
- 不正payload長をACK 0で拒否: 合格
- 拒否後の0引数、1引数、複数引数IPC: 全件合格
- 日本語・空白を含むパスの完全一致: 合格
- 一次プロセスだけが残存: 合格

自動回帰レポート:
`.tmp/single-instance-data/run-20260805121127517/report.json`

## 結論

スキルはプロジェクト固有のライセンス・ネイティブ資産条件に加え、通常のビルド成功だけでは
見逃すIPC耐障害性と起動順序を検出できた。実例をチェック項目へ還元し、Phase 4の変更レビューと
依存更新・発行レビューに使用できる状態になった。

