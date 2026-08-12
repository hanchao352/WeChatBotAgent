using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace WeChatBot.Agent.Automation;

public sealed record RedactedControlNode(
    int Depth,
    string ControlType,
    string Name,
    string AutomationId,
    string ClassName,
    string FrameworkId,
    bool IsOffscreen,
    string Bounds);

public sealed record RedactedControlTree(
    int ProcessId,
    string ProcessName,
    string? ProductVersion,
    DateTimeOffset CapturedAt,
    bool Truncated,
    IReadOnlyList<RedactedControlNode> Nodes);

public sealed class ControlTreeDiagnostics
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public RedactedControlTree Capture(
        WeChatProcessDescriptor process,
        TimeSpan timeout,
        int maxDepth = 8,
        int maxNodes = 1_000)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNodes, 1);

        using var application = Application.Attach(process.ProcessId);
        using var automation = new UIA3Automation();
        var window = application.GetMainWindow(automation, timeout)
            ?? throw new InvalidOperationException("The WeChat main window is unavailable.");

        var nodes = new List<RedactedControlNode>(Math.Min(maxNodes, 1_000));
        using var redactor = new EphemeralDiagnosticRedactor();
        var pending = new Queue<(AutomationElement Element, int Depth)>();
        pending.Enqueue((window, 0));
        var truncated = false;

        while (pending.TryDequeue(out var current))
        {
            if (nodes.Count >= maxNodes)
            {
                truncated = true;
                break;
            }

            nodes.Add(CreateNode(current.Element, current.Depth, redactor));
            if (current.Depth >= maxDepth)
            {
                if (current.Element.FindAllChildren().Length > 0)
                {
                    truncated = true;
                }

                continue;
            }

            foreach (var child in current.Element.FindAllChildren())
            {
                pending.Enqueue((child, current.Depth + 1));
            }
        }

        return new RedactedControlTree(
            process.ProcessId,
            process.ProcessName,
            process.ProductVersion,
            DateTimeOffset.UtcNow,
            truncated,
            nodes);
    }

    public static string ToJson(RedactedControlTree tree) =>
        JsonSerializer.Serialize(tree, SerializerOptions);

    private static RedactedControlNode CreateNode(
        AutomationElement element,
        int depth,
        EphemeralDiagnosticRedactor redactor)
    {
        var rectangle = element.BoundingRectangle;
        return new RedactedControlNode(
            depth,
            element.ControlType.ToString(),
            redactor.DescribeSensitive(element.Name),
            SensitiveValueRedactor.StructuralFingerprint(element.AutomationId),
            redactor.DescribeSensitive(element.ClassName),
            element.FrameworkType.ToString(),
            element.IsOffscreen,
            FormattableString.Invariant($"{rectangle.X:0},{rectangle.Y:0},{rectangle.Width:0},{rectangle.Height:0}"));
    }
}
