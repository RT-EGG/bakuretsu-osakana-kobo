# Phase 4 クリーン環境受け入れ手順

## 目的

初回リリースの公開候補を、開発環境とは別のクリーンなWindows 11 x64環境で検証する。
これは導入、依存関係、初回リリースの必須シナリオを確認するもので、最低ハードウェア性能の証明ではない。

## 前提

- 新規または初期状態へ戻せるWindows 11 x64の実機・VMを使用する。
- .NET 10 Desktop Runtime x64だけを事前導入する。LibVLCや開発SDKは導入しない。
- 保証対象のH.264/AAC MP4とVC-1/WMA 9 Professional WMVを各1本用意する。
- `build-release-candidate.ps1`を`-ValidationOnly`なしで実行して生成した次の3ファイルを同じフォルダーへコピーする。
  - `BakuretsuOsakanaKobo-win-x64.zip`
  - `bakuretsu-osakana-kobo-libvlc-sources-3.0.23.1.zip`
  - `release-assets.json`

Windows Sandboxは終了時に状態が失われるため使用してもよいが、上記ランタイムとアセットを毎回明示的に導入・コピーする。

## 実行

リポジトリの`test-phase4-clean-environment.ps1`も検証環境へコピーし、通常ユーザーのPowerShellから実行する。

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\test-phase4-clean-environment.ps1 -ReleaseAssetDirectory C:\ReleaseAssets
```

スクリプトは次を自動確認する。

- Windows 11 x64と.NET 10 Desktop Runtime x64
- マニフェストに対する両ZIPのサイズとSHA-256
- ZIP展開と製品ウィンドウの起動・応答・通常終了
- README、ライセンス、第三者通知、対応ソース案内、対応形式、依存台帳
- LibVLCプラグイン319件と既知のGPL-only 4件が含まれないこと

続いて表示される質問に従い、MP4/WMVの自動再生、再生・一時停止・再開、シーク・時刻表示、
破損MP4のエラー表示と継続動作、ライセンス文書の閲覧を確認する。

## 証跡と合格条件

既定では`%TEMP%\BakuretsuOsakanaKobo-Acceptance\<UTC日時>`へ
`clean-environment-acceptance.json`を保存する。`passed`が`true`であり、`automatedOnly`が`false`、
すべての`manual`項目が`true`であることを受け入れ条件とする。

公開時は、このJSONが記録するものと同じSHA-256のバイナリZIPと対応ソースZIPを、同じGitHub Releaseへ掲載する。
公開後にGitHubから両ZIPを再取得してハッシュを照合し、開発者承認を得てP4-REL-02を完了する。
