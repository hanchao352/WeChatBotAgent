using System.Security.Cryptography;
using System.Text;

namespace WeChatBot.Agent.Runtime;

public static class WeChatInstanceIdentity
{
    public static string ToStorageKey(string weChatInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(weChatInstanceId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(weChatInstanceId));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }
}
