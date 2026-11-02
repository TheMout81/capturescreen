using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PureClean.Core;
using PureClean.Infrastructure;

namespace PureClean;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Lancé par le Planificateur de tâches : nettoyage sans fenêtre, puis fermeture.
        if (e.Args.Contains(AutoClean.Argument, StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Task.Run(() => AutoClean.Run(AppSettings.Current))
                .ContinueWith(_ => Dispatcher.BeginInvoke(() => Shutdown()));
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        ThemeManager.Apply(AppSettings.Current.Theme);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        base.OnExit(e);
    }

    /// <summary>Suit le passage clair/sombre de Windows quand le thème « Selon Windows » est choisi.</summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && AppSettings.Current.Theme == ThemeMode.System)
            Dispatcher.BeginInvoke(() => ThemeManager.Apply(ThemeMode.System));
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Une erreur inattendue s'est produite :\n\n" + e.Exception.Message,
            AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
