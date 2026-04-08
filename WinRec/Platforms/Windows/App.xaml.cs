using Microsoft.UI.Xaml;
using WinRec.Platforms.Windows;
using WinRec.Services;

namespace WinRec.WinUI;

/// <summary>
/// WinUI application host. Sets up the system-tray icon so the app can minimise
/// there instead of closing, and wires tray menu actions to the recording service.
/// </summary>
public partial class App : MauiWinUIApplication
{
    private TrayIconService? _tray;

    public App()
    {
        this.InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        // Give MAUI a moment to create the window, then attach the tray icon
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
            ?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, AttachTrayIcon);
    }

    private void AttachTrayIcon()
    {
        var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
        if (mauiWindow?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window winUIWindow)
            return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(winUIWindow);
        if (hwnd == IntPtr.Zero) return;

        _tray = new TrayIconService();
        _tray.ShowRequested   += () => RestoreWindow(winUIWindow);
        _tray.ExitRequested   += ExitApp;
        _tray.RecordRequested += StartRecording;
        _tray.StopRequested   += StopRecording;

        _tray.Create(hwnd);
    }

    private static void RestoreWindow(Microsoft.UI.Xaml.Window window)
    {
        window.DispatcherQueue.TryEnqueue(() =>
        {
            window.Activate();
            var appWindow = window.AppWindow;
            if (appWindow != null)
            {
                appWindow.Show();
                appWindow.MoveInZOrderAtTop();
            }
        });
    }

    private static void StartRecording()
    {
        var svc = IPlatformApplication.Current?.Services
                       .GetService<AudioRecordingService>();
        if (svc != null)
            _ = svc.StartRecordingAsync();
    }

    private static void StopRecording()
    {
        var svc = IPlatformApplication.Current?.Services
                       .GetService<AudioRecordingService>();
        if (svc != null)
            _ = svc.StopRecordingAsync();
    }

    private void ExitApp()
    {
        _tray?.Dispose();
        var svc = IPlatformApplication.Current?.Services
                      .GetService<AudioRecordingService>();
        svc?.Dispose();
        System.Environment.Exit(0);
    }
}

