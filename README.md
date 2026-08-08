# bakuretsu-osakana-kobo
video player

初回リリースで保証するMP4/WMVの内部コーデック、解像度、fps、および保証外の扱いは、
[`docs/supported-media-formats.md`](docs/supported-media-formats.md)を参照してください。

## Development

Windows上で、`global.json`に固定された.NET 10 SDKを使用します。

```powershell
dotnet restore BakuretsuOsakanaKobo.slnx --locked-mode
dotnet build BakuretsuOsakanaKobo.slnx --configuration Release --no-restore
dotnet test BakuretsuOsakanaKobo.slnx --configuration Release --no-build
```

依存関係を変更した場合は、通常の`dotnet restore BakuretsuOsakanaKobo.slnx`で
各`packages.lock.json`を更新し、内容をレビューしてください。
