using System.Runtime.InteropServices;

namespace BenqBridge;

internal static class NativeConsole
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    /// <summary>
    /// The exe has no console of its own, so double-clicking it shows no window. Started from a
    /// terminal, this lets it print its banner there. Harmless when there is no parent console.
    /// </summary>
    public static void TryAttach()
    {
        try
        {
            AttachConsole(AttachParentProcess);
        }
        catch
        {
            // best effort only
        }
    }
}
