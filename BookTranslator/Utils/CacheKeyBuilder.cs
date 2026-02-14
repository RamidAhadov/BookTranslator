using System.Security.Cryptography;
using System.Text;

namespace BookTranslator.Utils;

public static class CacheKeyBuilder
{
    public static string Build(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));

        var shortHash = Convert.ToHexString(bytes[..16]).ToLowerInvariant();

        return $"pdftr-{shortHash}";
    }
}