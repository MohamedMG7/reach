using Microsoft.Win32;

namespace Reach.Agent.Tray;

/// <summary>"Start with Windows": a value under HKCU\...\Run (spec §5). Tests use another key.</summary>
public sealed class StartupRegistration(string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", string valueName = "Reach")
{
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is string;
        }
    }

    public void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(valueName, $"\"{exePath}\"");
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
