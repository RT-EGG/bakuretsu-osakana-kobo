using System.Windows.Threading;
using BakuretsuOsakanaKobo.Infrastructure.Errors;

namespace BakuretsuOsakanaKobo;

internal sealed class MainWindowNotificationSink : IUserNotificationSink
{
    private readonly MainWindow _window;

    internal MainWindowNotificationSink(MainWindow window)
    {
        _window = window;
    }

    public void Show(UserNotification notification)
    {
        if (_window.Dispatcher.HasShutdownStarted || _window.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_window.Dispatcher.CheckAccess())
        {
            _window.ShowNotification(notification);
            return;
        }

        _window.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            () => _window.ShowNotification(notification));
    }
}
