using System.Diagnostics;

namespace PureClean.Infrastructure;

/// <summary>Crée un point de restauration système via PowerShell (Checkpoint-Computer).</summary>
public static class RestorePoint
{
    public static async Task<(bool Success, string Message)> CreateAsync(string description)
    {
        var script =
            "$ErrorActionPreference='Stop'; " +
            $"Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType MODIFY_SETTINGS -WarningAction Stop";

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Impossible de lancer PowerShell.");
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode == 0)
            return (true, "Point de restauration créé.");

        // Cas fréquents : protection système désactivée, ou un point déjà créé il y a moins de 24 h.
        var detail = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return (false, "Le point de restauration n'a pas pu être créé" +
                       (string.IsNullOrEmpty(detail) ? "." : " : " + detail) +
                       "\n\nVérifiez que la protection du système est activée sur le lecteur C:. " +
                       "Windows limite aussi la création à un point toutes les 24 h.");
    }
}
