namespace WeChatBot.Agent.Configuration;

public sealed class SecretValue
{
    private readonly string _value;

    internal SecretValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Secret value cannot be empty.", nameof(value));
        }

        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Secret value contains invalid header characters.", nameof(value));
        }

        _value = value;
    }

    internal string Reveal() => _value;

    internal bool Matches(string candidate) =>
        string.Equals(_value, candidate, StringComparison.Ordinal);

    public override string ToString() => "[redacted]";
}

public enum AgentRunMode
{
    SelfCheck,
    Diagnose,
    Run,
    Help
}

public sealed record AgentOptions(
    AgentRunMode Mode,
    bool DryRun,
    string AgentId,
    string WeChatInstanceId,
    string StateDirectory,
    Uri? HeartbeatUri,
    SecretValue? ControlPlaneApiKey,
    IReadOnlyList<string> SupportedVersionPrefixes,
    IReadOnlyList<string> RequiredAutomationIdFingerprints,
    TimeSpan UiProbeTimeout,
    TimeSpan HeartbeatInterval)
{
    public static AgentOptions Parse(string[] args) =>
        Parse(args, Environment.GetEnvironmentVariable);

    internal static AgentOptions Parse(
        string[] args,
        Func<string, string?> environmentVariableReader)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environmentVariableReader);

        var values = args
            .Where(static argument => argument.StartsWith("--", StringComparison.Ordinal))
            .Select(static argument => argument[2..].Split('=', 2))
            .ToDictionary(
                static pair => pair[0],
                static pair => pair.Length == 1 ? "true" : pair[1],
                StringComparer.OrdinalIgnoreCase);

        var mode = values.ContainsKey("help")
            ? AgentRunMode.Help
            : values.ContainsKey("diagnose")
                ? AgentRunMode.Diagnose
                : values.ContainsKey("run")
                    ? AgentRunMode.Run
                    : AgentRunMode.SelfCheck;
        var dryRun = ReadBoolean(
            values,
            "dry-run",
            "WECHATBOT_AGENT_DRY_RUN",
            true,
            environmentVariableReader);
        if (!dryRun)
        {
            throw new ArgumentException("This build requires dry-run=true; live mutations are not available.");
        }

        var agentId = Read(values, "agent-id", "WECHATBOT_AGENT_ID", environmentVariableReader)
            ?? $"agent-{Environment.MachineName}";
        var instanceId = Read(values, "instance-id", "WECHATBOT_AGENT_INSTANCE_ID", environmentVariableReader)
            ?? "wechat-primary";
        var stateDirectory = Read(
                values,
                "state-directory",
                "WECHATBOT_AGENT_STATE_DIRECTORY",
                environmentVariableReader)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WeChatBot", "Agent");
        var heartbeatValue = Read(
            values,
            "heartbeat-uri",
            "WECHATBOT_AGENT_HEARTBEAT_URI",
            environmentVariableReader);
        var heartbeatUri = heartbeatValue is null ? null : new Uri(heartbeatValue, UriKind.Absolute);
        if (heartbeatUri is not null
            && heartbeatUri.Scheme != Uri.UriSchemeHttps
            && !heartbeatUri.IsLoopback)
        {
            throw new ArgumentException("Heartbeat URI must use HTTPS unless it targets loopback.");
        }

        var apiKeyValue = Read(
            values,
            "control-plane-api-key",
            "WECHATBOT_AGENT_CONTROL_PLANE_API_KEY",
            environmentVariableReader);
        var controlPlaneApiKey = string.IsNullOrWhiteSpace(apiKeyValue)
            ? null
            : new SecretValue(apiKeyValue);
        if (heartbeatUri is not null && controlPlaneApiKey is null)
        {
            throw new ArgumentException(
                "A control-plane API key is required when a heartbeat URI is configured.");
        }

        var versionValues = Read(
            values,
            "supported-version-prefixes",
            "WECHATBOT_AGENT_SUPPORTED_VERSION_PREFIXES",
            environmentVariableReader);
        var versions = versionValues?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();
        var automationIdFingerprintValues = Read(
            values,
            "required-automation-id-fingerprints",
            "WECHATBOT_AGENT_REQUIRED_AUTOMATION_ID_FINGERPRINTS",
            environmentVariableReader);
        var automationIdFingerprints = automationIdFingerprintValues?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray()
            ?? Array.Empty<string>();

        return new AgentOptions(
            mode,
            dryRun,
            agentId,
            instanceId,
            Path.GetFullPath(stateDirectory),
            heartbeatUri,
            controlPlaneApiKey,
            versions,
            automationIdFingerprints,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(15));
    }

    private static string? Read(
        IReadOnlyDictionary<string, string> values,
        string argument,
        string environmentVariable,
        Func<string, string?> environmentVariableReader) =>
        values.TryGetValue(argument, out var value)
            ? value
            : environmentVariableReader(environmentVariable);

    private static bool ReadBoolean(
        IReadOnlyDictionary<string, string> values,
        string argument,
        string environmentVariable,
        bool defaultValue,
        Func<string, string?> environmentVariableReader)
    {
        var value = Read(values, argument, environmentVariable, environmentVariableReader);
        return value is null ? defaultValue : bool.Parse(value);
    }
}
