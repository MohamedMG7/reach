using Microsoft.Win32;
using Reach.Agent.Tray;

namespace Reach.Agent.Tests;

public sealed class StartupRegistrationTests : IDisposable
{
    private readonly string _keyPath = @"Software\ReachTests\" + Guid.NewGuid().ToString("N");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);

    [Fact]
    public void TogglesTheRunValue()
    {
        var startup = new StartupRegistration(_keyPath);
        Assert.False(startup.IsEnabled);

        startup.Enable(@"C:\Program Files\Reach\Reach.Agent.exe");

        Assert.True(startup.IsEnabled);
        using (var key = Registry.CurrentUser.OpenSubKey(_keyPath)!)
            Assert.Equal("\"C:\\Program Files\\Reach\\Reach.Agent.exe\"", key.GetValue("Reach"));

        startup.Disable();
        startup.Disable();

        Assert.False(startup.IsEnabled);
    }
}
