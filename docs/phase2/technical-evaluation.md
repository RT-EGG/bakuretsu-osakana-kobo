# Phase 2 技術構成評価

最終更新日: 2026-08-05  
状態: 完了・開発者承認済み  
関連仕様: `docs/specifications/specs.md` 1.0

## 1. 目的

Phase 2で比較・実証する技術構成と、採否を決めるための検証順序を定める。
初期仮説を代表動画によるWindows 11実機スパイク、配布物測定、ライセンス確認で検証し、
2026-08-05の開発者レビューで最終採用した結果を記録する。

## 2. 現時点の前提

- 対象OSはWindows 11で、初回リリースはインストーラーを必須としない。
- 最初のリリースは、mp4/wmvのオープン、自動再生、再生・一時停止、シーク、時刻表示に絞る。
- 将来機能として、500%音量とピーク抑制、速度変更、サムネイル取得、動画上のコントロール、
  別プレイリストウィンドウ、フルスクリーン、単一起動が必要である。
- UI、再生バックエンド、永続化をインターフェースで分離し、Phase 3では再生処理をモックへ
  差し替えられる構成にする。
- リポジトリはMIT Licenseで公開されているため、同梱するネイティブライブラリのライセンス、
  ビルド設定、再配布条件を配布方式ごとに確認する。

## 3. 固定候補

### 3.1 言語・ランタイム

第一候補をC# / .NET 10 LTSとする。

根拠:

- WindowsデスクトップUI、非同期処理、JSON永続化、テスト、単一起動IPCを一つの言語で扱える。
- .NET 10は2028-11-14までサポートされるLTSである。
- 自己完結型と単一ファイル発行を選択できる。ただしネイティブ再生ライブラリを含めた
  真の単一ファイル化は別途検証が必要である。
- 開発環境には.NET SDK 10.0.301とWindows Desktop Runtimeが導入済みである。

### 3.2 永続化

第一候補を `System.Text.Json` によるバージョン付きJSONファイルとする。

- `data/settings.json`、`data/profiles.json`、`data/playlist.json` のように用途を分離する。
- 一時ファイルへ書き込み、同一ボリューム上で置換する原子的更新をスパイクする。
- DTOにスキーマバージョンを持たせ、不正データを項目単位で既定値へ戻せるか検証する。
- 初回規模では組み込みDBより配布、閲覧、復旧、ポータブル移動が単純である。

## 4. 比較する3構成

| 構成 | UI | 再生バックエンド | 強み | 主なリスク | 初期位置づけ |
| --- | --- | --- | --- | --- | --- |
| A | WPF / .NET 10 | libmpv | 多形式、速度、音声フィルター、フレーム取得を一つの再生エンジンで検証できる。libmpvは別アプリへの組み込み用途が明記されている | 通常ビルドはGPL。LGPL専用Windowsビルドの由来、全依存、対応ソース、再現可能なビルドを管理する負担が大きい | ライセンス条件により予備候補へ降格 |
| B | WPF / .NET 10 | LibVLCSharp + LibVLC | C# APIとWPF用VideoViewがあり、.NET 10でのビルド・起動を実証済み | 非GPL版NuGetにもGPL-onlyプラグイン4件が混入するため除外と全件監査が必須。500%増幅、フレーム取得、透明オーバーレイの実機挙動も要検証 | サニタイズ条件付きの暫定第一候補 |
| C | WinUI 3 / .NET 10 | MediaPlayerElement / Media Foundation | Windows 11との統合、標準UI・アクセシビリティ、ウィンドウAPIとの親和性が高い | コーデックがOS環境に依存しやすく、500%音量、音声フィルター、バックグラウンドフレーム取得の自由度が低い。自己完結配布のサイズと初回展開も要検証 | 基準・撤退候補 |

ライセンス事前監査の詳細は `docs/phase2/license-review.md` を参照する。

