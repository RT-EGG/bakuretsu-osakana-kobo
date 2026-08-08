# ADR-0001: アプリケーション技術構成

- 状態: Accepted
- 作成日: 2026-07-27
- 最終更新日: 2026-08-05
- 暫定候補承認日: 2026-07-28
- 最終承認日: 2026-08-05
- 決定者: 開発者
- 関連: `docs/phase2/technical-evaluation.md`

## コンテキスト

Windows 11向け動画プレイヤーとして、mp4/wmv再生、シーク、速度変更、500%音量とピーク抑制、
任意位置フレーム取得、動画上UI、複数ウィンドウ、ポータブル配布を満たす必要がある。
UI、再生、永続化を分離し、Phase 3では再生処理をモックへ交換できなければならない。
GitHubでMITソースと無償バイナリを公開するため、同梱ライブラリの再配布条件と対応ソースを
監査可能にすることも必須である。

## 決定

Phase 2最終レビューにより、次の構成を採用する。

| 領域 | 採用構成 |
| --- | --- |
| 言語・ランタイム | C# / .NET 10 LTS |
| UI | WPF、Per-Monitor V2 DPI aware |
| 再生 | `LibVLCSharp` / `LibVLCSharp.WPF` 3.10.0 |
| ネイティブ再生資産 | `VideoLAN.LibVLC.Windows` 3.0.23.1のx64資産。ただしGPL-onlyプラグイン4件を除外し、残り319件を発行ごとに監査 |
| 100%超の音声 | LibVLC `S16N` PCMコールバック、自作先読みリミッター、`NAudio.Core` / `NAudio.Wasapi` 2.3.0 |
| 永続化 | `System.Text.Json`によるバージョン付きJSON、同一ディレクトリ一時ファイルからの原子的置換 |
| 単一起動 | Named Mutex + current-user-only Named Pipe |
| 配布 | `win-x64` framework-dependent folderをZIP化。利用者が.NET 10 Desktop Runtime x64を事前導入 |

LibVLCとLibVLCSharpは交換可能な別ファイルとして動的リンクを維持する。公開バイナリと同じ
GitHub Releaseへ、VLC本体、contrib 126件、LibVLCSharp、パッケージングソースを含む検証済み
対応ソースZIP、LGPL/MIT本文、著作権表示、監査台帳を掲載する。

## 根拠

- H.264/AAC MP4は4K/60fpsまで、VC-1/WMA WMVは1080p/60fpsまで、WPF実描画、D3D11VA、
  WASAPI、左右音、A/V同期を確認した。
- 基準PCで保証上限3条件を各5回測定し、起動1500 ms、準備1000 ms、シーク1500 msの目標に
  15/15回合格した。
- 500%増幅は-1 dBFS上限、欠落・underrun・overflow 0で、MP4/WMVの自動試験と聴感試験に合格した。
- 別MediaPlayerによるサムネイル取得、低優先度生成、キャンセル、主再生との併走を実証した。
- WPFでモック再生境界、別ウィンドウ、Popup、フルスクリーン、複数DPI画面を実証した。
- 単一起動、1ファイルIPC、前面化、日本語・空白パス、JSONの原子的更新・書込不可・破損復旧を実証した。
- x64配布資産525/525件を公式VLC 3.0.23と照合し、完全な対応ソース束を発行時に再検証できる。

## 不採用・保留した構成

### WPF + libmpv

一般に入手しやすいWindowsビルドはGPLで、MITアプリのバイナリ配布方針と合わない。LGPL専用
ビルドを自前で用意する場合も、FFmpegを含む全依存の構成固定、対応ソース、再現ビルドの継続
コストが大きい。採用構成で必須機能を実証できたため比較実装は行わず、LibVLCで重大な阻害が
生じた場合の代替候補に留める。

### WinUI 3 + MediaPlayerElement / Media Foundation

Windows 11との統合には優れるが、対応コーデックがOS環境に依存し、500%増幅とピーク抑制、
独立サムネイル生成、詳細な再生制御に追加バックエンドが必要となる可能性が高い。WPF + LibVLCで
UI・再生・配布要件を満たしたため、Windows App SDKと追加メディア経路を増やす比較実装は行わない。

### Avalonia等

初回リリースはWindows 11品質を優先する。WPFで要件を満たせない場合、または他OS対応の優先度が
上がった場合に再評価する。

## 配布上の必須条件

- `VideoLAN.LibVLC.Windows.GPL`を参照しない。
- GPL-onlyプラグイン4件を除外し、残り319件のライセンス関数を自動検査する。
- 採用NuGetと発行資産のハッシュ・バージョンを固定し、更新時はライセンスと機能を再監査する。
- LibVLC/LibVLCSharpの利用、LGPL条件、交換可能性、対応ソース入手方法をREADMEとnoticeへ記載する。
- 公開バイナリと検証済み対応ソースZIPを同じGitHub Releaseに置く。
- READMEのHow To Useで.NET 10 Desktop Runtime x64、ZIP展開、起動方法を案内する。

## 残存リスクと範囲外

- 4K WMVは性能保証外。HEVC、VP9、VFRは保証外のベストエフォートである。
- 速度切替直後には約100～1,000 msの短い音飛びがあり、シームレス切替を保証しない。
- 100%超音量は自作DSPとWASAPI経路を製品実装し、OS音量ガード、停止手段、回帰試験を維持する。
- 4Kサムネイル生成はCPU負荷が高いため、逐次・低優先度・キャンセル可能なワーカーとする。
- WPF Popupの最終的な上下間隔とマウス透過はPhase 3/4で調整する。
- 初回リリースでは最低CPU・GPU・メモリを定義しない。基準PCは検証済み環境であり、他環境はベストエフォートとする。
- 新しいLibVLC/LibVLCSharp/NAudioバージョンへ更新するときは、発行監査と関連スパイクを再実行する。

## 影響

Phase 3は`IPlaybackBackend`相当のモック境界を維持したWPF UIとして進める。Phase 4は本ADRの
バージョンと配布ゲートを基準に実装し、クリーンなWindows 11環境で導入・起動・必須シナリオを
検証する。2026-08-05の開発者最終レビューで本構成と残存リスクが承認された。Phase 3以降は
本ADRを技術基準とする。
