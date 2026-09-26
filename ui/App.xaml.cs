using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Wpf.Ui.Appearance;

namespace CloudRedirect;

public partial class App : System.Windows.Application
{
    public static bool StartMinimized { get; private set; }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        StartMinimized = e.Args.Any(a => a.Equals("-minimized", StringComparison.OrdinalIgnoreCase) ||
                                         a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        Services.LanguageService.ApplyLanguage(Services.LanguageService.ReadLanguagePreference(), save: false);
        base.OnStartup(e);
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
    }
}
