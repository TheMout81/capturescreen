using System.Runtime.InteropServices;
using PureClean.Models;

namespace PureClean.Core.Startup;

/// <summary>
/// Accès au Planificateur de tâches via son API COM (« Schedule.Service »), sans dépendance externe.
/// Seules les tâches hors du dossier \Microsoft sont listées pour éviter de casser Windows.
/// </summary>
internal static class ScheduledTaskReader
{
    private const int TaskEnumHidden = 1;

    public static IReadOnlyList<StartupEntry> GetThirdPartyTasks()
    {
        var result = new List<StartupEntry>();
        try
        {
            dynamic service = CreateService();
            dynamic root = service.GetFolder(@"\");
            Collect(root, result);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // Planificateur indisponible : on n'affiche simplement pas de tâches.
        }
        return result;
    }

    public static void SetEnabled(string taskPath, bool enabled)
    {
        dynamic service = CreateService();
        var separator = taskPath.LastIndexOf('\\');
        var folderPath = separator <= 0 ? @"\" : taskPath[..separator];
        var taskName = taskPath[(separator + 1)..];

        dynamic folder = service.GetFolder(folderPath);
        dynamic task = folder.GetTask(taskName);
        task.Enabled = enabled;
    }

    private static object CreateService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
                   ?? throw new InvalidOperationException("Planificateur de tâches introuvable.");
        dynamic service = Activator.CreateInstance(type)
                          ?? throw new InvalidOperationException("Planificateur de tâches introuvable.");
        service.Connect();
        return service;
    }

    private static void Collect(dynamic folder, List<StartupEntry> result)
    {
        string folderPath = folder.Path;
        if (folderPath.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase))
            return;

        // Les collections du Planificateur sont indexées à partir de 1.
        dynamic tasks = folder.GetTasks(TaskEnumHidden);
        int taskCount = tasks.Count;
        for (var i = 1; i <= taskCount; i++)
        {
            try
            {
                dynamic task = tasks[i];
                string name = task.Name;
                string path = task.Path;
                bool enabled = task.Enabled;
                result.Add(new StartupEntry(name, DescribeActions(task), StartupSource.ScheduledTask, enabled, path));
            }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // Tâche illisible (droits, définition corrompue) : ignorée.
            }
        }

        dynamic folders = folder.GetFolders(0);
        int folderCount = folders.Count;
        for (var i = 1; i <= folderCount; i++)
            Collect(folders[i], result);
    }

    private static string DescribeActions(dynamic task)
    {
        const int ExecAction = 0;
        var parts = new List<string>();
        dynamic actions = task.Definition.Actions;
        int count = actions.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic action = actions[i];
            if ((int)action.Type == ExecAction)
                parts.Add($"{action.Path} {action.Arguments}".Trim());
        }
        return parts.Count > 0 ? string.Join(" ; ", parts) : "(action non exécutable)";
    }
}