Avaloniaは将来の他OS対応には有利だが、初回のWindows 11品質が優先であり、再生面では結局
ネイティブ埋め込みとオーバーレイの検証が必要になる。Phase 2の候補数を増やしすぎないため、
現時点では固定3構成から外す。WPFで要求を満たせない、または他OS対応の優先度が上がった場合に
再評価する。

## 5. 評価仮説

5段階の数値評価はスパイク実測後に行う。机上調査だけで点数を確定しない。

| 評価軸 | A: WPF + libmpv | B: WPF + LibVLCSharp | C: WinUI 3 + Media Foundation |
| --- | --- | --- | --- |
| mp4/wmv互換性 | 高い見込み | 高い見込み | OS・追加コーデック依存を要確認 |
| 0.25～2.0倍速 | 対応見込み。音声ピッチ補正を実測 | 定常品質はMP4/WMVで主観合格。切替直後は約100～1,000 msのPCM供給間隔があり、設定調整では解消せず、上流issueも未解決 | 対応可否と音声品質を形式別に実測 |
| 500%＋ピーク抑制 | 音声フィルターで構成可能な見込み | 標準音量APIだけでは不足する可能性 | 標準APIだけでは不足する可能性が高い |
| 任意位置フレーム取得 | コマンド・デコード経路を検証 | 映像出力コールバック等を検証 | 別のMediaClip/FFmpeg併用が必要な可能性 |
| WPF/WinUIオーバーレイ | 描画方式次第。最大リスク | 専用回避策あり。複数ウィンドウ挙動がリスク | UI内に構成しやすい |
| ポータブル配布 | .NETとネイティブDLL一式の同梱を検証 | .NET、LibVLC、プラグイン一式の同梱を検証 | Windows App SDK自己完結配布を検証 |
| 将来の他OS | バックエンドは有利、WPFはWindows限定 | バックエンドは有利、WPFはWindows限定 | Windows限定 |
| 実装・保守コスト | 中～高 | 中 | 初期は低、拡張時は高くなる懸念 |

## 6. 最初のスパイク

### 6.1 Spike 0: バックエンド・ゲート

UIを作り込む前に、Bの最小ホストで次を同じテスト動画に対して確認する。AはLGPL専用Windows
ビルドと対応ソースを再現可能に固定できた場合にだけ比較へ戻す。

1. H.264/AACのmp4、HEVC/AACのmp4、WMV9またはVC-1/WMAのwmvを映像・音声付きで開く。
2. 再生、一時停止、先頭・中間・末尾へのシークを行う。
3. 0.25、0.5、1.0、1.5、2.0倍速を切り替え、音切れ、ピッチ、A/V同期を記録する。
4. 100、200、300、500%相当の増幅とピーク抑制を行い、クリッピング、CPU負荷、切替応答を測る。
5. 再生中とは独立して任意位置のフレームを取得し、連続要求のキャンセルと優先順位付けを試す。
6. 非対応、破損、欠損、アクセス拒否を可能な範囲で区別できるか確認する。

CはWindows標準の基準値として、最初のリリース範囲と速度変更までを同じ動画で測定する。
500%増幅またはフレーム取得を標準APIだけで満たせないことが確認できた時点で、追加バックエンドを
必要とする構成として評価を下げる。

### 6.2 Spike 1: UI・ウィンドウ・差し替え

バックエンド・ゲートを通過した上位2構成で、以下だけを持つホストを作る。

- メインウィンドウ、動画領域、動画上の再生コントロール、シークバー
- 再生バックエンドのインターフェースとモック実装
- 別ウィンドウ、ホバーポップアップ、フルスクリーン
- DPIの異なる2画面での移動、フォーカス、前面化

### 6.3 Spike 2: OS連携・配布・性能

- Named Pipe等による単一起動、既存プロセスへの1ファイル引き渡し、前面化
- 日本語・空白を含む起動引数
- `data` 配下JSONの原子的更新、書き込み不可、破損復旧
- 自己完結フォルダー配布と単一exe相当の発行
- コールドスタート、オープンから最初のフレーム、シーク応答、メモリ、配布サイズ

