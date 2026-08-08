# Phase 2 ライセンス事前監査

最終更新日: 2026-08-02  
状態: 候補選別完了・最終配布物監査は継続  
対象: GitHubでMIT公開し、無償でWindows向けバイナリを配布する場合

## 1. 結論

次の構成は、下記の遵守事項を実施すれば、プロジェクト自身をMIT LicenseのままGitHubで
公開・無償配布できる候補と判断する。

- C# / .NET 10
- WPF
- `System.Text.Json`
- `LibVLCSharp.WPF` 3.10.0
- `LibVLCSharp` 3.10.0
- `VideoLAN.LibVLC.Windows` 3.0.23.1から、実ファイル監査で判明したGPL-onlyプラグインを除外した発行物

`VideoLAN.LibVLC.Windows` 3.0.23.1はパッケージ全体をLGPLと宣言しているが、そのままの
発行物にはGPL-onlyプラグインが4件混入することをスパイクで確認した。この4件を明示除外し、
全プラグインのライセンス関数を自動検査することを採用条件とする。

また `VideoLAN.LibVLC.Windows.GPL` は使用しない。mpv/libmpvの通常ビルドもGPLv2-or-laterが
既定であるため、MITのまま配布する今回の第一候補から外す。mpvを再検討する場合は、
`-Dgpl=false` で作成されたLGPLビルドと、その全依存ライブラリの構成・ソースを再現できることを
採用条件とする。

これは公開情報と取得した公式パッケージに基づく技術的なライセンス監査であり、法的助言ではない。
コーデック特許はオープンソースライセンスとは別の論点であり、本監査の「問題なし」は著作権
ライセンス上の配布条件を満たせるという意味に限定する。

## 2. 判定一覧

| コンポーネント | ライセンス | MITプロジェクトでの利用 | 判定・条件 |
| --- | --- | --- | --- |
| プロジェクト本体 | MIT | 可 | 現状維持 |
| .NET / WPF | MIT | 可 | MicrosoftのcopyrightとMIT本文を第三者表記へ含める |
| `System.Text.Json` | MIT | 可 | .NETランタイム同梱物のthird-party noticesも発行物から収集する |
| Windows App SDK / WinUI 3 | MIT | 可 | 比較候補として問題なし。実際の発行物に含まれるnoticeは別途収集する |
| `LibVLCSharp` 3.10.0 | LGPL-2.1系 | 条件付きで可 | 動的リンクを維持し、利用表示、LGPL本文、対応ソース入手手段を提供する |
| `LibVLCSharp.WPF` 3.10.0 | LGPL-2.1系 | 条件付きで可 | 同上 |
| `VideoLAN.LibVLC.Windows` 3.0.23.1 | パッケージ宣言はLGPL-2.1-or-laterだがGPL-onlyプラグイン混入あり | 条件付きで可 | GPL4件を除外し、全プラグインを自動監査する。DLL・pluginsを交換可能な別ファイルとして配布し、対応ソース入手手段を提供する |
| `VideoLAN.LibVLC.Windows.GPL` | GPL-2.0-or-later | MITのままでは採用しない | GPLプラグインを含むと公式に明記されているため禁止パッケージとする |
| mpv通常ビルド | GPL-2.0-or-later | MITのままでは採用しない | 動的リンクでも結合物全体へGPL条件が及ぶというFSF解釈がある |
| mpv `-Dgpl=false` ビルド | LGPL-2.1-or-later | 理論上は条件付きで可 | 公式Windowsバイナリの固定、依存FFmpeg等のビルド構成、対応ソースの再現が複雑 |
| FFmpeg | LGPL-2.1-or-laterが基本 | ビルド構成次第 | `--enable-gpl` やGPLライブラリを有効にしたビルドはGPLになる。直接採用時は個別監査必須 |
| `NAudio.Core` 2.3.0 | MIT | 可 | 500%音量のPCM処理・バッファ候補。NuGet上の外部依存なし。採用時はMIT本文と著作権表示を同梱する |
| `NAudio.Wasapi` 2.3.0 | MIT | 可 | Windows音声出力候補。同版の`NAudio.Core`だけに依存する。実時間音声スパイクへ固定追加済み |

## 3. VideoLAN公式パッケージの実物確認

2026-07-27にNuGet公式配布物を取得し、nuspec、内容物、SHA-256を確認した。

| パッケージ | SHA-256 | nuspecのライセンス | 備考 |
| --- | --- | --- | --- |
| `LibVLCSharp` 3.10.0 | `B1ED0CDFFAC10C1999A2CF95AF44324341AFC2DA635DDDB50397B9F4D0E95065` | `LGPL-2.1-or-later` | .NET 10向けアセンブリを含む |
| `LibVLCSharp.WPF` 3.10.0 | `84E597A8291FFBBC8EEBBFDB3B945D637355D6956257BC887B1BABB7F4A562D6` | `LGPL-2.1-or-later` | `LibVLCSharp` 3.10.0へ依存 |
| `VideoLAN.LibVLC.Windows` 3.0.23.1 | `70927AFA9AD34B77E7D9A5E6D02CAE099771F6EB3114DA18111A4B76F65B836F` | `LGPL-2.1-or-later` | x86/x64/ARM64のDLLとpluginsを含む |
| `NAudio.Core` 2.3.0 | `2940C2DBEABE7736424549227DE3D27F10100858BE56920AB120E4621B7F320C` | `MIT` | 外部NuGet依存なし。nuspec commitは`c89fee940ee6f8d7374d18714a6b85d8b7a18ab0` |
| `NAudio.Wasapi` 2.3.0 | `B80830DEB834F01838D87EF6DC3D177DBA5CDE658259BB9820BD60194EE595F1` | `MIT` | `NAudio.Core` 2.3.0だけに依存。同じcommit |

