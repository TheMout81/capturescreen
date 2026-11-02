namespace PureClean.Infrastructure;

public static class AppInfo
{
    /// <summary>Version affichée (ex. « v1.0.1 »), pour savoir quel exécutable est lancé.</summary>
    public static string Version { get; } =
        "v" + (typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    public static string Title { get; } = "PureClean " + Version;
}
