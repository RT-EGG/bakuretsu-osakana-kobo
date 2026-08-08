using System.IO;
using System.Windows;

namespace BakuretsuOsakanaKobo.Phase3UiMock;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--validate", StringComparison.OrdinalIgnoreCase))
        {
            var reportPath = e.Args.Length > 1 ? e.Args[1] : null;

            try
            {
                UiMockValidation.Run(reportPath);
                Shutdown(0);
            }
            catch (Exception exception)
            {
                if (!string.IsNullOrWhiteSpace(reportPath))
                {
                    File.WriteAllText(reportPath, $"Validation failed: {exception}");
                }

                Shutdown(1);
            }

            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
