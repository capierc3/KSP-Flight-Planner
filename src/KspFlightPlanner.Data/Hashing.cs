using System.Security.Cryptography;
using System.Text;

namespace KspFlightPlanner.Data;

internal static class Hashing
{
    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    public static string Sha256(Stream stream)
    {
        using (stream) return Convert.ToHexString(SHA256.HashData(stream))[..16];
    }
}
