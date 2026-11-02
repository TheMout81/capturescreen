using System.Security;
using System.Security.Principal;
using System.Text;

namespace PureClean.Infrastructure;

/// <summary>Crée ou supprime la tâche planifiée Windows qui lance le nettoyage automatique.</summary>
public static class ScheduledCleaning
{
    public const string TaskName = @"PureClean\Nettoyage automatique";

    private static string SchTasks => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    public static bool Exists() => ProcessHelper.Run(SchTasks, ["/Query", "/TN", TaskName]).ExitCode == 0;

    public static void Apply(ScheduleSettings schedule, string executablePath, string arguments)
    {
        if (!schedule.Enabled)
        {
            if (Exists())
                Check(ProcessHelper.Run(SchTasks, ["/Delete", "/TN", TaskName, "/F"]));
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var xml = BuildTaskXml(schedule, executablePath, arguments, identity.User?.Value ?? identity.Name);
        var file = Path.Combine(Path.GetTempPath(), $"PureClean-task-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks exige un fichier XML en UTF-16.
            File.WriteAllText(file, xml, Encoding.Unicode);
            Check(ProcessHelper.Run(SchTasks, ["/Create", "/TN", TaskName, "/XML", file, "/F"]));
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static void RunNow() => Check(ProcessHelper.Run(SchTasks, ["/Run", "/TN", TaskName]));

    /// <summary>Définition de la tâche, dans le format exporté par le Planificateur de tâches.</summary>
    public static string BuildTaskXml(ScheduleSettings schedule, string executablePath, string arguments, string userId)
    {
        var start = $"2024-01-01T{schedule.Hour:00}:00:00";
        var calendar = schedule.Frequency switch
        {
            ScheduleFrequency.Daily => "<ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>",
            ScheduleFrequency.Monthly =>
                "<ScheduleByMonth><DaysOfMonth><Day>1</Day></DaysOfMonth><Months>" +
                "<January /><February /><March /><April /><May /><June /><July /><August /><September /><October /><November /><December />" +
                "</Months></ScheduleByMonth>",
            _ => $"<ScheduleByWeek><DaysOfWeek><{schedule.Day} /></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek>",
        };

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>PureClean</Author>
                <Description>Nettoyage automatique des fichiers temporaires et caches par PureClean.</Description>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>{start}</StartBoundary>
                  <Enabled>true</Enabled>
                  {calendar}
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(userId)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{SecurityElement.Escape(executablePath)}"</Command>
                  <Arguments>{SecurityElement.Escape(arguments)}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static void Check(ProcessOutput output)
    {
        if (output.ExitCode != 0)
            throw new InvalidOperationException(
                (output.StandardError.Trim() is { Length: > 0 } error ? error : output.StandardOutput.Trim()) is { Length: > 0 } message
                    ? message
                    : $"schtasks a échoué (code {output.ExitCode}).");
    }
}
