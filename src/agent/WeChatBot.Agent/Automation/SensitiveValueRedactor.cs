using System.Security.Cryptography;
using System.Text;

namespace WeChatBot.Agent.Automation;

public static class SensitiveValueRedactor
{
    public static string Suppress(string? value) => string.IsNullOrEmpty(value) ? "[empty]" : "[redacted]";

    public static string StructuralFingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "[empty]";
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"[structural:sha256={Convert.ToHexString(bytes)}]";
    }
}

public sealed class EphemeralDiagnosticRedactor : IDisposable
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private int _disposed;

    public string DescribeSensitive(string? value)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrEmpty(value))
        {
            return "[empty]";
        }

        var digest = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value));
        return $"[redacted:hmac-sha256={Convert.ToHexString(digest)}]";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CryptographicOperations.ZeroMemory(_key);
        }
    }
}
