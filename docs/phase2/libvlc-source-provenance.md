# VideoLAN.LibVLC.Windows 3.0.23.1 x64 ソース来歴検証

実施日: 2026-08-02  
対象: `VideoLAN.LibVLC.Windows 3.0.23.1`から発行するx64 LibVLC資産

## 1. 結論

x64公開バイナリのライセンスゲートを、次の条件付きで技術監査上合格とする。

- `scripts/publish-phase2-release.ps1`でGPL-onlyプラグイン4件を除外し、残る319件の自己申告ライセンスを検査する。
- `scripts/collect-libvlc-corresponding-source.ps1`で生成・検証した対応ソースZIPを、アプリのバイナリと同じGitHub Releaseへ別アセットとして掲載する。
- LGPL 2.1本文、第三者通知、ソースZIP名とSHA-256を発行物へ含め、LibVLC DLLを交換可能な別ファイルとして維持する。
- リリーススクリプトによるソースZIP内131エントリのハッシュ再検証を通す。

この条件では、プロジェクト本体をMITのままGitHubで公開し、無償のx64バイナリを配布できると判断する。無償であること自体ではなく、LGPLの告知、交換可能性、対応ソース提供を満たすことが根拠である。

## 2. x64バイナリの来歴

| 対象 | 結果 |
| --- | --- |
| NuGet | `VideoLAN.LibVLC.Windows 3.0.23.1` |
| NuGet SHA-256 | `70927AFA9AD34B77E7D9A5E6D02CAE099771F6EB3114DA18111A4B76F65B836F` |
| 公式Windows配布物 | `vlc-3.0.23-win64.7z` |
| 公式・実測SHA-256 | `EB4FD8A28291DA73608C733786A09610FEA865FBE94113BCB60B91C1EBB8404A`、一致 |
| ファイル照合 | NuGet `build/x64`の525/525件が、公式Windows配布物の対応パスとSHA-256一致 |
| パッケージ定義 | NuGet同梱`VideoLAN.LibVLC.Windows.targets`のGit blob `4044199c...`が`libvlc-nuget` commit `042f49a49609b2da7aeea0c94e51f809cf2e1575`と一致 |

`libvlc-nuget`の監査対象commitは、公式VLC配布物をダウンロードして再梱包し、GPLリストを別パッケージへ分離する。NuGet nuspec自体は生成commitを宣言しないため、「NuGetがそのcommitから生成された」とメタデータだけで断定はしない。一方、配布対象のx64ネイティブ資産が公式VLC 3.0.23配布物と全件同一であるため、x64バイナリのソース来歴は公式配布物へ一意に結び付いた。

## 3. 対応ソース束

VLC本体にはVideoLAN公式`vlc-3.0.23.tar.xz`を使用し、公式SHA-256
`E891CAE6AA3CCDA69BF94173D5105CBC55C7A7D9B1D21B9B21666E69EFF3E7E0`と一致した。

同ソースの`contrib/src/*/SHA512SUMS`から126件のソース入力を抽出し、VideoLANミラーまたは同じ`rules.mak`が固定する一次URLから取得した。126/126件が指定SHA-512と一致した。さらに次を収録した。

- `LibVLCSharp` / `LibVLCSharp.WPF` commit `59d70e96026229e7c232ce5074ecefbf6f8959b6`
- `libvlc-nuget`監査対象commit `042f49a49609b2da7aeea0c94e51f809cf2e1575`
- 全URL、commit、SHA-256/SHA-512、525件のバイナリ照合結果を持つ`source-manifest.json`

Phase 2で生成した検証用ZIPは次のとおり。これは`.tmp`成果物であり、公開Releaseごとに再生成し、実際のSHA-256を`release-manifest.json`へ記録する。

- ファイル: `bakuretsu-osakana-kobo-libvlc-sources-3.0.23.1.zip`
- サイズ: 450,885,351 bytes
- SHA-256: `E72CC13E5B0D0015B8B1944D4353E4D1645C1870B38F616DB95FD9DC28927F29`
- ZIPエントリ: 131
- contrib: 126

## 4. 自動ゲート

`publish-phase2-release.ps1`は発行開始前に次を検査する。

1. 対応ソースが`collect-libvlc-corresponding-source.ps1`形式のZIPである。
2. NuGet、公式Windows配布物、VLC本体、LibVLCSharp、パッケージングcommit、525件一致、contrib 126件が監査値と一致する。
3. VLC、LibVLCSharp、libvlc-nuget、contrib 126件の実データをZIP内で再ハッシュする。
4. 合格後、ソースZIP名とSHA-256を`release-manifest.json`へ記録する。ソースZIPはアプリ本体へ内包せず、同じGitHub Releaseの別アセットとする。

正しいソースZIPを指定したframework-dependent発行は、433ファイル、103,959,943 bytes、LibVLCプラグイン319件、読取不能0件、GPL/AGPL 0件で合格した。不正な形式のアーカイブは発行ディレクトリ作成前に拒否した。

## 5. 公式資料

- [VideoLAN.LibVLC.Windows 3.0.23.1](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.23.1)
- [VLC 3.0.23 sources](https://download.videolan.org/pub/videolan/vlc/3.0.23/)
- [VLC 3.0.23 win64](https://download.videolan.org/pub/videolan/vlc/3.0.23/win64/)
- [VideoLAN contrib mirror](https://download.videolan.org/pub/contrib/)
- [libvlc-nuget](https://code.videolan.org/videolan/libvlc-nuget)
- [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp)
