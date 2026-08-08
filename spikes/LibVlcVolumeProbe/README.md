# LibVLC volume probe

Phase 2の`VOL-003`向けに、100～500%の増幅とピーク抑制候補を物理スピーカーへ
出力せず検証する破棄可能なコンソールスパイク。

LibVLCの通常の音声処理経路を通した結果をLGPLの`file` audio outputでfloat32 WAVへ
書き出し、ピーク、範囲外サンプル数、区間RMSを測定する。入力はプログラム自身が生成する
3.2秒の正弦波で、-30 dBFS、-12 dBFS、-0.4 dBFSの3区間を含む。追加ライブラリを
使わない先読みリミッター候補も同じ入力で測定する。

```powershell
dotnet run --project spikes/LibVlcVolumeProbe/LibVlcVolumeProbe.csproj --configuration Release -- .tmp/libvlc-volume-probe

# 自作リミッター候補の自動ゲートだけを実行
dotnet run --project spikes/LibVlcVolumeProbe/LibVlcVolumeProbe.csproj --configuration Release -- .tmp/libvlc-volume-probe custom-limiter
```

この測定は聴感品質を保証しない。物理出力による確認は、無音測定で安全な方式を絞り込んだ
後に、OS・アンプ・スピーカーの音量を最小から調整して別途行う。
