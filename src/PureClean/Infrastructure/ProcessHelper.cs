using System.Diagnostics;
using System.Text;

namespace PureClean.Infrastructure;

public sealed record ProcessOutput(int ExitCode, string StandardOutput, string StandardError);

public static class ProcessHelper
{
    public static bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        foreach (var p in processes)
            p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>Lance un programme sans fenêtre et attend sa fin en récupérant ses sorties.</summary>
    public static ProcessOutput Run(string fileName, IEnumerable<string> arguments, Encoding? outputEncoding = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"Impossible de lancer {fileName}.");

        // Lecture simultanée des deux flux pour éviter tout blocage quand un tampon est plein.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new ProcessOutput(process.ExitCode, stdout.Result, stderr.Result);
    }

    public static ProcessOutput RunPowerShell(string script)
        => Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], Encoding.UTF8);
}
