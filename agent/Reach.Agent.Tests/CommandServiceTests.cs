using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Reach.Agent.Commands;
using Reach.Protocol;

namespace Reach.Agent.Tests;

public sealed class CommandServiceTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string CommandsFile => _dir.File("commands.json");

    private CommandService Create(TimeSpan? timeout = null) =>
        new(CommandsFile, timeout ?? CommandService.DefaultTimeout, NullLogger<CommandService>.Instance);

    private void WriteCommands(params object[] commands) =>
        File.WriteAllText(CommandsFile, JsonSerializer.Serialize(commands));

    [Fact]
    public void SeedsTheSpecDefaultsOnFirstRun()
    {
        using var service = Create();

        Assert.True(File.Exists(CommandsFile));
        Assert.Equal(
            ["sleep", "lock", "mute", "vol-up", "vol-down", "shutdown-30", "shutdown-cancel"],
            service.List().Select(c => c.Id));
        Assert.Equal(new CommandInfo("sleep", "Sleep", "moon", true), service.List()[0]);
    }

    [Fact]
    public void KeepsAnExistingFile()
    {
        WriteCommands(new { id = "hello", label = "Hello", icon = "hand", confirm = false, run = "exit 0" });
        using var service = Create();
        Assert.Equal([new CommandInfo("hello", "Hello", "hand", false)], service.List());
    }

    [Fact]
    public void FillsInAMissingIcon()
    {
        WriteCommands(new { id = "x", label = "X", run = "exit 0" });
        using var service = Create();
        Assert.Equal(CommandService.DefaultIcon, service.List().Single().Icon);
    }

    [Theory]
    [InlineData("exit 0", true, 0)]
    [InlineData("exit 3", false, 3)]
    [InlineData("cmd /c exit 5", false, 5)]
    [InlineData("throw 'boom'", false, 1)]
    [InlineData("Get-Item C:\\does-not-exist-reach", false, 1)]
    public async Task ReturnsTheScriptsExitCode(string script, bool ok, int exitCode)
    {
        WriteCommands(new { id = "t", label = "T", icon = "x", confirm = false, run = script });
        using var service = Create();

        var result = await service.RunAsync("t", default);

        Assert.Equal(ok, result.Ok);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(ok ? null : $"exit code {exitCode}", result.Error);
    }

    [Fact]
    public async Task KillsAScriptThatRunsPastTheTimeout()
    {
        WriteCommands(new { id = "slow", label = "Slow", icon = "x", confirm = false, run = "Start-Sleep -Seconds 30" });
        using var service = Create(TimeSpan.FromSeconds(2));

        var started = DateTime.UtcNow;
        var result = await service.RunAsync("slow", default);

        Assert.Equal(new CmdResult("slow", false, -1, "timed out"), result);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task AnUnknownIdFailsWithoutRunningAnything()
    {
        using var service = Create();
        Assert.Equal(new CmdResult("nope", false, -1, "unknown command"), await service.RunAsync("nope", default));
    }

    [Fact]
    public async Task ReloadsWhenTheFileChanges()
    {
        using var service = Create();
        var reloaded = new TaskCompletionSource();
        service.Reloaded += () => reloaded.TrySetResult();

        WriteCommands(new { id = "new", label = "New", icon = "x", confirm = false, run = "exit 0" });

        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("new", service.List().Single().Id);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"id\":\"\",\"label\":\"x\",\"run\":\"exit 0\"}]")]
    [InlineData("[{\"id\":\"a\",\"label\":\"x\",\"run\":\"exit 0\"},{\"id\":\"a\",\"label\":\"y\",\"run\":\"exit 0\"}]")]
    public async Task AnInvalidEditKeepsTheLastValidListAndReportsIt(string content)
    {
        using var service = Create();
        var error = new TaskCompletionSource<string>();
        service.ConfigError += e => error.TrySetResult(e);

        File.WriteAllText(CommandsFile, content);

        Assert.NotEmpty(await error.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(7, service.List().Count);
    }

    [Fact]
    public void AnInvalidFileAtStartupGivesAnEmptyListInsteadOfCrashing()
    {
        File.WriteAllText(CommandsFile, "not json");
        using var service = Create();
        Assert.Empty(service.List());
    }

    [Fact]
    public async Task AnInvalidFileAtStartupIsKeptForTheTrayToReport()
    {
        File.WriteAllText(CommandsFile, "not json");
        using var service = Create();

        Assert.NotNull(service.LastError); // TrayApp subscribes after construction, so it reads this

        var reloaded = new TaskCompletionSource();
        service.Reloaded += () => reloaded.TrySetResult();
        WriteCommands(new { id = "x", label = "X", run = "exit 0" });
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task AnUnreadableFileIsReportedInsteadOfCrashing()
    {
        using var service = Create();
        var error = new TaskCompletionSource<string>();
        service.ConfigError += e => error.TrySetResult(e);
        var file = new FileInfo(CommandsFile);
        var acl = file.GetAccessControl();
        var denyRead = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        acl.AddAccessRule(denyRead);
        file.SetAccessControl(acl);
        try
        {
            using (var stream = new FileStream(CommandsFile, FileMode.Append, FileAccess.Write)) stream.WriteByte((byte)' ');

            Assert.NotEmpty(await error.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(7, service.List().Count);
        }
        finally
        {
            acl.RemoveAccessRule(denyRead);
            file.SetAccessControl(acl);
        }
    }
}
