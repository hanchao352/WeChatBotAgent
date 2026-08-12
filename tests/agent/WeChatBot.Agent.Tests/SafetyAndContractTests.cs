using WeChatBot.Agent.Automation;
using WeChatBot.Agent.Configuration;
using WeChatBot.Agent.Contracts;
using WeChatBot.Agent.Execution;
using WeChatBot.Agent.Runtime;

namespace WeChatBot.Agent.Tests;

public sealed class SafetyAndContractTests
{
    [Fact]
    public void DryRunDefaultsToTrue()
    {
        var options = AgentOptions.Parse([], static _ => null);

        Assert.True(options.DryRun);
        Assert.Equal(AgentRunMode.SelfCheck, options.Mode);
        Assert.Null(options.ControlPlaneApiKey);
    }

    [Fact]
    public void CommandLineCannotEnableLiveMutation()
    {
        var error = Assert.Throws<ArgumentException>(
            () => AgentOptions.Parse(["--dry-run=false"], static _ => null));

        Assert.Contains("requires dry-run=true", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HeartbeatEndpointRequiresControlPlaneApiKey()
    {
        var error = Assert.Throws<ArgumentException>(() => AgentOptions.Parse(
            ["--heartbeat-uri=https://control.example/api/agents/heartbeat"],
            static _ => null));

        Assert.Contains("control-plane API key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ControlPlaneApiKeyCanBeReadFromCommandLineWithoutBeingPrinted()
    {
        const string secret = "command-line-test-secret";
        var options = AgentOptions.Parse(
            [
                "--heartbeat-uri=https://control.example/api/agents/heartbeat",
                $"--control-plane-api-key={secret}"
            ],
            static _ => null);

        Assert.NotNull(options.ControlPlaneApiKey);
        Assert.True(options.ControlPlaneApiKey.Matches(secret));
        Assert.Equal("[redacted]", options.ControlPlaneApiKey.ToString());
        Assert.DoesNotContain(secret, options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ControlPlaneApiKeyCanBeReadFromEnvironment()
    {
        const string secret = "environment-test-secret";
        var options = AgentOptions.Parse(
            ["--heartbeat-uri=https://control.example/api/agents/heartbeat"],
            variable => variable == "WECHATBOT_AGENT_CONTROL_PLANE_API_KEY" ? secret : null);

        Assert.NotNull(options.ControlPlaneApiKey);
        Assert.True(options.ControlPlaneApiKey.Matches(secret));
    }

    [Fact]
    public void RedactorNeverReturnsSensitiveValue()
    {
        const string secret = "Alice private group";

        var redacted = SensitiveValueRedactor.Suppress(secret);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Equal("[redacted]", redacted);
    }

    [Fact]
    public void DiagnosticHmacIsStableOnlyWithinOneCapture()
    {
        const string sensitiveName = "Alice";
        using var firstCapture = new EphemeralDiagnosticRedactor();
        using var secondCapture = new EphemeralDiagnosticRedactor();

        var first = firstCapture.DescribeSensitive(sensitiveName);
        var repeated = firstCapture.DescribeSensitive(sensitiveName);
        var otherCapture = secondCapture.DescribeSensitive(sensitiveName);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, otherCapture);
        Assert.DoesNotContain(sensitiveName, first, StringComparison.Ordinal);
        Assert.DoesNotContain("length", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StructuralFingerprintUsesFullSha256()
    {
        var fingerprint = SensitiveValueRedactor.StructuralFingerprint("stable-automation-id");

        const string prefix = "[structural:sha256=";
        Assert.StartsWith(prefix, fingerprint, StringComparison.Ordinal);
        Assert.Equal(64, fingerprint.Length - prefix.Length - 1);
    }

    [Fact]
    public void StrictUiProfileRequiresExplicitAutomationIds()
    {
        var profile = UiCompatibilityProfile.StrictDefault(["4.1.11"], []);

        Assert.Empty(profile.RequiredAutomationIdFingerprints);
        Assert.Single(profile.SupportedVersionPrefixes);
    }

    [Fact]
    public void ProcessDetectorIgnoresProcessesOutsideTheConfiguredWindowsSession()
    {
        var impossibleSessionId = int.MaxValue;
        var detector = new WeChatProcessDetector(impossibleSessionId);

        Assert.Empty(detector.DetectMainProcesses());
    }

    [Fact]
    public void VersionPrefixMatchesOnlyAtComponentBoundary()
    {
        var profile = UiCompatibilityProfile.StrictDefault(["4.0.5"], ["fingerprint-1", "fingerprint-2"]);

        Assert.True(profile.SupportsVersion("4.0.5"));
        Assert.True(profile.SupportsVersion("4.0.5.12"));
        Assert.False(profile.SupportsVersion("4.0.50"));
    }

    [Fact]
    public void CompleteUiProbeHasHardDeadline()
    {
        var detector = new BlockingProcessDetector(TimeSpan.FromSeconds(2));
        var profile = UiCompatibilityProfile.StrictDefault(
            ["4.0.5"],
            ["fingerprint-1", "fingerprint-2"]);
        var probe = new WeChatUiProbe(detector, profile);
        var started = DateTime.UtcNow;

        var result = probe.Probe(TimeSpan.FromMilliseconds(100));

        Assert.Equal(UiRecognitionStatus.AutomationFailure, result.Status);
        Assert.Equal("UIA_PROBE_TIMEOUT", result.Code);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task UnknownUiPausesRuntimeAndSkipsHandler()
    {
        var runtime = new AgentRuntimeState();
        var probe = new StubUiProbe(new UiProbeResult(
            UiRecognitionStatus.UnknownSurface,
            "WECHAT_SURFACE_UNKNOWN",
            "unknown",
            null,
            null,
            DateTimeOffset.UtcNow));
        var gate = new FlaUiSafetyGate(probe, runtime, TimeProvider.System, TimeSpan.FromSeconds(1));
        var handler = new DryRunCommandHandler(gate, dryRun: true);
        await using var executor = new SerializedCommandExecutor(
            [handler],
            new InMemoryIdempotencyStore(),
            runtime,
            "wechat-test");
        executor.Start();

        var result = await executor.EnqueueAsync(CreateRemark());

        Assert.Equal(CommandResultStatus.Paused, result.Status);
        Assert.Equal(AgentOperatingState.PausedUnknownUi, runtime.Snapshot().State);
        Assert.Equal("WECHAT_SURFACE_UNKNOWN", runtime.Snapshot().ReasonCode);
    }

    [Fact]
    public void UnknownUiPauseRequiresExplicitVerifiedResume()
    {
        var runtime = new AgentRuntimeState();
        runtime.PauseForUnknownUi("UNKNOWN", "unknown surface", DateTimeOffset.UtcNow);

        var implicitResume = runtime.TryMarkHealthy("probe passed", DateTimeOffset.UtcNow);
        var explicitResume = runtime.ResumeAfterVerifiedSelfCheck("operator verified compatibility", DateTimeOffset.UtcNow);

        Assert.False(implicitResume);
        Assert.True(explicitResume);
        Assert.Equal(AgentOperatingState.Healthy, runtime.Snapshot().State);
    }

    [Fact]
    public void LowerPriorityPauseCannotOverwriteEmergencyStop()
    {
        var runtime = new AgentRuntimeState();
        runtime.PauseByOperator("emergency stop", DateTimeOffset.UtcNow);

        runtime.PauseForControlPlane("heartbeat unavailable", DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Equal(AgentOperatingState.PausedOperator, runtime.Snapshot().State);
        Assert.Equal("OPERATOR_PAUSE", runtime.Snapshot().ReasonCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void OperatorPauseCannotResumeWithoutAcceptedClearedControlPlaneDecision(
        bool accepted,
        bool emergencyStop)
    {
        var runtime = new AgentRuntimeState();
        runtime.PauseByOperator("emergency stop", DateTimeOffset.UtcNow);

        var resumed = runtime.ResumeAfterControlPlaneDecision(
            accepted,
            emergencyStop,
            "control plane decision",
            DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.False(resumed);
        Assert.Equal(AgentOperatingState.PausedOperator, runtime.Snapshot().State);
    }

    [Fact]
    public void AcceptedClearedControlPlaneDecisionResumesOperatorPause()
    {
        var runtime = new AgentRuntimeState();
        runtime.PauseByOperator("emergency stop", DateTimeOffset.UtcNow);

        var resumed = runtime.ResumeAfterControlPlaneDecision(
            accepted: true,
            emergencyStop: false,
            "emergency stop cleared",
            DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.True(resumed);
        Assert.Equal(AgentOperatingState.Healthy, runtime.Snapshot().State);
    }

    [Fact]
    public void EmergencyStopCannotEraseUnknownUiRecoveryRequirement()
    {
        var runtime = new AgentRuntimeState();
        runtime.PauseForUnknownUi("UNKNOWN", "unknown surface", DateTimeOffset.UtcNow);
        runtime.PauseByOperator("emergency stop", DateTimeOffset.UtcNow.AddSeconds(1));

        var resumed = runtime.ResumeAfterControlPlaneDecision(
            accepted: true,
            emergencyStop: false,
            "emergency stop cleared",
            DateTimeOffset.UtcNow.AddSeconds(2));

        Assert.False(resumed);
        Assert.Equal(AgentOperatingState.PausedUnknownUi, runtime.Snapshot().State);
        Assert.Equal("UNKNOWN_UI_RECHECK_REQUIRED", runtime.Snapshot().ReasonCode);
        Assert.True(runtime.ResumeAfterVerifiedSelfCheck(
            "controlled self-check passed",
            DateTimeOffset.UtcNow.AddSeconds(3)));
        Assert.Equal(AgentOperatingState.Healthy, runtime.Snapshot().State);
    }

    [Fact]
    public async Task LiveRemarkMutationIsAlwaysRejected()
    {
        var runtime = new AgentRuntimeState();
        var probe = new StubUiProbe(new UiProbeResult(
            UiRecognitionStatus.CompatibleMainWindow,
            "WECHAT_UI_COMPATIBLE",
            "compatible",
            null,
            null,
            DateTimeOffset.UtcNow));
        var handler = new DryRunCommandHandler(
            new FlaUiSafetyGate(probe, runtime, TimeProvider.System, TimeSpan.FromSeconds(1)),
            dryRun: false);
        await using var executor = new SerializedCommandExecutor(
            [handler],
            new InMemoryIdempotencyStore(),
            runtime,
            "wechat-test",
            dryRun: false);
        executor.Start();

        var result = await executor.EnqueueAsync(CreateRemark());

        Assert.Equal(CommandResultStatus.Rejected, result.Status);
        Assert.Equal("LIVE_MUTATION_NOT_IMPLEMENTED", result.Code);
        Assert.False(result.ExternalActionAttempted);
    }

    [Fact]
    public void RemarkContractRejectsControlCharacters()
    {
        var command = CreateRemark() with { DesiredRemark = "bad\rremark" };

        var errors = AgentCommandValidator.Validate(command, DateTimeOffset.UtcNow);

        Assert.Contains(errors, error => error.Contains("control characters", StringComparison.Ordinal));
    }

    private static UpdateRemarkCommand CreateRemark()
    {
        var now = DateTimeOffset.UtcNow;
        return new UpdateRemarkCommand(
            new CommandMetadata(
                "remark-command",
                "remark-key",
                "wechat-test",
                now,
                now.AddMinutes(1),
                TimeSpan.FromSeconds(2),
                "trace-remark"),
            RemarkTargetKind.Contact,
            "contact-stable-id",
            "expected name",
            "old remark",
            "new remark");
    }

    private sealed class StubUiProbe(UiProbeResult result) : IWeChatUiProbe
    {
        public UiProbeResult Probe(TimeSpan timeout) => result;
    }

    private sealed class BlockingProcessDetector(TimeSpan delay) : IWeChatProcessDetector
    {
        public IReadOnlyList<WeChatProcessDescriptor> DetectMainProcesses()
        {
            Thread.Sleep(delay);
            return [];
        }
    }
}
