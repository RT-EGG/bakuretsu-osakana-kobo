# LibVLC real-time audio probe

LibVLCのPCMコールバック、500%増幅、先読みリミッター、NAudioバッファを接続する
Phase 2用スパイク。

この段階ではWASAPI出力を開かない。既定デバイスの情報を読み取るだけで、処理済みPCMは
10 ms周期の模擬コンシューマーがNAudioの`BufferedWaveProvider`から消費する。

```powershell
dotnet run --project spikes/LibVlcRealtimeAudioProbe/LibVlcRealtimeAudioProbe.csproj `
  --configuration Release -- `
  .tmp/libvlc-probe-smoke/phase2-1920x1080-5s-30fps-h264-aac.mp4 `
  .tmp/libvlc-realtime-audio/report.json `
  500
```

合格条件は、-1 dBFS上限、範囲外サンプル0、バッファオーバーフロー0、プリバッファ後の
アンダーラン0、入力・出力フレーム数一致である。これは実音やA/V同期の合格を意味しない。

速度切替時のPCM供給を測る場合は、7番目の引数へ`rate-transitions`、8番目へ
`default`、`low-latency`または`no-stretch`を指定する。WASAPIは開かない。

```powershell
spikes/LibVlcRealtimeAudioProbe/bin/x64/Release/net10.0-windows/win-x64/LibVlcRealtimeAudioProbe.exe `
  test-assets/generated/phase2-1920x1080-60s-30fps-h264-aac.mp4 `
  .tmp/libvlc-rate-transitions/default.json `
  500 simulated -40 5 rate-transitions default
```

このシナリオでのアンダーランは、速度変更による供給停止を検出するための結果であり、
詳細な判断は`docs/phase2/rate-transition-investigation.md`を参照する。
