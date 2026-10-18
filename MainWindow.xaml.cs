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
        InitializeComponent();
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
