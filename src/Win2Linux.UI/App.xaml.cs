using Microsoft.UI.Xaml;
using System.IO;

namespace Win2Linux_UI;

public partial class App : Application
{
    public static Window? MainWindowInstance { get; private set; }

    public App()
    {
        InitializeComponent();

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            try
            {
                var logFile = Path.Combine(AppContext.BaseDirectory, "debug.log");
                File.AppendAllText(logFile, $"[ProcessExit at {DateTime.Now}]:\nStack trace:\n{Environment.StackTrace}\n");
            }
            catch { }
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                var logFile = Path.Combine(AppContext.BaseDirectory, "crash.log");
                File.AppendAllText(logFile, $"\n[AppDomain.UnhandledException at {DateTime.Now}]:\n{e.ExceptionObject}\n");
            }
            catch { }
        };

        UnhandledException += (s, e) =>
        {
            try
            {
                var logFile = Path.Combine(AppContext.BaseDirectory, "crash.log");
                File.AppendAllText(logFile, $"\n[App.UnhandledException at {DateTime.Now}]:\n{e.Message}\n{e.Exception}\n");
            }
            catch { }
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug.log"), $"[OnLaunched at {DateTime.Now}] Creating MainWindow...\n");
            var win = new MainWindow();
            MainWindowInstance = win;

            win.Closed += (s, e) =>
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug.log"), $"[Window.Closed at {DateTime.Now}]\n");
            };

            win.Activate();
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug.log"), $"[OnLaunched at {DateTime.Now}] MainWindow activated successfully.\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), $"[OnLaunched Exception at {DateTime.Now}]:\n{ex}\n");
            throw;
        }
    }
}
