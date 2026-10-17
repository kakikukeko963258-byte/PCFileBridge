using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;

namespace PCFileBridge.Windows;

internal sealed class ConnectionQRWindow : Window
{
    private readonly ServerHost _server;
    private readonly System.Windows.Controls.Image _image = new() { Width = 420, Height = 420, Margin = new Thickness(0, 12, 0, 12) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _code;

    public ConnectionQRWindow(ServerHost server)
    {
        _server = server;
        Title = "iPhoneでQR接続"; Width = 500; Height = 660;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "QRコードでPCに接続", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "iPhoneの main → 1 →「QRコードで接続」で読み取ってください。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        panel.Children.Add(_image); panel.Children.Add(_status);
        var refresh = new System.Windows.Controls.Button { Content = "QRコードを更新", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 12, 0, 0) };
        refresh.Click += (_, _) => RenderQR(); panel.Children.Add(refresh); Content = panel;
        _timer.Tick += (_, _) => { if (_code != _server.Store.PairingCode) RenderQR(); };
        Loaded += (_, _) => { RenderQR(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); _image.Source = null; };
    }

    private void RenderQR()
    {
        try
        {
            var setup = LocalSetupBroker.CreateSetup(_server);
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(setup)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            using var data = QRCodeGenerator.GenerateQrCode("pcfilebridge://pair?data=" + encoded, QRCodeGenerator.ECCLevel.M);
            using var qr = new PngByteQRCode(data);
            using var stream = new MemoryStream(qr.GetGraphic(8));
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            _image.Source = bitmap; _code = _server.Store.PairingCode;
            _status.Text = "有効期限は最大10分です。登録後は自動更新されます。PCとiPhoneでTailscaleをオンにしてください。";
        }
        catch { _image.Source = null; _status.Text = "QRコードを作れません。PCのネットワーク接続を確認して更新してください。"; }
    }
}
