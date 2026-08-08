# LibVLC WPF バックエンド・ゲート

Phase 2でWPF + LibVLCSharp + VideoLAN公式LGPL版LibVLCの実現性を確認するための破棄可能な
技術スパイクである。本番UIの設計案ではない。

## 固定依存

- .NET 10
- `LibVLCSharp` 3.10.0
- `LibVLCSharp.WPF` 3.10.0
- `VideoLAN.LibVLC.Windows` 3.0.23.1

`VideoLAN.LibVLC.Windows.GPL` は追加しない。
また、非GPL版3.0.23.1へ誤って含まれる次のGPLv2+プラグインは、MSBuild設定で発行物から
除外する。

- `plugins/audio_filter/libdolby_surround_decoder_plugin.dll`
- `plugins/audio_filter/libheadphone_channel_mixer_plugin.dll`
- `plugins/codec/libx26410b_plugin.dll`
- `plugins/lua/liblua_plugin.dll`

## 実行

```powershell
dotnet restore spikes/LibVlcWpfSpike/LibVlcWpfSpike.csproj --locked-mode
dotnet run --project spikes/LibVlcWpfSpike/LibVlcWpfSpike.csproj
```

起動時に動画を直接開く場合は、動画の絶対パスを引数へ指定する。

```powershell
dotnet run --project spikes/LibVlcWpfSpike/LibVlcWpfSpike.csproj -- `
  "Z:\path\to\video.mp4"
```

初回のlock file生成前だけは `--locked-mode` を外してrestoreする。

発行物のLibVLCプラグインがGPL-onlyでないことは、各DLL自身が公開するライセンス関数を使って
次のように検査する。

```powershell
./scripts/verify-libvlc-plugin-licenses.ps1 `
  -LibVlcDirectory spikes/LibVlcWpfSpike/bin/Release/net10.0-windows/win-x64/publish/libvlc/win-x64
```

## 確認できる項目

- mp4/wmv選択と自動再生
- 再生・一時停止
- 正規化位置によるシーク
- `hh:mm:ss / hh:mm:ss` 表示
- 0.25～2.0倍速の切替要求
- 0～500%の音量要求値と、LibVLCが返す実値の比較
- WPF VideoView上のオーバーレイ表示
- LibVLCの非同期エラー通知

500%相当の音量とピーク抑制、任意位置フレーム取得、コーデック別保証、性能測定は未検証である。
特に音量UIの500%は要求を送って戻り値を観察するためのもので、品質や実効増幅を保証しない。

## 手動記録

各代表動画について次を記録する。

| 項目 | 結果 |
| --- | --- |
| 映像・音声付きで開始 | 未実施 |
| 中間・末尾シーク | 未実施 |
| 0.25～2.0倍速と音声品質 | 2026-08-02、同期専用MP4/WMVの0.25・0.5・2.0倍で主観合格。切替直後に短い音飛びあり |
| 100～500%要求時の実値 | 未実施 |
| オーバーレイ、リサイズ、DPI | 未実施 |
| エラー種別と既存状態維持 | 未実施 |
