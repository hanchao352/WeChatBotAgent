using System.Diagnostics;

namespace WeChatBot.Agent.Automation;

public sealed record WeChatProcessDescriptor(
    int ProcessId,
    string ProcessName,
    string? ProductVersion,
    long MainWindowHandle,
    DateTimeOffset DetectedAt);

public interface IWeChatProcessDetector
{
    IReadOnlyList<WeChatProcessDescriptor> DetectMainProcesses();
}

public sealed class WeChatProcessDetector : IWeChatProcessDetector
{
    private static readonly HashSet<string> SupportedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WeChat",
        "Weixin"
    };

    public IReadOnlyList<WeChatProcessDescriptor> DetectMainProcesses()
    {
        var detectedAt = DateTimeOffset.UtcNow;
        var results = new List<WeChatProcessDescriptor>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!SupportedProcessNames.Contains(process.ProcessName) || process.MainWindowHandle == nint.Zero)
                    {
                        continue;
                    }

                    string? version = null;
                    try
                    {
                        version = process.MainModule?.FileVersionInfo.ProductVersion;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // A privilege mismatch can hide version metadata. Window detection remains useful.
                    }

                    results.Add(new WeChatProcessDescriptor(
                        process.Id,
                        process.ProcessName,
                        version,
                        process.MainWindowHandle.ToInt64(),
                        detectedAt));
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The process may exit while the process table is being inspected.
                }
            }
        }

        return results.OrderBy(static process => process.ProcessId).ToArray();
    }
}
