# GCT FOV

Mở `App/GCT.Fov.exe`. Kéo thanh FOV là tự gửi, không cần bấm Apply.

FPS: nhập số rồi bấm **Áp dụng FPS**.

Camera: bấm **Bật mũi tên** để dùng ↑/↓ tiến/lùi theo hướng camera, ←/→ sang trái/phải. **Page Up/Page Down** lên/xuống theo trục cao của scene. Ô tốc độ tính bằng đơn vị scene/giây. WASD và Q/E dành cho gameplay; **F8** hoặc **Tắt camera** ngừng override. Alt+Tab ngừng nhận phím, vẫn giữ vị trí camera. Phím camera chỉ nhận khi game có focus và con trỏ ẩn.

Tọa độ camera: chọn **Vị trí XYZ (scene)**, bấm **Lấy XYZ hiện tại** rồi sửa X/Y/Z; hoặc chọn **Độ lệch XYZ** và nhập lượng dịch thêm. **Bật / cập nhật XYZ** giữ điểm đích đến khi tắt, không giới hạn 3 giây hay khoảng cách 5 đơn vị. Độ lệch tính một lần từ vị trí khi bắt đầu, không cộng dồn mỗi frame. Đóng app điều khiển cũng gửi yêu cầu dừng. GUI không tự chạy loader hoặc nạp DLL.

Dùng config hiện có: `C:\GCT\Backends\z3lx-7.1.0\plugin_config.json`.
Game cần plugin FOV đang chạy để hình ảnh thay đổi. App không tự nạp plugin.

Build bằng .NET 10 SDK:
```powershell
dotnet publish GCT.Fov.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o App
```

Đổi đường dẫn khi cần: `App\GCT.Fov.exe --config "C:\duong-dan\plugin_config.json"`.

## Source backend

[Patch native](backends/z3lx/gct-camera.patch) chứa FreeCamCore, NativePoseHook và NativeArgumentTrial cùng thay đổi CMake/đăng ký component. Patch dựa trên z3lx commit `1b8951d2062fbd09bd170730ace1813de8ccf85d`; snapshot fork local tương ứng `5a5baf3`. [MIT license upstream](backends/z3lx/LICENSE) được giữ nguyên. Không kèm loader, game binary hoặc DLL đã build.

Áp vào checkout sạch rồi chỉ build target plugin, với Visual Studio 2022/MSVC 19.43 trở lên và CMake:

```powershell
git clone https://github.com/z3lx/genshin-unlock.git .build/z3lx
git -C .build/z3lx checkout 1b8951d2062fbd09bd170730ace1813de8ccf85d
$patch = (Resolve-Path .\backends\z3lx\gct-camera.patch).Path
git -C .build/z3lx apply --check $patch
git -C .build/z3lx apply $patch
cmake -S .build/z3lx -B .build/native -G "Visual Studio 17 2022" -A x64 -DGCT_NATIVE_POSE_OBSERVER=ON -DGCT_NATIVE_ARGUMENT_TRIAL=ON -DGCT_NATIVE_POSE_EXPERIMENT=OFF -DGCT_BUILD_CAMERA_TESTS=OFF
cmake --build .build/native --config Release --target plugin --parallel 1
```

Backend kiểm hash executable `08A3086D5F3FE695F01DAB61EFA42E442006B18E5E475B2520DF356F6A073B7D` và các entry đã phân tích; không tự tương thích với bản game khác. FreeCamCore chưa được gắn với một adapter ownership/lifecycle đã xác minh. Điều khiển hiện tại thay bản sao argument của setter native; không sửa source pose của owner hoặc mở gate cũ bằng boolean.

## Kiểm chứng — 19/10/2026

- Release plugin MSVC 19.43 và desktop publish: **PASS** trên máy phát triển. Hướng dẫn clone/build ở checkout mới chưa chạy độc lập.
- Kiểm tra WPF/config FOV/FPS: **PASS**. FOV/FPS source giữ nguyên trong các thay đổi camera.
- Các phép thử XYZ và yaw/pitch ngắn hạn trước đây: **USER CONFIRMED** có tác dụng trong game; không thay thế kiểm chứng bản mới.
- Bản sáu hướng đã cài local, config FOV/FPS không đổi; di chuyển phím, tách input nhân vật và OFF/restore của bản này: **NOT TESTED** trực tiếp.
- Quaternion XYZW và hướng di chuyển vẫn cần đối chiếu live; chưa chứng minh last-writer, render binding hoặc lifecycle qua mọi lần đổi scene. Đổi owner/thread/target, pose không hợp lệ hoặc lỗi IPC sẽ dừng override.

DLL, ảnh chụp, log runtime và trạng thái triển khai riêng của máy không được đưa lên repository.
