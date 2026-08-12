using WeChatBot.Agent.Automation;
using WeChatBot.Agent.Contracts;

namespace WeChatBot.Agent.Execution;

public sealed class DryRunCommandHandler(IUiSafetyGate safetyGate, bool dryRun) : IAgentCommandHandler
{
    public bool CanHandle(IAgentCommand command) =>
        command is ObserveMentionsCommand or UpdateRemarkCommand;

    public async ValueTask<CommandPreflightResult> PreflightAsync(
        IAgentCommand command,
        CancellationToken cancellationToken)
    {
        var safety = await safetyGate.VerifyAsync(cancellationToken).ConfigureAwait(false);
        return safety.Allowed
            ? CommandPreflightResult.Allow(safety.Code, safety.Summary)
            : CommandPreflightResult.Reject(safety.Code, safety.Summary);
    }

    public ValueTask<CommandHandlerResult> ExecuteAsync(
        IAgentCommand command,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return command switch
        {
            ObserveMentionsCommand mentions => ValueTask.FromResult(ObserveMentions(mentions)),
            UpdateRemarkCommand remark => ValueTask.FromResult(PreviewRemark(remark)),
            _ => ValueTask.FromResult(new CommandHandlerResult(
                CommandResultStatus.Rejected,
                "COMMAND_UNSUPPORTED",
                "This dry-run handler does not support the command."))
        };
    }

    private CommandHandlerResult ObserveMentions(ObserveMentionsCommand command)
    {
        // The version-specific message reader is intentionally not guessed from an unknown UI tree.
        return new CommandHandlerResult(
            CommandResultStatus.DryRun,
            "MENTION_OBSERVE_DRY_RUN",
            "Mention observation contract validated; no UI navigation or message capture was performed.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["group"] = SensitiveValueRedactor.Suppress(command.GroupStableId),
                ["observationCount"] = "0",
                ["dryRun"] = "true"
            });
    }

    private CommandHandlerResult PreviewRemark(UpdateRemarkCommand command)
    {
        if (!dryRun)
        {
            return new CommandHandlerResult(
                CommandResultStatus.Rejected,
                "LIVE_MUTATION_NOT_IMPLEMENTED",
                "This build never changes WeChat contacts or groups; enable a separately reviewed adapter first.");
        }

        return new CommandHandlerResult(
            CommandResultStatus.DryRun,
            "REMARK_PREVIEW_READY",
            "Remark command validated and previewed; WeChat was not changed.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["targetKind"] = command.TargetKind.ToString(),
                ["target"] = SensitiveValueRedactor.Suppress(command.TargetStableId),
                ["expectedCurrentRemark"] = SensitiveValueRedactor.Suppress(command.ExpectedCurrentRemark),
                ["desiredRemark"] = SensitiveValueRedactor.Suppress(command.DesiredRemark),
                ["dryRun"] = "true"
            });
    }
}
