# LibVLC playback probe

Phase 2の代表動画を画面・音声機器なしで実デコードし、LibVLCバックエンドの互換性を自動確認する。
製品UIや主観的な映像・音声品質を検証するものではない。

```powershell
dotnet restore spikes/LibVlcPlaybackProbe/LibVlcPlaybackProbe.csproj --locked-mode
dotnet run `
  --project spikes/LibVlcPlaybackProbe/LibVlcPlaybackProbe.csproj `
  --configuration Release `
  -- `
  test-assets/generated `
  docs/phase2/results/libvlc-playback-probe.json
```

第3引数に期待ファイル数を指定できる。同期専用素材2本だけを検証する例:

```powershell
dotnet run `
  --project spikes/LibVlcPlaybackProbe/LibVlcPlaybackProbe.csproj `
  --configuration Release `
  -- `
  test-assets/generated/av-sync `
  docs/phase2/results/libvlc-av-sync-playback-probe.json `
  2
```

検証内容:

- LibVLCによるメディア解析、コーデック、解像度、fps、長さ、音声形式
- RV32映像フレームとS16N 48 kHz stereo音声サンプルのデコード
- 再生時計の進行、一時停止、再開
- 中間・末尾シーク後の3フレーム以上のデコードと時計進行
- 60秒素材で0.25～2.0倍速要求

`--avcodec-hw=none`を使用するため、結果はソフトウェアデコードのバックエンド・ゲートである。
WPF `VideoView`、GPUデコード、通常音声出力、音量増幅、A/V同期の目視・聴取は別途確認する。

WPFスパイクと同じGPL-onlyプラグイン4件をMSBuildで除外する。Release出力は
`scripts/verify-libvlc-plugin-licenses.ps1`で検査すること。
