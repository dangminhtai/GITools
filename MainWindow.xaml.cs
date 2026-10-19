using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace GctFov;

public partial class MainWindow : Window
{
    private readonly FovConfig config;
    private readonly FpsConfig fpsConfig;
    private Task? fpsWriting;
    private readonly ArgumentTrialClient argumentTrial;
    private readonly DispatcherTimer argumentTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool argumentRefreshing, argumentClosing, argumentCloseComplete;
    private Task? argumentCommand;
    private string? cameraError;
    private (int Pid, long Session)? cameraSession;
    private (int Pid, long Session, string Nonce)? keyboardRequest;
    private bool keyboardAutoAttempted;
    private bool keyboardEventsReady, syncingKeyboardCheckbox;
    private bool resettingCamera;
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool ready, closing;
    private long revision, appliedRevision;
    private Task<bool>? writing;

    public MainWindow()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--config");
        config = new FovConfig(index >= 0 && index + 1 < args.Length ? args[index + 1] : FovConfig.DefaultPath);
        fpsConfig = new FpsConfig(index >= 0 && index + 1 < args.Length ? args[index + 1] : FovConfig.DefaultPath);
        argumentTrial = new ArgumentTrialClient(index >= 0 && index + 1 < args.Length ? args[index + 1] : FovConfig.DefaultPath);
        InitializeComponent();
        keyboardEventsReady = true;
        argumentTimer.Tick += async (_, _) => await RefreshArgumentTrial();
        debounce.Tick += async (_, _) => { debounce.Stop(); await ApplyLatest(); };
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var value = await Task.Run(config.Read);
            FovSlider.Value = value;
            FovNumber.Text = $"{value}°";
            FovSlider.SelectionEnd = value;
            FovSlider.IsEnabled = ready = true;
            Status.Text = "Kéo thanh để đổi FOV.";
        }
        catch (Exception ex) { Status.Text = ErrorText(ex); }
        try
        {
            FpsInput.Text = (await Task.Run(fpsConfig.Read)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            FpsInput.IsEnabled = ApplyFpsButton.IsEnabled = true;
        }
        catch { FpsStatus.Text = "Không đọc được cấu hình FPS."; }
        await RefreshArgumentTrial();
        argumentTimer.Start();
    }

    private async Task RefreshArgumentTrial()
    {
        if (argumentRefreshing || argumentClosing || argumentCommand != null) return;
        argumentRefreshing = true;
        try
        {
            var state = await Task.Run(argumentTrial.Read);
            if (argumentClosing || argumentCommand != null) return;
            var session = (state.Pid, state.Session);
            if (cameraSession != session)
            {
                cameraSession = session;
                keyboardRequest = null;
                keyboardAutoAttempted = false;
                cameraError = null;
            }
            // Initial check is user intent, not an assertion that the backend is active.
            // Send once when ready; a rejection or F8 must not create a re-arm loop.
            if (KeyboardStart.IsChecked == true && !keyboardAutoAttempted && state.SupportsKeyboard && state.Bindings.Length == 1)
            {
                keyboardAutoAttempted = true;
                await EnableKeyboard();
                return;
            }
            if (keyboardRequest is { } request && request.Pid == state.Pid && request.Session == state.Session &&
                request.Nonce == state.Nonce && state.Phase is "stopped" or "completed" or "rejected")
            {
                SetKeyboardChecked(false);
                keyboardRequest = null;
            }
            ArgTrialStatus.Text = state.Phase switch
            {
                "waiting_focus" => "Chuyển về game, ẩn con trỏ trong 15 giây để bắt đầu.",
                "active" when state.ArgumentKind == "keyboard" => "Đang bật điều khiển phím. Bỏ tick hoặc F8 để tắt.",
                "active" => "Đang giữ camera. Bỏ tick để tắt.",
                "rejected" => "Backend từ chối: " + state.Reason,
                "completed" or "stopped" when state.LastOverride > 0 && state.LastOriginal > state.LastOverride
                    => "Đã trả argument gốc. Kiểm tra camera đã trở lại bình thường.",
                "completed" or "stopped" => "Đã dừng: " + state.Reason,
                _ => KeyboardStart.IsChecked == true ? "Đang chờ backend camera sẵn sàng để bật." : "Điều khiển camera đã tắt."
            };
            if (cameraError != null) ArgTrialStatus.Text = cameraError;
        }
        catch
        {
            if (!argumentClosing && argumentCommand == null)
            {
                ArgTrialStatus.Text = KeyboardStart.IsChecked == true
                    ? "Đã chọn bật; đang chờ game và backend camera."
                    : "Điều khiển camera đã tắt.";
            }
        }
        finally { argumentRefreshing = false; }
    }

    private void SetKeyboardChecked(bool value)
    {
        syncingKeyboardCheckbox = true;
        try { KeyboardStart.IsChecked = value; }
        finally { syncingKeyboardCheckbox = false; }
    }

    private async void KeyboardToggleChanged(object sender, RoutedEventArgs e)
    {
        if (!keyboardEventsReady || syncingKeyboardCheckbox || argumentClosing || argumentCommand != null) return;
        cameraError = null;
        if (KeyboardStart.IsChecked == true)
        {
            keyboardAutoAttempted = false;
            await RefreshArgumentTrial();
            return;
        }
        await StopKeyboard();
    }

    private async void CameraResetClick(object sender, RoutedEventArgs e)
    {
        if (argumentClosing || resettingCamera) return;
        resettingCamera = true;
        CameraReset.IsEnabled = false;
        SetKeyboardChecked(false);
        keyboardAutoAttempted = true;
        try
        {
            if (argumentCommand != null) { try { await argumentCommand; } catch { } }
            await StopKeyboard();
        }
        finally { resettingCamera = false; CameraReset.IsEnabled = !argumentClosing; }
    }

    private async Task StopKeyboard()
    {
        if (argumentClosing || argumentCommand != null) return;
        cameraError = null;
        KeyboardStart.IsEnabled = false;
        try
        {
            argumentCommand = Task.Run(() => argumentTrial.Stop());
            await argumentCommand;
            keyboardRequest = null;
            ArgTrialStatus.Text = "Đã gửi tắt camera.";
        }
        catch (Exception ex)
        {
            // With no session/request, an unchecked offline checkbox simply cancels pending activation.
            if (keyboardRequest != null) ArgTrialStatus.Text = cameraError = ex.Message;
            else ArgTrialStatus.Text = "Điều khiển camera đã tắt.";
        }
        finally { argumentCommand = null; KeyboardStart.IsEnabled = !argumentClosing; }
    }

    private async Task EnableKeyboard()
    {
        if (argumentClosing || argumentCommand != null) return;
        cameraError = null;
        if (!double.TryParse(KeyboardSpeed.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var speed) || !double.IsFinite(speed) || speed <= 0)
        {
            ArgTrialStatus.Text = cameraError = "Nhập tốc độ dương, dùng dấu chấm thập phân.";
            SetKeyboardChecked(false);
            return;
        }
        KeyboardStart.IsEnabled = false;
        try
        {
            var request = Task.Run(() => argumentTrial.StartKeyboard(speed));
            argumentCommand = request;
            await argumentCommand;
            keyboardRequest = await request;
            ArgTrialStatus.Text = "Đã gửi bật điều khiển phím; đang chờ backend xác nhận.";
        }
        catch (Exception ex) { SetKeyboardChecked(false); ArgTrialStatus.Text = cameraError = ex.Message; }
        finally { argumentCommand = null; KeyboardStart.IsEnabled = !argumentClosing; }
    }

    private async void ApplyFpsClick(object sender, RoutedEventArgs e)
    {
        if (fpsWriting != null || closing) return;
        if (!int.TryParse(FpsInput.Text, out var value) || value is < 1 or > 1000)
        {
            FpsStatus.Text = "Nhập FPS từ 1 đến 1000.";
            return;
        }
        FpsInput.IsEnabled = ApplyFpsButton.IsEnabled = false;
        FpsStatus.Text = "Đang gửi FPS…";
        try
        {
            fpsWriting = Task.Run(() => fpsConfig.Write(value));
            await fpsWriting;
            FpsStatus.Text = $"Đã gửi {value} FPS.";
        }
        catch { FpsStatus.Text = "Chưa gửi được FPS. Thử lại."; }
        finally
        {
            fpsWriting = null;
            FpsInput.IsEnabled = ApplyFpsButton.IsEnabled = true;
        }
    }

    private void FovChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || closing) return;
        var value = (int)Math.Round(e.NewValue);
        FovNumber.Text = $"{value}°";
        FovSlider.SelectionEnd = value;
        ++revision;
        Status.Text = "Đang gửi…";
        if (!debounce.IsEnabled) debounce.Start();
    }

    private Task<bool> ApplyLatest()
    {
        if (writing != null) return writing;
        if (revision == appliedRevision) return Task.FromResult(true);
        return writing = WritePending();
    }

    private async Task<bool> WritePending()
    {
        try
        {
            while (revision != appliedRevision)
            {
                var version = revision;
                var value = (int)Math.Round(FovSlider.Value);
                await Task.Run(() => config.Write(value));
                appliedRevision = version;
                if (revision == version) Status.Text = $"Đã gửi {value}°.";
            }
            return true;
        }
        catch (Exception ex) { Status.Text = ErrorText(ex); return false; }
        finally { writing = null; }
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!argumentCloseComplete)
        {
            e.Cancel = true;
            if (argumentClosing) return;
            argumentClosing = true;
            argumentTimer.Stop();
            KeyboardStart.IsEnabled = false;
            if (argumentCommand != null) { try { await argumentCommand; } catch { } }
            try { await Task.Run(() => argumentTrial.Stop(onlyOwnSession: true)); } catch { }
            argumentCloseComplete = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
            return;
        }
        if (!closing && fpsWriting != null)
        {
            e.Cancel = true;
            await fpsWriting.ContinueWith(_ => { }, TaskScheduler.Default);
            fpsWriting = null;
            Close();
            return;
        }
        if (closing || !ready || revision == appliedRevision) return;
        e.Cancel = true;
        closing = true;
        FovSlider.IsEnabled = false;
        debounce.Stop();
        await ApplyLatest();
        Close();
    }

    private static string ErrorText(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "Không tìm thấy plugin_config.json của backend FOV.",
        UnauthorizedAccessException => "Không có quyền ghi config FOV.",
        IOException => "Config đang bận hoặc đã đổi. Kéo lại để gửi FOV.",
        _ => "Config FOV không hợp lệ. Chưa gửi thay đổi."
    };
}
