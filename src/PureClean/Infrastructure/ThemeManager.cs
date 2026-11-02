using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace PureClean.Infrastructure;

/// <summary>Bascule entre Themes/Light.xaml et Themes/Dark.xaml, y compris la barre de titre.</summary>
public static class ThemeManager
{
    private const int DwmUseImmersiveDarkMode = 20;

    public static bool IsDark { get; private set; }

    public static void Apply(ThemeMode mode)
    {
        IsDark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemUsesDarkTheme());

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var theme = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Resources/Themes/{(IsDark ? "Dark" : "Light")}.xaml"),
        };

        // Le dictionnaire de couleurs est toujours le premier (voir App.xaml).
        if (dictionaries.Count > 0)
            dictionaries[0] = theme;
        else
            dictionaries.Add(theme);

        foreach (Window window in Application.Current.Windows)
            ApplyTitleBar(window);
    }

    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        var value = IsDark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
    }

    private static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
