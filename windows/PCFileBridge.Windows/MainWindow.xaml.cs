using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PCFileBridge.Windows;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<SharedRoot> _roots = [];
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        InitializeComponent();
        RootsList.ItemsSource = _roots;
        var runtime = AppRuntime.Server!;
        ComputerText.Text = $"PC名: {Environment.MachineName}";
        AddressText.Text = $"接続先: {GetLanAddress()}:{runtime.Port}";
        foreach (var root in runtime.Store.GetRoots()) _roots.Add(root);
        _refresh.Tick += (_, _) => RefreshStatus();
        _refresh.Start();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        PairingCodeText.Text = AppRuntime.Server!.Store.PairingCode;
        var devices = AppRuntime.Server.Store.GetDevices();
        DevicesText.Text = devices.Count == 0 ? "まだ接続されていません" : string.Join("\n", devices.Select(d => $"{d.Name}　最終アクセス: {d.LastAccessLocal}"));
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "iPhoneに公開するフォルダーを選択してください", UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        try
        {
            var added = AppRuntime.Server!.Store.AddRoot(dialog.SelectedPath);
            _roots.Add(added);
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "追加できませんでした", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is not SharedRoot selected) return;
        AppRuntime.Server!.Store.RemoveRoot(selected.Id);
        _roots.Remove(selected);
    }

    private static string GetLanAddress()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        foreach (var address in ni.GetIPProperties().UnicastAddresses)
            if (address.Address.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(address.Address)) return address.Address.ToString();
        return "127.0.0.1";
    }
}
