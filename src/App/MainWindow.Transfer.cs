using System.Windows;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private TransferWindow? _transferWindow;

    private void OnTransferClick(object sender, RoutedEventArgs args)
    {
        if (_transferWindow is null)
        {
            _transferWindow = new TransferWindow();
            _transferWindow.Closed += (_, _) => _transferWindow = null;
            _transferWindow.Show();
        }
        if (_transferWindow.WindowState == WindowState.Minimized)
            _transferWindow.WindowState = WindowState.Normal;
        _transferWindow.Activate();
    }
}
