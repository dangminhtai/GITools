# GCT FOV

Mở `App/GCT.Fov.exe`. Kéo thanh FOV là tự gửi, không cần bấm Apply.

Dùng config hiện có: `C:\GCT\Backends\z3lx-7.1.0\plugin_config.json`.
Game cần plugin FOV đang chạy để hình ảnh thay đổi. App không tự nạp plugin.

Build bằng .NET 10 SDK:
```powershell
dotnet publish GCT.Fov.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o App
```

Đổi đường dẫn khi cần: `App\GCT.Fov.exe --config "C:\duong-dan\plugin_config.json"`.
