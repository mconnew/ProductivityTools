using System.Security.Cryptography;
using System.Text;

namespace WcfPrTriage.Services;

/// <summary>
/// Thin wrapper over Windows DPAPI (CurrentUser scope) used to encrypt the optional GitHub token at
/// rest. Backed by the Microsoft-owned <see cref="ProtectedData"/> managed API, so there is no native
/// interop or manual memory management to get wrong.
/// </summary>
/// <remarks>
/// No optional entropy is used, matching the tool's original implementation, so tokens encrypted by
/// earlier builds decrypt unchanged (DPAPI's data-description string is informational and never part
/// of key derivation).
/// </remarks>
internal static class Dpapi
{
    public static string Protect(string plaintext)
    {
        byte[] cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    public static string Unprotect(string protectedBase64)
    {
        byte[] plain = ProtectedData.Unprotect(
            Convert.FromBase64String(protectedBase64), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }
}
