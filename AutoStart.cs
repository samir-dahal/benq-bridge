using Microsoft.Win32;

namespace BenqBridge;

/// <summary>
/// "Start with Windows" through the per-user Run key: no admin rights, visible in Task Manager's
/// Startup tab, and the app starts inside the user's desktop session, which monitor control needs.
/// </summary>
internal static class AutoStart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BenQ Bridge";

    /// <summary>
    /// False when running as `dotnet BenqBridge.dll`, where the process is dotnet.exe and a Run
    /// entry would start the wrong program.
    /// </summary>
    public static bool IsAvailable =>
        string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "BenqBridge",
            StringComparison.OrdinalIgnoreCase);

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static bool Enable()
    {
        if (!IsAvailable) return false;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
