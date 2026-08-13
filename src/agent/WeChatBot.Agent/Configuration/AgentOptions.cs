namespace WeChatBot.Agent.Configuration;

/// <summary>封装敏感配置值，默认字符串表示始终脱敏，仅允许受信任的内部 HTTP 客户端显式读取。</summary>
public sealed class SecretValue
{
    /// <summary>保存进程内明文；该字段不得参与日志、序列化或异常消息。</summary>
    private readonly string _value;

    /// <summary>校验并保存非空且可安全放入 HTTP 请求头的秘密。</summary>
    /// <param name="value">由参数或环境变量注入的秘密值。</param>
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

    /// <summary>向受信任的传输层返回明文，不得把返回值写入日志。</summary>
    /// <returns>原始秘密值。</returns>
    internal string Reveal() => _value;

    /// <summary>仅供测试或内部校验比较候选值，不改变秘密状态。</summary>
    /// <param name="candidate">待比较的候选明文。</param>
    /// <returns>候选值与保存值完全一致时为真。</returns>
    internal bool Matches(string candidate) =>
        string.Equals(_value, candidate, StringComparison.Ordinal);

    /// <summary>返回固定脱敏文本，避免记录配置对象时泄露明文。</summary>
    /// <returns>固定的脱敏占位符。</returns>
    public override string ToString() => "[redacted]";
}

/// <summary>定义 Agent 启动后执行的互斥运行模式。</summary>
public enum AgentRunMode
{
    /// <summary>只执行环境与 UI 兼容性自检。</summary>
    SelfCheck,

    /// <summary>输出已脱敏的 UI Automation 诊断树。</summary>
    Diagnose,

    /// <summary>启动心跳、租约轮询和串行命令执行宿主。</summary>
    Run,

    /// <summary>输出命令行帮助后退出。</summary>
    Help
}

