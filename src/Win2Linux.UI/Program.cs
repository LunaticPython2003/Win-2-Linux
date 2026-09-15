using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.IO;

namespace Win2Linux_UI;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug.log"), $"[Program.Main at {DateTime.Now}] Starting ComWrappers...\n");
            WinRT.ComWrappersSupport.InitializeComWrappers();

            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug.log"), $"[Program.Main at {DateTime.Now}] Calling Application.Start...\n");

            Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });

            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug.log"), $"[Program.Main at {DateTime.Now}] Application.Start returned!\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), $"[Program.Main Exception at {DateTime.Now}]:\n{ex}\n");
        }
    }
}