Windowsパッケージ内には `libvlc.dll`、`libvlccore.dll`、`libavcodec_plugin.dll` などが含まれる。
一方、取得した3パッケージにはLGPL本文そのものは格納されておらず、nuspecのSPDX表記だけだった。
したがって、アプリの配布処理側でライセンス本文を必ず追加する。

2026-08-01、500%音量の実時間出力候補として、正式版`NAudio.Core` 2.3.0と
`NAudio.Wasapi` 2.3.0をNuGet公式情報およびNAudio公式リポジトリで事前確認した。
両パッケージはMITで、`NAudio.Core`は外部依存なし、`NAudio.Wasapi`は同版の
`NAudio.Core`だけに依存する。プレビュー版3.xは採用候補に含めない。

実際のRelease publishを全件監査した結果、非GPL版パッケージにも次のGPL v2 or later
プラグインが含まれていた。

- `plugins/audio_filter/libdolby_surround_decoder_plugin.dll`
- `plugins/audio_filter/libheadphone_channel_mixer_plugin.dll`
- `plugins/codec/libx26410b_plugin.dll`
- `plugins/lua/liblua_plugin.dll`

各DLLが公開する `vlc_entry_license__3_0_0f` の戻り値でGPLを確認した。スパイクでは4件を
MSBuildで除外し、残り319件を `scripts/verify-libvlc-plugin-licenses.ps1` で検査した結果、
読取不能0件、GPL-only 0件となった。パッケージのSPDX表記だけでは不十分であり、この検査を
継続する。

LibVLCSharpリポジトリの3.xブランチは `LGPL-2.1 only` と表示する一方、NuGet 3.10.0は
`LGPL-2.1-or-later` と宣言している。差異があるため、遵守時はより限定的な
LGPL-2.1として扱い、LGPL-2.1本文を同梱する。

## 4. LGPL構成で必ず行うこと

1. アプリ本体とLGPL DLLを別ファイルのまま配布し、利用者が同互換ライブラリへ交換できる構成にする。
2. 単一exeへLGPL DLLを不可逆に静的結合しない。単一exeが起動時にDLLを展開する方式も、
   交換可能性と告知が明確になるまでは採用しない。
3. 配布物に `THIRD-PARTY-NOTICES.md` と `licenses/LGPL-2.1.txt` を含める。
4. アプリがLibVLC/LibVLCSharpを利用し、それらがLGPLの対象であることをREADMEとnoticeへ明記する。
5. 使用した正確なバージョン、NuGet URL、SHA-256、改変の有無を記録する。
6. 対応する完全なソースを利用者が取得できる状態にする。リンク切れや版ずれを避けるため、
   GitHub Releaseごとに次を同じリリースから案内する。
   - LibVLCSharp 3.10.0の対応ソース
   - VLC 3.0.23およびWindows NuGetビルドに対応するcontribソース
   - 改変した場合はパッチを含む完全な対応ソース
7. LGPLライブラリの置換・デバッグを禁止する利用規約を追加しない。
8. 発行後の実ファイル一覧をSBOMまたはライセンス台帳へ出力し、予定外のGPL/AGPL依存が
   混入していないことをCIで検査する。
9. 上記4件を発行物から除外し、`scripts/verify-libvlc-plugin-licenses.ps1` を成功させる。

ソース提供方法は、最終配布前に実際のVideoLAN NuGetビルドと対応するソース一式を確定する。
単に「最新版の上流リポジトリ」へリンクするだけでは、配布したバイナリと版が一致しなくなるため
不十分と扱う。

## 5. 禁止・注意ルール

- `VideoLAN.LibVLC.Windows.GPL` をPackageReferenceへ追加しない。
- `VideoLAN.LibVLC.Windows`を未加工のまま発行しない。
- 出所やビルドオプションが不明なVLC、mpv、FFmpegバイナリを同梱しない。
- GPL版mpvをlibmpvとして直接リンクし、プロジェクト本体をMITだけで配布しない。
- FFmpegを直接導入する場合、パッケージ名のライセンス表示だけで判断せず、
  `configure` オプションと全外部ライブラリを監査する。
- 「無償」「GitHubでソース公開」は、GPL/LGPLの義務を免除しない。
- H.264、HEVC等のコーデック特許・ロイヤルティは、MIT/LGPL/GPLとは別途扱う。

### 5.1 テスト動画生成ツール

