using System.Diagnostics;
using System.IO;
using System.Windows;
using BakuretsuOsakanaKobo.Infrastructure.Errors;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;

namespace BakuretsuOsakanaKobo;

public partial class MainWindow : Window
{
    private PortableDataPaths? _paths;
    private ErrorReporter? _errorReporter;

    public MainWindow()
    {
        InitializeComponent();
    }

    internal void ConfigureServices(PortableDataPaths paths, ErrorReporter errorReporter)
    {
        _paths = paths;
        _errorReporter = errorReporter;
    }

    internal void ShowNotification(UserNotification notification)
    {
        NotificationMessageText.Text = notification.Message;
        NotificationActionText.Text = notification.SuggestedAction;
        NotificationBorder.Visibility = Visibility.Visible;
    }

    private void OpenDataFolderMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_paths is null || _errorReporter is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _paths.DataDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _errorReporter.Report(
                new UserNotification(
                    UserNotificationSeverity.Warning,
                    "データフォルダーを開けませんでした。",
                    "アプリの配置先とアクセス権限を確認してください。"),
                "open-data-folder-failed",
                exception.Message,
                exception,
                _paths.DataDirectory);
        }
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e) => Close();

    private void DismissNotificationButton_OnClick(object sender, RoutedEventArgs e) =>
        NotificationBorder.Visibility = Visibility.Collapsed;
}
