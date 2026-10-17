using System.Windows;

namespace PCFileBridge.Windows;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource _setupCancellation = new();
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--export-connection")
        {
            try { await LocalSetupBroker.ExportAsync(e.Args[1]); Shutdown(0); }
            catch { Shutdown(1); }
            return;
        }
        try
        {
            AppRuntime.Server = await ServerHost.StartAsync();
            if (e.Args.Contains("--share-all-drives")) AppRuntime.Server.Store.AddAllDrives();
            _ = LocalSetupBroker.ServeAsync(AppRuntime.Server, _setupCancellation.Token);
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"サーバーを起動できませんでした。\n{ex.Message}", "PC File Bridge", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _setupCancellation.Cancel();
        if (AppRuntime.Server is not null) await AppRuntime.Server.StopAsync();
        base.OnExit(e);
    }
}

public static class AppRuntime
{
    public static ServerHost? Server { get; set; }
}
