using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Appearance;

namespace CloudRedirect;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\CloudRedirect_SingleInstance_Mutex_99214";
    private const string ShowWindowEventName = @"Local\CloudRedirect_ShowMainWindow_Event_99214";

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _showWindowEvent;
    private static Thread? _eventWaitThread;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    public static bool StartMinimized { get; private set; }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        bool isNewInstance;
        try
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out isNewInstance);
        }
        catch
        {
            isNewInstance = true;
        }

        if (!isNewInstance)
        {
            // Another instance of CloudRedirect is already running!
            // Signal the running instance to open / restore from tray and bring to foreground
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var showEvent))
                {
                    showEvent.Set();
                    showEvent.Dispose();
                }
            }
            catch { }

            // Exit immediately so only one instance runs
            Shutdown(0);
            return;
        }

        // We are the primary instance. Create the event wait handle for secondary instance signals
        try
        {
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            _eventWaitThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        if (_showWindowEvent.WaitOne())
                        {
                            Current?.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                BringToForeground();
                            }));
                        }
                    }
                    catch (ThreadAbortException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch
                    {
                        Thread.Sleep(200);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "CloudRedirect_SingleInstance_Listener"
            };
            _eventWaitThread.Start();
        }
        catch { }

        StartMinimized = e.Args.Any(a => a.Equals("-minimized", StringComparison.OrdinalIgnoreCase) ||
                                         a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        Services.LanguageService.ApplyLanguage(Services.LanguageService.ReadLanguagePreference(), save: false);
        base.OnStartup(e);
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
    }

    public static void BringToForeground()
    {
        try
        {
            Services.TrayIconService.Instance.RestoreFromTray();

            if (Current?.MainWindow != null)
            {
                var win = Current.MainWindow;
                if (!win.IsVisible)
                {
                    win.Show();
                }
                win.ShowInTaskbar = true;
                if (win.WindowState == WindowState.Minimized)
                {
                    win.WindowState = WindowState.Normal;
                }

                var hwnd = new WindowInteropHelper(win).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }

                win.Activate();
                win.Topmost = true;
                win.Topmost = false;
                win.Focus();
            }
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _showWindowEvent?.Dispose();
            if (_singleInstanceMutex != null)
            {
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
            }
        }
        catch { }
        base.OnExit(e);
    }
}
