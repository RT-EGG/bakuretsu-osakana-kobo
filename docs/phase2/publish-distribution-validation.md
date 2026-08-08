# Phase 2 発行・配布構成検証

実施日: 2026-08-02  
対象: WPF / .NET 10 / LibVLCSharp 3.10.0 / VideoLAN.LibVLC.Windows 3.0.23.1

## 1. 結論

初回配布には`framework-dependent`のfolder発行を採用する。`self-contained`も動作するが、
サイズ、初回起動、同梱ライセンス監査範囲で不利である。どちらも単一exe化せず、LibVLCと
LibVLCSharpのDLLを交換可能な別ファイルとして維持する。
framework-dependent版は利用PCへ.NET 10 Desktop Runtime x64の事前インストールが必要になる。
この前提を含めて2026-08-02に開発者承認済みであり、必要なランタイム、ZIPの展開、起動方法を
リリース時のREADMEにHow To Useとして記載する。ランタイム導入が実利用上の大きな障害になる
場合は、動作確認済みのself-containedを再評価する。

`VideoLAN.LibVLC.Windows 3.0.23.1`のnuspecだけではbuild provenanceが不足していたが、
NuGet x64資産525/525件がVideoLAN公式VLC 3.0.23 win64配布物とSHA-256一致することを確認した。
さらにVLC本体、SHA-512固定されたcontrib 126件、LibVLCSharp、監査対象パッケージングソースを
1つの対応ソースZIPへ収録し、リリーススクリプトで全エントリを再検証できるようにした。
このZIPをバイナリと同じGitHub Releaseへ別アセットとして掲載する条件で、x64公開バイナリの
LGPL技術ゲートを合格とする。詳細は`docs/phase2/libvlc-source-provenance.md`を参照する。

## 2. 発行比較

`PublishSingleFile=false`、`win-x64`、Releaseで発行した。

| 項目 | framework-dependent | self-contained |
| --- | ---: | ---: |
| ファイル数（ライセンス資産追加前） | 429 | 824 |
| サイズ（同上） | 99.03 MiB | 238.11 MiB |
| LibVLCプラグイン | 319 | 319 |
| GPL-only一致 | 0 | 0 |
| .NETランタイム同梱 | なし | 10.0.9を同梱 |
| 利用PCの前提 | .NET 10 Desktop Runtime x64が必要 | ランタイム事前導入不要 |
| 起動・実画面検証 | 5/5成功 | 5/5成功 |
| Dispatcher準備中央値 | 971 ms | 973 ms |
| Dispatcher準備最大 | 1223 ms | 2761 ms |
| 1500 ms目標 | 5/5 | 4/5 |
| 最大映像準備 | 210 ms | 222 ms |
| 最大ピークWorking Set | 342 MiB | 344 MiB |

self-containedの初回だけ2.76秒となり、以後はframework-dependentと同程度だった。
ファイル展開直後の多数のランタイムファイルに対する初回I/O・セキュリティ走査の影響が推測される。
またself-containedでは.NET 10.0.9の約395ファイルも配布物台帳・第三者通知の対象になる。

## 3. 依存とプラグイン監査

直接・推移NuGet依存は次の3件だけである。

- `LibVLCSharp 3.10.0`
- `LibVLCSharp.WPF 3.10.0`
- `VideoLAN.LibVLC.Windows 3.0.23.1`

両発行形式で`verify-libvlc-plugin-licenses.ps1`を実行し、次を確認した。

- 自己申告ライセンスを読めたプラグイン: 319
- 読取不能: 0
- GPL/AGPL: 0
- 既知のGPL-only 4ファイル: 0

## 4. リリース工程

`scripts/publish-phase2-release.ps1`を追加し、次を自動化した。

1. single-fileを無効にしたfolder publish
2. `VideoLAN.LibVLC.Windows.GPL`のlock file検出
3. 既知GPL-only 4プラグインの不在確認
4. 全LibVLCプラグインの自己申告ライセンス検査
5. GNU公式LGPL 2.1本文の固定ハッシュ検査と同梱
6. .NETライセンス・第三者通知とVideoLAN noticeの同梱
7. 全ファイルの相対パス、サイズ、SHA-256を`release-manifest.json`へ記録
8. 公開用実行では検証済み対応ソースZIPを必須化し、未指定・形式不正なら発行開始前に停止
9. ZIP内の監査マニフェスト、VLC本体、LibVLCSharp、パッケージングソース、contrib 126件を再ハッシュ

`-ValidationOnly`で両形式を実行し、ライセンス資産追加後はframework-dependentが433ファイル・
99.14 MiB、self-containedが828ファイル・238.22 MiBとなった。両方の自動ゲートは成功した。
公開モードで対応ソースを省略した試験は、出力ディレクトリを作る前に意図どおり停止した。

## 5. 対応ソースの確定

`scripts/collect-libvlc-corresponding-source.ps1`で、VLC 3.0.23公式ソース、contrib 126件、
LibVLCSharp commit `59d70e...`、libvlc-nuget commit `042f49a...`を収集し、全ハッシュを検証する。
検証用ZIPは450,885,351 bytes、131エントリ、SHA-256
`E72CC13E5B0D0015B8B1944D4353E4D1645C1870B38F616DB95FD9DC28927F29`となった。

`publish-phase2-release.ps1`へこのZIPを渡したframework-dependent発行は433ファイル、
103,959,943 bytes、プラグイン319件、GPL/AGPL 0件で合格した。ZIPはアプリフォルダーへ
重複内包せず、同じGitHub Releaseの別アセットとして配置し、名前とSHA-256を発行物台帳へ記録する。