単一起動・IPCとポータブルJSONは2026-08-03に合格した。Named Mutexと同一利用者限定
Named Pipeで、単一プロセス、0/1/複数引数、日本語・空白パス、既存ウィンドウの実画面前面化を
確認した。JSONは`data`配下の同一ディレクトリ一時ファイルをflush後に置換し、書込不可時の
既存値維持と、破損ファイル退避後の既定値復旧を確認した。詳細は
`docs/phase2/single-instance-data-spike.md`を参照する。

## 7. テスト動画セット

2026-07-28に、次の軸の全組み合わせをMP4とWMVで生成した。合計16本、約123 MBである。

- 解像度: 1920x1080、3840x2160
- 長さ: 5秒、60秒
- フレームレート: 30 fps、60 fps
- MP4: H.264/AVC 8-bit + AAC-LC stereo 48 kHz
- WMV/ASF: VC-1 + Windows Media Audio 9 Professional stereo 48 kHz

映像はグリッド、固定色、移動図形、条件表示、経過時間、フレーム番号を含む。音声は左440 Hz、
右660 Hzを基準信号とし、左右で半秒ずらした同期パルスを含む。デコード後ピークはMP4で
-0.4 dBFS、WMVで-0.3 dBFSとなり、高音量・ピーク抑制試験にも使用できる。

`scripts/verify-phase2-test-videos.ps1` による全件検査で、コーデック、解像度、長さ、
フレーム数（5秒は150/300、60秒は1800/3600）、48 kHzステレオ、ピーク、SHA-256が
すべて合格した。ASFの平均フレームレート表示は信頼せず、映像パケット数と時間で検証する。
結果は `test-assets/generated/verification.json` を正とする。

生成物は大きいためGit管理対象外とし、次の再生成可能なスクリプトと検証結果だけを管理する。

- `scripts/generate-phase2-test-videos.ps1`
- `scripts/transcode-phase2-test-videos-to-wmv.ps1`
- `scripts/verify-phase2-test-videos.ps1`
- `test-assets/README.md`

検証結果から確定した初回保証範囲と、利用者向けの保証外の説明は
`docs/supported-media-formats.md`にまとめる。

生成専用FFmpegはアプリやリポジトリへ同梱しない。取得物はBtbNのLGPL shared build
`N-125781-gacf6b520c1-20260727`、アーカイブSHA-256
`E21E3CC4B4BEDE7CB341956EE0702AEDF2F61942D94093AE81B41A2C0FA8C51D`である。
実構成で `--enable-gpl`、`--enable-nonfree`、libx264、libx265が無効であることを確認し、
MP4のH.264はWindows Media Foundationエンコーダー `h264_mf` を使用した。WMVは外部
エンコーダーを追加せず、Windows 11標準の `Windows.Media.Transcoding` APIで作成した。

次の互換性・エラー素材は別途追加する。

| ID | コンテナ | 映像 | 音声 | 用途 |
| --- | --- | --- | --- | --- |
| V17 | mp4 | HEVC 8-bit 1080p30 | AAC-LC stereo 48 kHz | Windows環境差・HW decode |
| V18 | mp4 | H.264可変フレームレート | AAC-LC | 時刻・シーク境界 |
| E01 | mp4 | 意図的な非対応コーデック | 任意 | 非対応判定 |
| E02 | mp4 | 途中で切断した破損ファイル | 任意 | 破損時の状態維持 |

### 7.1 LibVLCバックエンド検証

2026-07-29に、上記16本を `LibVLCSharp` 3.10.0 + LibVLC 3.0.23でソフトウェアデコードし、
全件で映像・音声デコード、一時停止・再開、中間・末尾シークに成功した。機能面ではH.264/AAC
MP4とVC-1/WMA 9 Professional WMVを次のWPF実出力ゲートへ進められる。

