using System.Collections.ObjectModel;
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
        RefreshAddresses();
        foreach (var root in runtime.Store.GetRoots()) _roots.Add(root);
        _refresh.Tick += (_, _) => RefreshStatus();
        _refresh.Start();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        RefreshAddresses();
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

    private void AddAllDrives_Click(object sender, RoutedEventArgs e)
    {
        AppRuntime.Server!.Store.AddAllDrives();
        _roots.Clear();
        foreach (var root in AppRuntime.Server.Store.GetRoots()) _roots.Add(root);
    }

    private void ShowQR_Click(object sender, RoutedEventArgs e) => new ConnectionQRWindow(AppRuntime.Server!) { Owner = this }.Show();

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is not SharedRoot selected) return;
        AppRuntime.Server!.Store.RemoveRoot(selected.Id);
        _roots.Remove(selected);
    }

    private void RefreshAddresses()
    {
        var vpn = LanAddressProvider.GetTailscaleAddress();
        AddressText.Text = $"同じWi-Fi: {GetLanAddress()}:8765" +
            (vpn is null ? "\n外出先: Tailscaleを接続してください" : $"\n外出先: {vpn}:8765（Tailscale）");
    }

    private void ExportSetup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "PCFileBridgeSetup.json", Filter = "接続設定 (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, LocalSetupBroker.CreateSetup(AppRuntime.Server!)); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "設定を書き出せませんでした"); }
    }

    private static string GetLanAddress()
    {
        return LanAddressProvider.GetIPv4Addresses().FirstOrDefault()?.ToString() ?? "IPv4のLAN接続が見つかりません";
    }
}
