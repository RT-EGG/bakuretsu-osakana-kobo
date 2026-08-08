# WPF + LibVLCSharp バックエンド・ゲート結果

最終更新日: 2026-08-02  
状態: WPF実描画・通常音声・速度別主観品質の確認完了  
実装: `spikes/LibVlcWpfSpike`

## 1. 目的

暫定第一候補であるWPF + LibVLCSharp + `VideoLAN.LibVLC.Windows`について、.NET 10との
API互換性、ネイティブ初期化、LGPL DLL分離、GPLプラグイン混入、最小操作の実装可能性を確認する。
このスパイクのUIはPhase 3のデザイン案ではない。

## 2. 固定構成

| 要素 | バージョン・条件 |
| --- | --- |
| SDK | .NET SDK 10.0.301 |
| Target Framework | `net10.0-windows` |
| Architecture | Windows x64 |
| LibVLCSharp | 3.10.0 |
| LibVLCSharp.WPF | 3.10.0 |
| VideoLAN.LibVLC.Windows | 3.0.23.1、GPL版ではないパッケージ |
| 発行 | framework-dependent、非single-file |

`packages.lock.json` を生成し、上記3パッケージ以外のNuGet依存がないことを確認した。

## 3. 実装した検証導線

- mp4/wmvのファイル選択と自動再生
- 再生・一時停止
- 正規化位置によるシーク
- `hh:mm:ss / hh:mm:ss` 時刻表示
- 0.25、0.5、1.0、1.5、2.0倍速の変更要求
- 0～500%の音量要求値と、LibVLCが返す実値の比較
- WPF VideoView上のオーバーレイ
- LibVLC非同期エラーの表示
- `IPlaybackBackend`による操作境界

500%音量の実効増幅・ピーク抑制、任意位置フレーム取得、エラー分類、実動画性能は未実装または
未検証である。

## 4. ビルド・起動結果

- `dotnet restore --locked-mode`: 成功
- Debug build: 成功、警告0、エラー0
- framework-dependent Release publish: 成功
- 非表示で5秒起動: 正常継続、応答あり
- GPLプラグイン除外後の発行物: 正常起動、応答あり

API確認では、LibVLCSharp 3.10.0の `MediaPlayer.SetRate` はboolではなく、0成功の整数コードを
返すことを確認し、アダプター側でboolへ変換した。

## 5. ライセンス上の重要な発見

`VideoLAN.LibVLC.Windows` 3.0.23.1はNuGet上でLGPL-2.1-or-laterと宣言されているが、
そのまま発行するとGPLv2-or-laterを自己申告するプラグインが4件含まれた。

| 発行物内パス | スパイクでの扱い |
| --- | --- |
| `plugins/audio_filter/libdolby_surround_decoder_plugin.dll` | 除外 |
| `plugins/audio_filter/libheadphone_channel_mixer_plugin.dll` | 除外 |
| `plugins/codec/libx26410b_plugin.dll` | 除外 |
| `plugins/lua/liblua_plugin.dll` | 除外 |

`libx26410b_plugin.dll` はx264コードを静的に含む構成で、DLLが公開する
`vlc_entry_license__3_0_0f` はGPL v2 or laterと返した。したがってファイル名だけの推測ではなく、
配布物自体からGPLであることを確認した。

スパイクのcsprojで4件を `VlcWindowsX64ExcludeFiles` に指定した。さらに
`scripts/verify-libvlc-plugin-licenses.ps1` が、発行された全プラグインの
`vlc_entry_license__3_0_0f` を呼び出し、GPL-onlyがあれば失敗する。

## 6. サニタイズ後の発行物

| 項目 | 結果 |
| --- | --- |
| ファイル数 | 429 |
| 合計サイズ | 99.0 MB |
| ライセンスを読めたLibVLCプラグイン | 319 |
| ライセンス読取不能 | 0 |
| GPL-only | 0 |
| x86/ARM64ファイル | なし |
| `THIRD-PARTY-NOTICES.md` | あり |
| LibVLC DLLとアプリ本体 | 別ファイル |

除外前は3アーキテクチャがコピーされ、1270ファイル、279.7 MBだった。明示的にx64だけを有効にし、
ライセンス除外と配布サイズの両方を改善した。

## 7. 判定

WPF + LibVLCSharpは、.NET 10でビルド・起動でき、基本的な再生操作APIとWPFオーバーレイを
構成できるため、代表動画を使う次のゲートへ進める。

ただし `VideoLAN.LibVLC.Windows` 3.0.23.1をそのまま配布してはならない。4件の明示除外と
全プラグインの実行時ライセンス監査をビルド・リリースの必須ゲートにする。この除外を維持できない
将来バージョンへ更新する場合は、更新前に採用判断をやり直す。

## 8. 代表動画の自動バックエンド検証

`spikes/LibVlcPlaybackProbe`を追加し、H.264/AAC MP4 8本とVC-1/WMA 9 Professional WMV
8本をソフトウェアデコードした。全16本でメディア解析、映像・音声コールバック、一時停止・再開、
中間・末尾シークが成功した。60秒素材では0.25～2.0倍速要求もすべて受理された。

4K長尺WMVの5回測定では、末尾シーク中央値が30fpsで2728 ms、60fpsで4972 msとなり、
MP4より明確に遅い。詳細、測定限界、診断ログは
`docs/phase2/libvlc-playback-validation.md`を参照する。

自動検証はWPF画面、GPUデコード、通常音声出力を使わないため、`OPEN-008`と`OPEN-009`の
手動受け入れ完了とはしない。

## 9. 次の検証

2026-07-29にWPF実描画をMP4、WMV、1080p、4Kの代表条件で確認した。4K/60fps H.264と
1080p/60fps VC-1はRTX 3070 TiのD3D11VAを使用したが、4K/30fps VC-1はGPUが構成を
受理せずソフトウェアデコードへフォールバックした。通常音声出力は`mmdevice` / WASAPIまで
開始した。詳細は`docs/phase2/wpf-real-output-validation.md`を参照する。

左右音、同期、速度別音質、シーク、500%音量、サムネイル、配布形式、対応ソースの各検証は
別記録を含めて完了した。速度別品質は2026-08-02に同期専用MP4/WMVの0.25、0.5、2.0倍で
開発者が定常品質を主観合格とした。速度切替直後の短い音飛びは2026-08-03に再調査し、
LibVLCの速度変更・音声同期層に及ぶ可能性が高いと判明した。同日の開発者判断により、
再生速度の重要度を下げて既知制約として許容し、シームレス切替を保証せず、追加調査・
代替バックエンド比較を行わない。詳細は`docs/phase2/rate-transition-investigation.md`を参照する。