性能はMP4とWMVで大きく異なる。4K長尺WMVの5回測定では、末尾シーク中央値が30fpsで
2728 ms、60fpsで4972 msだった。これはソフトウェアデコード・コールバック出力条件であり、
製品目標値ではないが、4K WMVを1080p WMVと同じ保証範囲に含める前にWPF/GPU経路の再測定が
必要である。詳細は `docs/phase2/libvlc-playback-validation.md` を参照する。

2026-08-03の速度切替再調査では、バックエンド値の反映が100 ms以内でも、PCM供給は
1.0↔2.0倍で約100～250 ms、低速側で最大約1,000 ms開いた。100 ms先読み、ピッチ維持
フィルターの低遅延化・無効化では改善せず、VideoLANの未解決issue #25056とも整合する。
定常再生の主観合格と、シームレスな切替保証は分けて評価する。詳細は
`docs/phase2/rate-transition-investigation.md`を参照する。

## 8. 測定項目

基準PCのCPU、GPU、メモリ、ストレージ、Windowsビルド、電源モード、ディスプレイ構成を記録する。
各時間はコールド条件を明記して5回測定し、中央値と最大値を残す。

- プロセス開始から操作可能なメインウィンドウ表示まで
- オープン要求から最初の映像フレーム表示・音声開始まで
- シーク入力からシーク先フレーム表示まで
- 速度・音量変更の反映まで
- サムネイル単発取得、および100件バックグラウンド生成中のUI応答
- アイドル時・1080p再生時・4K再生時のCPU、GPU、Working Set
- 自己完結配布物の圧縮前後サイズ

目標値は候補技術の同一条件での実測後に提案し、根拠なく先に固定しない。

2026-08-01にWPF実出力・D3D11VA・WASAPI条件で、MP4 1080p60、MP4 4K60、WMV 1080p60の
60秒素材を各5回測定した。15/15回合格し、操作可能までの最大は1196 ms、映像準備の最大は
719 ms、50% / 90%シークの最大は1114 msだった。プロセスコールド（OSファイルキャッシュは
消去しない）での結果である。詳細な基準PC、代理測定の定義、CPU・メモリ値、生データは
`docs/phase2/wpf-performance-validation.md`と
`docs/phase2/results/wpf-performance-measurement.json`を参照する。

同条件のPhase 2目標として、操作可能1500 ms以内、映像・音声準備1000 ms以内、シーク1500 ms以内、
速度・音量のバックエンド反映100 ms以内を、2026-08-01に開発者承認により確定した。

## 9. ライセンス確認ゲート

- WPF/.NETのライセンスと再配布条件
- LibVLCSharp、LibVLC本体、同梱プラグインのライセンスとnotice
- mpvをLGPL構成で用意できるか、使用するWindowsビルドの再現性、FFmpeg等の構成オプション
- FFmpegは基本LGPL 2.1+だが、GPL部品を有効にしたビルドではGPLが適用される点
- コーデックの特許・配布条件はオープンソースライセンスと別問題である点

法的判断そのものではなく、採用する正確なバイナリとビルド設定を固定し、配布物に必要な
ライセンス本文、copyright notice、ソース入手方法等を列挙することを完了条件とする。

## 10. 統合評価

Phase 2の全ゲートを通した結果、最終採用候補をC# / .NET 10 LTS、WPF、
`LibVLCSharp` / `LibVLCSharp.WPF` 3.10.0、サニタイズした
`VideoLAN.LibVLC.Windows` 3.0.23.1に確定する。100%超の音声経路では
`NAudio.Core` / `NAudio.Wasapi` 2.3.0を使用し、永続化は`System.Text.Json`、
単一起動はNamed Mutex + current-user-only Named Pipeとする。

