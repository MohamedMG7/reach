using System.Net;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Reach.Agent.Commands;
using Reach.Agent.Input;
using Reach.Agent.Net;
using Reach.Agent.Pairing;
using Reach.Agent.Storage;
using Reach.Agent.Terminal;
using Reach.Agent.Tray;
using Reach.Protocol;
using Serilog;
using Serilog.Events;

namespace Reach.Agent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\Reach.Agent", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Reach is already running — look for its icon in the notification area.", "Reach");
            return;
        }

        ApplicationConfiguration.Initialize();
        var paths = AgentPaths.Default;
        using var logs = CreateLogging(paths);
        var log = logs.CreateLogger("Reach");
        Application.ThreadException += (_, e) => log.LogError(e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");

        var time = TimeProvider.System;
        KeyPair identity;
        bool identityReplaced;
        DeviceStore devices;
        try
        {
            identity = IdentityStore.LoadOrCreate(paths.IdentityKey, out identityReplaced);
            devices = new DeviceStore(paths.Devices, time);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogCritical(e, "Could not read Reach's data files");
            MessageBox.Show($"Reach can't read its data files in {Path.GetDirectoryName(paths.Devices)}: {e.Message}", "Reach", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (identityReplaced) log.LogWarning("identity.key was unusable; moved aside and replaced");

        using var commands = new CommandService(paths.Commands, CommandService.DefaultTimeout, logs.CreateLogger<CommandService>());
        using var terminal = TerminalService.CreateDefault(time, logs.CreateLogger<TerminalService>());
        var agent = new AgentContext
        {
            Identity = identity,
            Devices = devices,
            Pairing = new PairingService(time),
            Limiter = new RateLimiter(time),
            Input = new InputService(new Win32InputSink()),
            Terminal = terminal,
            Commands = commands,
            Sessions = new SessionManager(),
            Logs = logs,
            Time = time,
        };

        var server = new AgentServer(agent);
        try
        {
            server.StartAsync(IPAddress.Any, AgentServer.DefaultPort).GetAwaiter().GetResult();
        }
        catch (IOException e)
        {
            log.LogCritical(e, "Could not listen on port {Port}", AgentServer.DefaultPort);
            MessageBox.Show($"Reach can't listen on port {AgentServer.DefaultPort}: {e.Message}", "Reach", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        log.LogInformation("Reach agent {Version} listening on port {Port}", agent.AgentVersion, server.Port);

        using var tray = new TrayApp(agent, commands, paths, server.Port);
        if (identityReplaced)
            tray.Notify("Reach's security key was damaged or came from another PC, so it made a new one. Pair your phone again.", ToolTipIcon.Warning);
        EnsureFirewallRule(tray, log);
        Application.Run(tray);

        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        log.LogInformation("Reach agent stopped");
    }

    /// <summary>Asks for elevation once per start, only while the rule is missing (spec §3.5).</summary>
    private static void EnsureFirewallRule(TrayApp tray, Microsoft.Extensions.Logging.ILogger log)
    {
        if (FirewallSetup.RuleExists()) return;
        var answer = MessageBox.Show(
            "Reach needs a Windows Firewall rule so your phone can connect over your home Wi-Fi " +
            "(Private networks only). Windows will ask for permission next.",
            "Reach", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (answer == DialogResult.OK && FirewallSetup.AddRule(AgentServer.DefaultPort))
        {
            log.LogInformation("Firewall rule added");
            return;
        }
        log.LogWarning("Firewall rule not added");
        tray.Notify("No firewall rule — your phone may not be able to connect. Restart Reach to try again.", ToolTipIcon.Warning);
    }

    /// <summary>%APPDATA%\Reach\logs, one file per day, 7 days kept (spec §3.7).</summary>
    private static ILoggerFactory CreateLogging(AgentPaths paths)
    {
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(paths.Logs, "reach-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
        return LoggerFactory.Create(b => b.AddSerilog(serilog, dispose: true));
    }
}
