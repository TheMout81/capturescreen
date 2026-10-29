using Microsoft.Win32;
using PureClean.Models;

namespace PureClean.Core.Startup;

/// <summary>
/// Liste et active/désactive les programmes au démarrage.
/// La désactivation passe par les clés « StartupApproved », exactement comme le
/// Gestionnaire des tâches de Windows : rien n'est supprimé, tout est réversible.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static IReadOnlyList<StartupEntry> GetEntries()
    {
        var entries = new List<StartupEntry>();

        entries.AddRange(ReadRunKey(RegistryHive.CurrentUser, RunKey, StartupSource.RegistryCurrentUser));
        entries.AddRange(ReadRunKey(RegistryHive.LocalMachine, RunKey, StartupSource.RegistryLocalMachine));
        entries.AddRange(ReadRunKey(RegistryHive.LocalMachine, Run32Key, StartupSource.RegistryLocalMachine32));
        entries.AddRange(ReadStartupFolder(Environment.SpecialFolder.Startup, StartupSource.StartupFolderUser));
        entries.AddRange(ReadStartupFolder(Environment.SpecialFolder.CommonStartup, StartupSource.StartupFolderCommon));
        entries.AddRange(ScheduledTaskReader.GetThirdPartyTasks());

        return entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static void SetEnabled(StartupEntry entry, bool enabled)
    {
        if (entry.Source == StartupSource.ScheduledTask)
        {
            ScheduledTaskReader.SetEnabled(entry.Key, enabled);
            return;
        }

        var (hive, approvedKey) = ApprovedLocation(entry.Source);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(approvedKey, writable: true);

        // Format : 4 octets d'état (02 = activé, 03 = désactivé) + FILETIME de la désactivation.
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled)
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);

        key.SetValue(entry.Key, data, RegistryValueKind.Binary);
    }

    private static IEnumerable<StartupEntry> ReadRunKey(RegistryHive hive, string path, StartupSource source)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(path);
        if (key is null)
            yield break;

        foreach (var valueName in key.GetValueNames().Where(n => !string.IsNullOrEmpty(n)))
        {
            var command = key.GetValue(valueName, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
            yield return new StartupEntry(valueName, command, source, IsApproved(source, valueName), valueName);
        }
    }

    private static IEnumerable<StartupEntry> ReadStartupFolder(Environment.SpecialFolder folder, StartupSource source)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            yield break;

        string[] files;
        try
        {
            files = Directory.GetFiles(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                continue;

            yield return new StartupEntry(Path.GetFileNameWithoutExtension(file), file, source, IsApproved(source, fileName), fileName);
        }
    }

    private static bool IsApproved(StartupSource source, string valueName)
    {
        var (hive, approvedKey) = ApprovedLocation(source);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(approvedKey);

        // Pas d'entrée = jamais désactivé. Octet de poids faible impair = désactivé.
        return key?.GetValue(valueName) is not byte[] { Length: > 0 } data || (data[0] & 0x01) == 0;
    }

    private static (RegistryHive Hive, string Key) ApprovedLocation(StartupSource source) => source switch
    {
        StartupSource.RegistryCurrentUser => (RegistryHive.CurrentUser, ApprovedRoot + @"\Run"),
        StartupSource.RegistryLocalMachine => (RegistryHive.LocalMachine, ApprovedRoot + @"\Run"),
        StartupSource.RegistryLocalMachine32 => (RegistryHive.LocalMachine, ApprovedRoot + @"\Run32"),
        StartupSource.StartupFolderUser => (RegistryHive.CurrentUser, ApprovedRoot + @"\StartupFolder"),
        StartupSource.StartupFolderCommon => (RegistryHive.LocalMachine, ApprovedRoot + @"\StartupFolder"),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };
}
