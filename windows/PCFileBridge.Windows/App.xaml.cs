using System.Windows;

namespace PCFileBridge.Windows;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            AppRuntime.Server = await ServerHost.StartAsync();
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
        if (AppRuntime.Server is not null) await AppRuntime.Server.StopAsync();
        base.OnExit(e);
    }
}

public static class AppRuntime
{
    public static ServerHost? Server { get; set; }
}
