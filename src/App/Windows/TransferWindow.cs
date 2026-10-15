using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using Microsoft.Win32;

namespace IPhoneMirror.App.Windows;

internal sealed class TransferWindow : Window
{
    private readonly ComboBox _addresses = new() { MinWidth = 160 };
    private readonly TextBox _link = new() { IsReadOnly = true };
    private readonly TextBox _send = TextArea();
    private readonly TextBox _received = TextArea();
    private readonly TextBlock _code = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _files = new() { Height = 100, DisplayMemberPath = "Name" };
    private readonly CheckBox _automatic = new();
    private readonly Button _start;
    private LanTransferService? _service;
    private bool _closing;
    private bool _autoCopy = true;
    private readonly ClipboardSyncState _textSync;
    private Task? _stop;
    private static string L(string key) => LocalizationService.Get(key);

    internal TransferWindow(Window? owner)
    {
        Owner = owner;
        _textSync = new ClipboardSyncState(text => Clipboard.SetDataObject(text, true), GetClipboardSequenceNumber,
            (_, source) => !_closing && _autoCopy && ReferenceEquals(_service, source),
            (_, _, _) => { });
        _textSync.SelectDevice("lan-transfer");
        Title = L("TransferTitle");
        Width = 690; Height = 810; MinWidth = 480; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        var panel = new StackPanel { Margin = new Thickness(22) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = L("TransferHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        foreach (var ip in LanTransferService.GetAddresses()) _addresses.Items.Add(ip);
        if (_addresses.Items.Count > 0) _addresses.SelectedIndex = 0;
        var connect = new WrapPanel(); connect.Children.Add(_addresses);
        _start = AddButton(connect, "TransferStart", OnStart);
        panel.Children.Add(connect);
        panel.Children.Add(_link); panel.Children.Add(_code); panel.Children.Add(_status);
        var linkButtons = new WrapPanel();
        AddButton(linkButtons, "TransferCopyLink", (_, _) =>
        {
            if (_service is { Address.Length: > 0 } host) Clipboard.SetDataObject(host.Address + "/#" + host.PairCode, true);
        });
        AddButton(linkButtons, "TransferOpenPage", (_, _) =>
        {
            if (_service is { Address.Length: > 0 } host)
                Process.Start(new ProcessStartInfo(host.Address + "/#" + host.PairCode) { UseShellExecute = true });
        });
        panel.Children.Add(linkButtons);
        Header(panel, "TransferSendText"); panel.Children.Add(_send);
        var textButtons = new WrapPanel();
        AddButton(textButtons, "TransferReadClipboard", (_, _) => _send.Text = Clipboard.GetText());
        AddButton(textButtons, "TransferShareText", (_, _) =>
        {
            if (_service is null) { _status.Text = L("TransferStartFirst"); return; }
            _service.PublishText(_send.Text); _status.Text = L("TransferRefreshHint");
        });
        panel.Children.Add(textButtons);
        var sendFiles = new WrapPanel();
        AddButton(sendFiles, "TransferChooseFiles", OnChooseFiles);
        AddButton(sendFiles, "TransferClipboardImage", OnShareImage);
        panel.Children.Add(sendFiles);
        Header(panel, "TransferReceivedText");
        _received.IsReadOnly = true;
        panel.Children.Add(_received);
        _automatic.Content = L("TransferAutoCopy"); _automatic.IsChecked = true;
        _automatic.Checked += (_, _) => _autoCopy = true;
        _automatic.Unchecked += (_, _) => _autoCopy = false;
        panel.Children.Add(_automatic);
        var receivedButtons = new WrapPanel();
        AddButton(receivedButtons, "TransferCopyReceived", (_, _) => { if (_received.Text.Length > 0) Clipboard.SetDataObject(_received.Text, true); });
        AddButton(receivedButtons, "TransferCopyImage", OnCopyImage);
        AddButton(receivedButtons, "TransferOpenFolder", (_, _) =>
        {
            if (_service is { } host) Process.Start(new ProcessStartInfo(host.ReceivedDirectory) { UseShellExecute = true });
        });
        panel.Children.Add(receivedButtons); panel.Children.Add(_files);
        Closing += (_, _) => { _closing = true; _textSync.Stop(); };
        Closed += async (_, _) =>
        {
            await StopAsync();
        };
    }

    internal Task StopAsync()
    {
        _closing = true;
        _textSync.Stop();
        return _stop ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        try { if (_service is { } host) await host.DisposeAsync(); }
        catch (Exception error) { DiagnosticLogger.Exception("transfer", "shutdown_failed", error); }
    }

    private static TextBox TextArea() => new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 88, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static void Header(Panel panel, string key) => panel.Children.Add(new TextBlock {
        Text = L(key), FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8) });
    private Button AddButton(Panel panel, string key, RoutedEventHandler handler)
    {
        var button = new Button { Content = L(key), Margin = new Thickness(0, 8, 8, 8), Padding = new Thickness(12, 7, 12, 7) };
        button.Click += (sender, args) =>
        {
            try { handler(sender, args); }
            catch (Exception error) { _status.Text = L("TransferError") + " " + error.Message; }
        };
        panel.Children.Add(button); return button;
    }

    private async void OnStart(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        if (_addresses.SelectedItem is not IPAddress ip) { _status.Text = L("TransferNoNetwork"); return; }
        _start.IsEnabled = false;
        try
        {
            if (_service is { } old) await old.DisposeAsync();
            if (_closing) return;
            var host = new LanTransferService();
            _service = host;
            _files.Items.Clear(); _received.Clear();
            host.TextReceived += text =>
            {
                var sequence = _textSync.CaptureSequence();
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    if (_closing || !ReferenceEquals(_service, host)) return;
                    _received.Text = text;
                    if (_autoCopy)
                    {
                        _textSync.Observe("lan-transfer", host, text, sequence);
                        await _textSync.FlushAsync();
                    }
                    _status.Text = L("TransferTextReceived");
                });
            };
            host.FileReceived += file => _ = Dispatcher.InvokeAsync(() =>
            {
                if (_closing || !ReferenceEquals(_service, host)) return;
                _files.Items.Add(file); _files.SelectedItem = file; _status.Text = L("TransferFileReceived") + " " + file.Name;
            });
            await host.StartAsync(ip);
            if (_closing) { await host.DisposeAsync(); return; }
            _link.Text = host.Address;
            _code.Text = L("TransferPairCode") + " " + host.PairCode;
            _status.Text = L("TransferReady");
        }
        catch (Exception error) { _status.Text = L("TransferError") + " " + error.Message; }
        finally { _start.IsEnabled = true; }
    }

    private async void OnChooseFiles(object sender, RoutedEventArgs args)
    {
        if (_service is not { } host) { _status.Text = L("TransferStartFirst"); return; }
        var picker = new OpenFileDialog { Multiselect = true, CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            foreach (var path in picker.FileNames) await host.OfferFileAsync(path);
            _status.Text = L("TransferRefreshHint");
        }
        catch (Exception error) { _status.Text = L("TransferError") + " " + error.Message; }
    }

    private async void OnShareImage(object sender, RoutedEventArgs args)
    {
        if (_service is not { } host) { _status.Text = L("TransferStartFirst"); return; }
        var temporary = Path.Combine(Path.GetTempPath(), "iPhoneMirror-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var bitmap = Clipboard.GetImage();
            if (bitmap is null) { _status.Text = L("TransferNoImage"); return; }
            if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 32000000) throw new IOException(L("TransferImageTooLarge"));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(temporary)) encoder.Save(output);
            await host.OfferFileAsync(temporary, "clipboard.png");
            _status.Text = L("TransferRefreshHint");
        }
        catch (Exception error) { _status.Text = L("TransferError") + " " + error.Message; }
        finally { File.Delete(temporary); }
    }

    private void OnCopyImage(object sender, RoutedEventArgs args)
    {
        if (_files.SelectedItem is not LanTransferService.SharedFile file) return;
        var extension = Path.GetExtension(file.Name).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff"))
        { _status.Text = L("TransferNoImage"); return; }
        var image = LoadImageForClipboard(file.Path, file.Size);
        var data = new DataObject(); data.SetImage(image); Clipboard.SetDataObject(data, true);
    }

    internal static BitmapSource LoadImageForClipboard(string path, long size)
    {
        if (size > 64L * 1024 * 1024) throw new IOException(L("TransferImageTooLarge"));
        using var source = File.OpenRead(path);
        var metadata = BitmapDecoder.Create(source, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        if ((long)metadata.PixelWidth * metadata.PixelHeight > 32000000) throw new IOException(L("TransferImageTooLarge"));
        var scale = Math.Min(1.0, 4096.0 / Math.Max(metadata.PixelWidth, metadata.PixelHeight));
        source.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = Math.Max(1, (int)(metadata.PixelWidth * scale));
        image.DecodePixelHeight = Math.Max(1, (int)(metadata.PixelHeight * scale));
        image.StreamSource = source; image.EndInit(); image.Freeze();
        return image;
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