Phase 2のテスト動画生成には、BtbN `FFmpeg-Builds` のLGPL shared build
`N-125781-gacf6b520c1-20260727`を一時ツールとして使用した。FFmpeg公式ダウンロードページが
Windowsビルド提供元として案内するプロジェクトである。取得アーカイブのSHA-256は
`E21E3CC4B4BEDE7CB341956EE0702AEDF2F61942D94093AE81B41A2C0FA8C51D`。

実バイナリのconfigure表示を確認し、`--enable-gpl`、`--enable-nonfree`、libx264、libx265が
有効でないことを確認した。生成スクリプトもこれらの文字列を検出した場合は停止する。
H.264エンコードにはWindows Media Foundation経由の `h264_mf`、音声にはFFmpegのAAC
エンコーダーを使用する。FFmpegバイナリとDLLは `.tmp` 配下だけに置き、リポジトリ、
アプリ配布物、GitHub Releaseへ含めない。

WMVはWindows 11標準の `Windows.Media.Transcoding` APIを使用し、VC-1 +
Windows Media Audio 9 Professionalとして生成した。プロジェクトへWMVエンコーダーの
ライブラリやNuGetパッケージは追加していない。

生成されたメディアファイルはエンコーダーをアプリへリンク・同梱するものではない。一方、
H.264、AAC、VC-1、WMAのコーデック特許は著作権ライセンスとは別の論点である。テスト素材は
技術検証用とし、無条件にGit管理・再配布せず、公開範囲をリリース前に再確認する。

## 6. リリース前ライセンスゲート

2026-08-02に`publish-phase2-release.ps1`で、folder発行、LGPL/.NET通知、全ファイルSHA-256台帳、
GPL-only除外、319プラグイン監査、対応ソースarchive必須化までを自動化した。validation-onlyの
framework-dependent/self-contained両方で成功した。ただし以下は実際の公開Releaseごとに
再実行するチェックリストであり、Phase 2の模擬発行だけでは完了扱いにしない。

- [ ] `dotnet list package --include-transitive` の固定結果を保存する。
- [ ] 発行ディレクトリの全DLL・EXEを台帳化する。
- [ ] 各ファイルの出所、バージョン、ライセンス、ソースURL、SHA-256を記録する。
- [ ] `VideoLAN.LibVLC.Windows.GPL`、GPL、AGPL、ライセンス不明の依存がない。
- [ ] 全LibVLCプラグインの自己申告ライセンス検査が、読取不能0件、GPL-only 0件で完了する。
- [ ] LGPL DLLがアプリ本体と分離され、利用者が交換できる。
- [ ] `THIRD-PARTY-NOTICES.md` と全ライセンス本文が配布物に含まれる。
- [ ] 対応ソースまたは有効な提供方法を、バイナリと同じGitHub Releaseから辿れる。
- [ ] クリーン環境でnoticeとライセンスを閲覧できる。
- [x] コーデック特許を本プロジェクトの保証対象に含めない旨を利用文書へ明記する。`docs/supported-media-formats.md`、2026-08-01。

2026-08-02にx64資産525/525件がVideoLAN公式VLC 3.0.23 win64配布物とSHA-256一致することを確認し、
VLC本体、SHA-512固定のcontrib 126件、LibVLCSharp、監査対象パッケージングソースを含む対応ソースZIPを
生成した。公開用スクリプトは同ZIPのマニフェストと131エントリを再ハッシュし、未指定・形式不正・
内容不一致なら発行開始前に失敗する。ZIPをバイナリと同じGitHub Releaseへ別アセットとして掲載する
条件で、x64公開バイナリの対応ソース阻害事項は解消した。詳細は
`docs/phase2/libvlc-source-provenance.md`を参照する。

## 7. 根拠資料

- WPF repository / MIT: https://github.com/dotnet/wpf
- Windows App SDK / MIT: https://github.com/microsoft/WindowsAppSDK
- System.Text.Json NuGet / MIT: https://www.nuget.org/packages/System.Text.Json
- LibVLCSharp repository: https://github.com/videolan/libvlcsharp
- LibVLCSharp.WPF 3.10.0: https://www.nuget.org/packages/LibVLCSharp.WPF/3.10.0
- VideoLAN.LibVLC.Windows 3.0.23.1 / LGPL: https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.23.1
- VideoLAN.LibVLC.Windows.GPL 3.0.23.1 / GPL: https://www.nuget.org/packages/VideoLAN.LibVLC.Windows.GPL/3.0.23.1
- VLC/libVLCのライセンス説明: https://github.com/videolan/vlc
- VLC 3.0.23 source: https://www.videolan.org/vlc/download-sources.html
- mpvのライセンスとビルド切替: https://github.com/mpv-player/mpv
- FFmpeg legal information: https://ffmpeg.org/legal.html
- NAudio repository / MIT: https://github.com/naudio/NAudio
- NAudio.Core 2.3.0 / MIT: https://www.nuget.org/packages/NAudio.Core/2.3.0
- NAudio.Wasapi 2.3.0 / MIT: https://www.nuget.org/packages/NAudio.Wasapi/2.3.0
- GNU LGPL 2.1本文: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
- GNU GPL/LGPL FAQ: https://www.gnu.org/licenses/gpl-faq.html
