using System.Runtime.InteropServices;

namespace WeChatBot.Agent.Automation;

public enum SelfCheckSeverity
{
    Information,
    Warning,
    Critical
}

public sealed record SelfCheckFinding(
    string Code,
    SelfCheckSeverity Severity,
    bool Passed,
    string Summary);

public sealed record EnvironmentSelfCheckReport(
    DateTimeOffset CheckedAt,
    bool Ready,
    IReadOnlyList<SelfCheckFinding> Findings,
    UiProbeResult UiProbe);

public interface IDesktopSessionProbe
{
    bool IsWindows { get; }

    bool IsUserInteractive { get; }

    bool IsInputDesktopAvailable();
}

public sealed class WindowsDesktopSessionProbe : IDesktopSessionProbe
{
    private const uint DesktopSwitchDesktop = 0x0100;

    public bool IsWindows => OperatingSystem.IsWindows();

    public bool IsUserInteractive => Environment.UserInteractive;

    public bool IsInputDesktopAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var desktop = OpenInputDesktop(0, false, DesktopSwitchDesktop);
        if (desktop == nint.Zero)
        {
            return false;
        }

        try
        {
            return SwitchDesktop(desktop);
        }
        finally
        {
            _ = CloseDesktop(desktop);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SwitchDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);
}

public sealed class EnvironmentSelfCheck(
    IDesktopSessionProbe desktopSession,
    IWeChatUiProbe uiProbe,
    bool dryRun)
{
    public EnvironmentSelfCheckReport Run(TimeSpan uiTimeout)
    {
        var findings = new List<SelfCheckFinding>();
        Add("OS_WINDOWS", SelfCheckSeverity.Critical, desktopSession.IsWindows, "Agent requires Windows.");
        Add(
            "SESSION_INTERACTIVE",
            SelfCheckSeverity.Critical,
            desktopSession.IsUserInteractive,
            "Agent requires an interactive user session.");
        Add(
            "INPUT_DESKTOP_AVAILABLE",
            SelfCheckSeverity.Critical,
            desktopSession.IsInputDesktopAvailable(),
            "The input desktop must be available and unlocked.");
        Add(
            "DRY_RUN_ENABLED",
            SelfCheckSeverity.Warning,
            dryRun,
            dryRun
                ? "Dry-run is enabled; mutating commands cannot touch WeChat."
                : "Dry-run is disabled, but this build still rejects all mutating commands.");

        var probe = uiProbe.Probe(uiTimeout);
        Add(probe.Code, SelfCheckSeverity.Critical, probe.IsSafe, probe.Summary);

        var ready = findings.All(finding => finding.Severity != SelfCheckSeverity.Critical || finding.Passed);
        return new EnvironmentSelfCheckReport(DateTimeOffset.UtcNow, ready, findings, probe);

        void Add(string code, SelfCheckSeverity severity, bool passed, string summary) =>
            findings.Add(new SelfCheckFinding(code, severity, passed, summary));
    }
}
