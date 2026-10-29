using System.Windows;
using System.Windows.Threading;

namespace PureClean;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Une erreur inattendue s'est produite :\n\n" + e.Exception.Message,
            "PureClean", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
