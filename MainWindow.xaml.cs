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
    private string? coordinateError;
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
            CoordinateControls.IsEnabled = state.Bindings.Length == 1 && state.SupportsCustomPosition && state.Bindings[0].Position.Length == 3;
            KeyboardStart.IsEnabled = state.SupportsKeyboard && state.Bindings.Length == 1;
            ArgTrialStop.IsEnabled = true;
            ArgTrialStatus.Text = state.Phase switch
            {
                "waiting_focus" => "Chuyển về game, ẩn con trỏ trong 15 giây để bắt đầu.",
                "active" when state.ArgumentKind == "keyboard" => "Đang bật mũi tên. F8 hoặc Tắt camera để về gameplay.",
                "active" => "Đang giữ camera. Bấm Tắt để về gameplay.",
                "rejected" => "Backend từ chối: " + state.Reason,
                "completed" or "stopped" when state.LastOverride > 0 && state.LastOriginal > state.LastOverride
                    => "Đã trả argument gốc. Kiểm tra camera đã trở lại bình thường.",
                "completed" or "stopped" => "Đã dừng: " + state.Reason,
                _ => state.Bindings.Length == 1 ? "Chọn tọa độ và bật giữ camera." : "Đang chờ đích pose duy nhất."
            };
            if (coordinateError != null) ArgTrialStatus.Text = coordinateError;
        }
        catch
        {
            if (!argumentClosing && argumentCommand == null)
            {
                ArgTrialStop.IsEnabled = false;
                CoordinateControls.IsEnabled = false;
                KeyboardStart.IsEnabled = false;
                ArgTrialStatus.Text = "Cần game chạy với plugin bản thử camera.";
            }
        }
        finally { argumentRefreshing = false; }
    }

    private async void ArgumentTrialStopClick(object sender, RoutedEventArgs e)
    {
        if (argumentClosing || argumentCommand != null) return;
        coordinateError = null;
        ArgTrialStop.IsEnabled = CoordinateControls.IsEnabled = KeyboardStart.IsEnabled = false;
        try
        {
            argumentCommand = Task.Run(() => argumentTrial.Stop());
            await argumentCommand;
            ArgTrialStatus.Text = "Đã gửi Tắt camera.";
        }
        catch (Exception ex) { ArgTrialStatus.Text = coordinateError = ex.Message; }
        finally { argumentCommand = null; }
    }

    private async void KeyboardStartClick(object sender, RoutedEventArgs e)
    {
        if (argumentClosing || argumentCommand != null) return;
        coordinateError = null;
        if (!double.TryParse(KeyboardSpeed.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var speed) || !double.IsFinite(speed) || speed <= 0)
        {
            ArgTrialStatus.Text = coordinateError = "Nhập tốc độ dương, dùng dấu chấm thập phân.";
            return;
        }
        KeyboardStart.IsEnabled = CoordinateControls.IsEnabled = ArgTrialStop.IsEnabled = false;
        try
        {
            argumentCommand = Task.Run(() => argumentTrial.StartKeyboard(speed));
            await argumentCommand;
            ArgTrialStatus.Text = "Đã gửi bật phím mũi tên. Quay về game và ẩn con trỏ để di chuyển.";
        }
        catch (Exception ex) { ArgTrialStatus.Text = coordinateError = ex.Message; }
        finally { argumentCommand = null; }
    }

    private void CoordinateModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        coordinateError = null;
        if (CoordinateX != null && CoordinateY != null && CoordinateZ != null)
            CoordinateX.Text = CoordinateY.Text = CoordinateZ.Text = "0";
    }

    private async void ReadCoordinatesClick(object sender, RoutedEventArgs e)
    {
        if (argumentClosing || argumentCommand != null) return;
        coordinateError = null;
        CoordinateControls.IsEnabled = false;
        try
        {
            var read = Task.Run(argumentTrial.Read); argumentCommand = read;
            var state = await read;
            if (argumentClosing) return;
            if (!state.SupportsCustomPosition || state.Bindings.Length != 1 || state.Bindings[0].Position.Length != 3)
                throw new InvalidOperationException("Chưa có XYZ camera hiện tại.");
            CoordinateMode.SelectedIndex = 0;
            var position = state.Phase == "active" && state.LastOverride > 0 &&
                state.ArgumentKind is "keyboard" or "custom_position" &&
                state.AppliedPosition.Length == 3 && state.AppliedPosition.All(double.IsFinite)
                ? state.AppliedPosition : state.Bindings[0].Position;
            CoordinateX.Text = position[0].ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            CoordinateY.Text = position[1].ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            CoordinateZ.Text = position[2].ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            ArgTrialStatus.Text = "Đã lấy XYZ camera theo scene hiện tại.";
        }
        catch (Exception ex) { ArgTrialStatus.Text = coordinateError = ex.Message; }
        finally { argumentCommand = null; }
    }

    private async void ApplyCoordinatesClick(object sender, RoutedEventArgs e)
    {
        if (argumentClosing || argumentCommand != null) return;
        coordinateError = null;
        static double Number(string text)
        {
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new ArgumentException("Nhập XYZ là số hữu hạn, dùng dấu chấm thập phân.");
            return value;
        }
        try
        {
            var x = Number(CoordinateX.Text); var y = Number(CoordinateY.Text); var z = Number(CoordinateZ.Text);
            var offset = CoordinateMode.SelectedIndex == 1;
            CoordinateControls.IsEnabled = ArgTrialStop.IsEnabled = KeyboardStart.IsEnabled = false;
            argumentCommand = Task.Run(() => argumentTrial.StartPosition(x, y, z, offset));
            await argumentCommand;
            ArgTrialStatus.Text = "Đã gửi XYZ. Camera giữ đến khi bấm Tắt.";
        }
        catch (Exception ex) { ArgTrialStatus.Text = coordinateError = ex.Message; }
        finally { argumentCommand = null; }
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
            ArgTrialStop.IsEnabled = false;
            CoordinateControls.IsEnabled = false;
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
