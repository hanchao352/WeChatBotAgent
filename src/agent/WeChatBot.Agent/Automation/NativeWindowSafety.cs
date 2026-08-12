using System.Runtime.InteropServices;

namespace WeChatBot.Agent.Automation;

internal static class NativeWindowSafety
{
    public static bool IsForeground(long expectedWindowHandle) =>
        expectedWindowHandle != 0 && GetForegroundWindow().ToInt64() == expectedWindowHandle;

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetForegroundWindow();
}