初回配布は`win-x64` framework-dependent folderのZIPである。配布可否は、GPL-onlyプラグイン
4件の除外、319プラグイン監査、525件のx64資産照合、対応ソースZIPの再ハッシュ、notice同梱を
自動ゲートとして判定する。各構成の正確な採用条件、不採用理由、残存リスクは
`docs/decisions/0001-technology-stack.md`を正とする。

WPF + libmpvは一般的なGPL WindowsビルドとLGPL専用ビルドの継続監査負担、WinUI 3 +
Media Foundationはコーデック環境依存と高度な音声・サムネイル機能への追加経路が主な不採用理由で
ある。採用候補が必須機能を実証したため、これらの比較実装は追加しない。

## 11. 公式資料

- .NET support policy: https://dotnet.microsoft.com/en-us/platform/support/policy
- .NET single-file deployment: https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview
- WPF desktop guide: https://learn.microsoft.com/en-us/dotnet/desktop/
- Windows App SDK deployment: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/
- LibVLCSharp documentation: https://docs.videolan.me/libvlcsharp/
- LibVLCSharp WPF caveats: https://docs.videolan.me/libvlcsharp/docs/getting_started.html
- mpv manual / libmpv: https://mpv.io/manual/stable/
- mpv source and license: https://github.com/mpv-player/mpv
- FFmpeg legal information: https://ffmpeg.org/legal.html

## 12. 採用後の引き継ぎ

主要な再生・性能・音量・サムネイル・エッジケース・発行物スパイク、技術評価・ADRへの統合、
コードレビュースキルの試行を完了した。2026-08-05に開発者が構成と残存リスクを承認し、
WPF + LibVLCSharpを最終採用した。次はPhase 3のUI設計へ進む。

速度切替直後の音飛びは、2026-08-03に再生速度の重要度を下げて既知制約として許容した。
シームレス切替を保証せず、本件を理由とする追加調査、LibVLC独自修正、代替バックエンド比較は行わない。

UI・ウィンドウスパイクは2026-08-03に合格した。`IPlaybackBackend`へモックを注入し、別HWND、
シークPopup、フルスクリーン、foreground、96/144 DPIの3画面移動を実証した。WPFでは
`PerMonitorV2, PerMonitor`のマニフェスト宣言を必須とする。Popupの細かな間隔とマウス透過は
Phase 3/4で調整する。詳細は`docs/phase2/wpf-windowing-spike.md`を参照する。

`VideoLAN.LibVLC.Windows 3.0.23.1`のx64対応ソースは2026-08-02に確定し、公開バイナリ用ゲートを
合格した。詳細は`docs/phase2/libvlc-source-provenance.md`を参照する。
初回配布形式は同日、`win-x64`のframework-dependent folderに確定した。.NET 10 Desktop Runtime
x64の事前導入とZIP展開後の起動方法は、リリース時のREADMEにHow To Useとして記載する。

単一起動・IPCとポータブルJSONは2026-08-03に合格した。Named Mutex + current-user-only
Named Pipeと、同一ディレクトリ一時ファイルを用いたJSON置換方式を製品実装へ進められる。

最低ハードウェアは2026-08-05に、初回リリースでは定義しない方針とした。現在の基準PCは
性能目標を検証した環境として公開するが、最低要件とは表現しない。Windows 11 x64、.NET 10
Desktop Runtime x64等の必須ソフトウェア条件は別に明示し、未検証のハードウェアは
ベストエフォートとする。Phase 4のクリーン環境確認は導入・起動・必須シナリオの確認であり、
低性能環境の保証試験を必須としない。利用可能な検証環境が増えた場合にだけ下限の定義を再評価する。

プロジェクト専用コードレビュースキルは2026-08-05に作成・試行した。単一起動スパイクへの適用で、
不正IPCが待受全体を停止する問題と、初回状態・二次要求の順序競合を検出して修正した。詳細は
`docs/phase2/code-review-skill-trial.md`を参照する。

詳細な順序と完了条件は`docs/project-plan.md`の`P2-REM-01`～`P2-REM-09`を正とする。
