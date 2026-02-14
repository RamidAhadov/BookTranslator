using System.Security.Cryptography;
using System.Text;

namespace BookTranslator.Utils;

public static class CacheKeyBuilder
{
    public static string Build(string input)
    {
        using SHA256 sha = SHA256.Create();
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));

        string shortHash = Convert.ToHexString(bytes[..16]).ToLowerInvariant();

        return $"pdftr-{shortHash}";
    }
}