/// <summary>
/// 保存经校验的 Agent 启动配置，包括设备绑定、控制面地址、独立凭据和安全时间参数。
/// </summary>
/// <param name="Mode">本次进程运行模式。</param>
/// <param name="DryRun">是否强制只演练；当前构建只允许真。</param>
/// <param name="AgentId">管理员预注册的稳定 Agent 标识。</param>
/// <param name="WeChatInstanceId">与注册记录固定绑定的微信实例标识。</param>
/// <param name="StateDirectory">幂等日志和实例状态的绝对目录。</param>
/// <param name="HeartbeatUri">可选心跳接口地址。</param>
/// <param name="RemarkTaskLeaseUri">可选备注任务租约接口根地址。</param>
/// <param name="AgentCredential">与单个 AgentRegistration 绑定的敏感凭据。</param>
/// <param name="SupportedVersionPrefixes">经批准的微信版本前缀。</param>
/// <param name="RequiredAutomationIdFingerprints">经批准的 UI 自动化标识指纹。</param>
/// <param name="UiProbeTimeout">单次 UI 探测最长时间。</param>
/// <param name="HeartbeatInterval">正常心跳发送间隔。</param>
public sealed record AgentOptions(
    AgentRunMode Mode,
    bool DryRun,
    string AgentId,
    string WeChatInstanceId,
    string StateDirectory,
    Uri? HeartbeatUri,
    Uri? RemarkTaskLeaseUri,
    SecretValue? AgentCredential,
    IReadOnlyList<string> SupportedVersionPrefixes,
    IReadOnlyList<string> RequiredAutomationIdFingerprints,
    TimeSpan UiProbeTimeout,
    TimeSpan HeartbeatInterval)
{
    /// <summary>
    /// 旧版控制面 API Key 属性别名；仅为迁移现有调用方，实际值来自每 Agent 独立凭据。
    /// </summary>
    [Obsolete("请改用 AgentCredential；该别名将在后续版本移除。")]
    public SecretValue? ControlPlaneApiKey => AgentCredential;

    /// <summary>使用当前进程环境变量解析命令行配置。</summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>完成安全校验的不可变配置。</returns>
    public static AgentOptions Parse(string[] args) =>
        Parse(args, Environment.GetEnvironmentVariable);

    /// <summary>使用可替换的环境变量读取器解析配置，供生产入口和确定性测试共用。</summary>
    /// <param name="args">命令行参数。</param>
    /// <param name="environmentVariableReader">按名称读取环境变量的函数。</param>
    /// <returns>完成安全校验的不可变配置。</returns>
    internal static AgentOptions Parse(
        string[] args,
        Func<string, string?> environmentVariableReader)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environmentVariableReader);

        // 参数只接受 --name 或 --name=value 形式；同名参数立即失败，避免覆盖顺序造成凭据歧义。
        var values = args
            .Where(static argument => argument.StartsWith("--", StringComparison.Ordinal))
            .Select(static argument => argument[2..].Split('=', 2))
            .ToDictionary(
                static pair => pair[0],
                static pair => pair.Length == 1 ? "true" : pair[1],
                StringComparer.OrdinalIgnoreCase);

        // 模式按帮助、诊断、运行、自检的优先级解析，确保显式诊断不会误启动控制面宿主。
        var mode = values.ContainsKey("help")
            ? AgentRunMode.Help
            : values.ContainsKey("diagnose")
                ? AgentRunMode.Diagnose
                : values.ContainsKey("run")
                    ? AgentRunMode.Run
                    : AgentRunMode.SelfCheck;
        // 当前 UIA 写入链路未达到兼容性门禁，任何关闭 dry-run 的配置都必须在启动前失败。
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

        // 设备身份允许开发期安全默认值；生产部署必须通过预注册值显式覆盖。
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
        // 所有控制面地址统一执行 HTTPS/回环校验，租约轮询依赖心跳建立的健康状态。
        var heartbeatValue = Read(
            values,
            "heartbeat-uri",
            "WECHATBOT_AGENT_HEARTBEAT_URI",
            environmentVariableReader);
        var heartbeatUri = heartbeatValue is null ? null : new Uri(heartbeatValue, UriKind.Absolute);
        ValidateControlPlaneUri(heartbeatUri, "Heartbeat URI");
        var remarkTaskLeaseValue = Read(
            values,
            "remark-task-lease-uri",
            "WECHATBOT_AGENT_REMARK_TASK_LEASE_URI",
            environmentVariableReader);
        var remarkTaskLeaseUri = remarkTaskLeaseValue is null
            ? null
            : new Uri(remarkTaskLeaseValue, UriKind.Absolute);
        ValidateControlPlaneUri(remarkTaskLeaseUri, "Remark-task lease URI");
        if (remarkTaskLeaseUri is not null && heartbeatUri is null)
        {
            throw new ArgumentException(
                "A heartbeat URI is required when remark-task lease polling is configured.");
        }

        // 新名称明确表达该秘密与单个 AgentRegistration 绑定；旧名称只作为迁移回退且不覆盖新配置。
        var agentCredentialValue = Read(
            values,
            "agent-credential",
            "WECHATBOT_AGENT_CREDENTIAL",
            environmentVariableReader);
        var legacyApiKeyValue = Read(
            values,
            "control-plane-api-key",
            "WECHATBOT_AGENT_CONTROL_PLANE_API_KEY",
            environmentVariableReader);
        var credentialValue = string.IsNullOrWhiteSpace(agentCredentialValue)
            ? legacyApiKeyValue
            : agentCredentialValue;
        var agentCredential = string.IsNullOrWhiteSpace(credentialValue)
            ? null
            : new SecretValue(credentialValue);
        if ((heartbeatUri is not null || remarkTaskLeaseUri is not null) && agentCredential is null)
        {
            throw new ArgumentException(
                "An Agent credential is required when a heartbeat URI is configured.");
        }

        // 逗号列表在解析时去空白并去重，使 UI 兼容性判断不受重复配置影响。
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

        // 时间参数集中由解析器提供安全默认值，避免运行路径散落不可追踪的间隔字面量。
        return new AgentOptions(
            mode,
            dryRun,
            agentId,
            instanceId,
            Path.GetFullPath(stateDirectory),
            heartbeatUri,
            remarkTaskLeaseUri,
            agentCredential,
            versions,
            automationIdFingerprints,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// 验证控制面地址仅使用 HTTPS，回环地址允许 HTTP 以支持本机开发。
    /// </summary>
    /// <param name="uri">待验证地址；空值表示未配置。</param>
    /// <param name="optionName">用于错误提示的配置名称。</param>
    private static void ValidateControlPlaneUri(Uri? uri, string optionName)
    {
        if (uri is not null && uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
        {
            throw new ArgumentException($"{optionName} must use HTTPS unless it targets loopback.");
        }
    }

    /// <summary>按“命令行优先、环境变量回退”读取单项配置。</summary>
    /// <param name="values">已解析的命令行参数字典。</param>
    /// <param name="argument">不含双横线的参数名。</param>
    /// <param name="environmentVariable">回退环境变量名。</param>
    /// <param name="environmentVariableReader">环境变量读取函数。</param>
    /// <returns>配置值；两处都未提供时为空。</returns>
    private static string? Read(
        IReadOnlyDictionary<string, string> values,
        string argument,
        string environmentVariable,
        Func<string, string?> environmentVariableReader) =>
        values.TryGetValue(argument, out var value)
            ? value
            : environmentVariableReader(environmentVariable);

    /// <summary>读取并严格解析布尔配置，缺失时使用调用方给定的安全默认值。</summary>
    /// <param name="values">已解析的命令行参数字典。</param>
    /// <param name="argument">不含双横线的参数名。</param>
    /// <param name="environmentVariable">回退环境变量名。</param>
    /// <param name="defaultValue">命令行和环境变量均缺失时的值。</param>
    /// <param name="environmentVariableReader">环境变量读取函数。</param>
    /// <returns>解析后的布尔值。</returns>
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
