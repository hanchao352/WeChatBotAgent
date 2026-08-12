using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using WeChatBot.Agent.Runtime;

namespace WeChatBot.Agent.Automation;

public enum UiRecognitionStatus
{
    CompatibleMainWindow,
    NoProcess,
    AmbiguousMainProcess,
    MainWindowUnavailable,
    UnsupportedVersion,
    UnknownSurface,
    AutomationFailure
}

public sealed record UiCompatibilityProfile(
    IReadOnlyList<string> SupportedVersionPrefixes,
    IReadOnlySet<string> MainWindowTitles,
    IReadOnlyList<string> RequiredAutomationIdFingerprints)
{
    public static UiCompatibilityProfile StrictDefault(
        IReadOnlyList<string> supportedVersionPrefixes,
        IReadOnlyList<string> requiredAutomationIdFingerprints) =>
        new(
            supportedVersionPrefixes,
            new HashSet<string>(StringComparer.Ordinal) { "WeChat", "Weixin", "\u5fae\u4fe1" },
            requiredAutomationIdFingerprints);

    public bool SupportsVersion(string? productVersion)
    {
        if (string.IsNullOrWhiteSpace(productVersion))
        {
            return false;
        }

        return SupportedVersionPrefixes.Any(prefix =>
            productVersion.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || productVersion.StartsWith($"{prefix}.", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record UiProbeResult(
    UiRecognitionStatus Status,
    string Code,
    string Summary,
    WeChatProcessDescriptor? Process,
    string? WindowTitleFingerprint,
    DateTimeOffset ProbedAt)
{
    public bool IsSafe => Status == UiRecognitionStatus.CompatibleMainWindow;
}

public interface IWeChatUiProbe
{
    UiProbeResult Probe(TimeSpan timeout);
}

public sealed class WeChatUiProbe(
    IWeChatProcessDetector processDetector,
    UiCompatibilityProfile profile) : IWeChatUiProbe
{
    public UiProbeResult Probe(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var startedAt = DateTimeOffset.UtcNow;
        var completion = new TaskCompletionSource<UiProbeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var probeThread = new Thread(() => completion.TrySetResult(ProbeCore(timeout)))
        {
            IsBackground = true,
            Name = "WeChatBot bounded UIA probe"
        };
        probeThread.SetApartmentState(ApartmentState.MTA);
        probeThread.Start();

        if (completion.Task.Wait(timeout))
        {
            return completion.Task.GetAwaiter().GetResult();
        }

        return new UiProbeResult(
            UiRecognitionStatus.AutomationFailure,
            "UIA_PROBE_TIMEOUT",
            "The complete FlaUI probe exceeded its hard deadline; the UI surface is unsafe.",
            null,
            null,
            startedAt);
    }

    private UiProbeResult ProbeCore(TimeSpan timeout)
    {
        var probedAt = DateTimeOffset.UtcNow;
        var processes = processDetector.DetectMainProcesses();
        if (processes.Count == 0)
        {
            return Result(UiRecognitionStatus.NoProcess, "WECHAT_PROCESS_NOT_FOUND", "No WeChat main process was found.");
        }

        if (processes.Count > 1)
        {
            return Result(
                UiRecognitionStatus.AmbiguousMainProcess,
                "WECHAT_PROCESS_AMBIGUOUS",
                "More than one WeChat main process was found; instance identity cannot be proven.");
        }

        var process = processes[0];
        if (profile.SupportedVersionPrefixes.Count == 0
            || !profile.SupportsVersion(process.ProductVersion))
        {
            return Result(
                UiRecognitionStatus.UnsupportedVersion,
                "WECHAT_VERSION_UNSUPPORTED",
                "The detected WeChat version is not in the tested compatibility allow-list.",
                process);
        }

        try
        {
            using var application = Application.Attach(process.ProcessId);
            using var automation = new UIA3Automation();
            var window = application.GetMainWindow(automation, timeout);
            if (window is null)
            {
                return Result(
                    UiRecognitionStatus.MainWindowUnavailable,
                    "WECHAT_MAIN_WINDOW_NOT_FOUND",
                    "FlaUI could not obtain the WeChat main window.",
                    process);
            }

            if (window.IsOffscreen || !window.IsEnabled)
            {
                return Result(
                    UiRecognitionStatus.UnknownSurface,
                    "WECHAT_MAIN_WINDOW_INACTIVE",
                    "The WeChat main window is hidden or disabled.",
                    process);
            }

            if (!NativeWindowSafety.IsForeground(process.MainWindowHandle))
            {
                return Result(
                    UiRecognitionStatus.UnknownSurface,
                    "WECHAT_MAIN_WINDOW_NOT_FOREGROUND",
                    "The WeChat main window is not the active foreground window.",
                    process);
            }

            var hasVisibleSecondaryWindow = application
                .GetAllTopLevelWindows(automation)
                .Any(candidate =>
                    !candidate.IsOffscreen
                    && candidate.IsEnabled
                    && candidate.Properties.NativeWindowHandle.ValueOrDefault != process.MainWindowHandle);
            if (hasVisibleSecondaryWindow)
            {
                return Result(
                    UiRecognitionStatus.UnknownSurface,
                    "WECHAT_SECONDARY_WINDOW_VISIBLE",
                    "Another visible WeChat top-level window may be modal or covering the approved surface.",
                    process);
            }

            var title = window.Title;
            var titleFingerprint = SensitiveValueRedactor.Suppress(title);
            if (!profile.MainWindowTitles.Contains(title))
            {
                return Result(
                    UiRecognitionStatus.UnknownSurface,
                    "WECHAT_SURFACE_UNKNOWN",
                    "The active top-level surface does not match an approved main-window title.",
                    process,
                    titleFingerprint);
            }

            if (!HasRequiredStructure(window, profile.RequiredAutomationIdFingerprints))
            {
                return Result(
                    UiRecognitionStatus.UnknownSurface,
                    "WECHAT_UI_SIGNATURE_MISMATCH",
                    "Required UI Automation controls are missing from the tested signature.",
                    process,
                    titleFingerprint);
            }

            return Result(
                UiRecognitionStatus.CompatibleMainWindow,
                "WECHAT_UI_COMPATIBLE",
                "The WeChat version and main-window signature match the configured compatibility profile.",
                process,
                titleFingerprint);
        }
        catch (Exception exception)
        {
            return Result(
                UiRecognitionStatus.AutomationFailure,
                "UIA_PROBE_FAILED",
                $"FlaUI failed to inspect the main window ({exception.GetType().Name}).",
                process);
        }

        UiProbeResult Result(
            UiRecognitionStatus status,
            string code,
            string summary,
            WeChatProcessDescriptor? detectedProcess = null,
            string? titleFingerprint = null) =>
            new(status, code, summary, detectedProcess, titleFingerprint, probedAt);
    }

    private static bool HasRequiredStructure(
        Window window,
        IReadOnlyList<string> requiredAutomationIdFingerprints)
    {
        if (requiredAutomationIdFingerprints.Count < 2)
        {
            return false;
        }

        var descendants = window.FindAllDescendants();
        var presentFingerprints = descendants
            .Where(static element => !element.IsOffscreen && element.IsEnabled)
            .Select(element => element.AutomationId)
            .Where(static automationId => !string.IsNullOrWhiteSpace(automationId))
            .Select(SensitiveValueRedactor.StructuralFingerprint)
            .ToHashSet(StringComparer.Ordinal);
        return requiredAutomationIdFingerprints.All(presentFingerprints.Contains);
    }
}

public sealed record UiSafetyDecision(bool Allowed, string Code, string Summary, UiProbeResult Probe);

public interface IUiSafetyGate
{
    ValueTask<UiSafetyDecision> VerifyAsync(CancellationToken cancellationToken);
}

public sealed class FlaUiSafetyGate(
    IWeChatUiProbe probe,
    AgentRuntimeState runtimeState,
    TimeProvider timeProvider,
    TimeSpan probeTimeout) : IUiSafetyGate
{
    public ValueTask<UiSafetyDecision> VerifyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = probe.Probe(probeTimeout);
        cancellationToken.ThrowIfCancellationRequested();

        if (result.IsSafe)
        {
            var accepted = runtimeState.TryMarkHealthy(
                "A tested WeChat UI signature is active.",
                timeProvider.GetUtcNow());
            return accepted
                ? ValueTask.FromResult(new UiSafetyDecision(true, result.Code, result.Summary, result))
                : ValueTask.FromResult(new UiSafetyDecision(
                    false,
                    "RUNTIME_RESUME_REQUIRED",
                    "The UI is compatible, but the existing pause requires an explicit verified resume.",
                    result));
        }

        runtimeState.PauseForUnknownUi(result.Code, result.Summary, timeProvider.GetUtcNow());
        return ValueTask.FromResult(new UiSafetyDecision(false, result.Code, result.Summary, result));
    }
}
