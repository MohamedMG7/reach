using System.ComponentModel;
using System.Diagnostics;

namespace Reach.Agent.Tray;

/// <summary>Inbound TCP rule for the agent's port, Private profile only (spec §3.5).</summary>
public static class FirewallSetup
{
    public const string RuleName = "Reach Agent";

    public static bool RuleExists() => RunNetsh($"advfirewall firewall show rule name=\"{RuleName}\"", elevated: false) == 0;

    /// <summary>Adds the rule through one UAC prompt. False if the user declined or netsh failed.</summary>
    public static bool AddRule(int port)
    {
        try
        {
            return RunNetsh(
                $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} profile=private",
                elevated: true) == 0;
        }
        catch (Win32Exception)
        {
            return false; // UAC prompt cancelled
        }
    }

    private static int RunNetsh(string arguments, bool elevated)
    {
        var start = new ProcessStartInfo("netsh.exe", arguments)
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = elevated,
            Verb = elevated ? "runas" : "",
            RedirectStandardOutput = !elevated,
        };
        using var process = Process.Start(start)!;
        if (!elevated) process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
