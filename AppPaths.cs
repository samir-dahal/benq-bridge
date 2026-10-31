namespace BenqBridge;

internal static class AppPaths
{
    /// <summary>
    /// Per-user data folder. The token lives here rather than next to the exe, so it survives
    /// a read-only install folder, an update and a re-extracted zip.
    /// </summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BenqBridge");
}
