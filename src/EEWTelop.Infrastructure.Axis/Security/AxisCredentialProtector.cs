using System.Security.Cryptography;
using System.Text;

namespace EEWTelop.Infrastructure.Axis.Security;

public static class AxisCredentialProtector
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("QTelopper/AXIS/JWT/v1");

    public static string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        byte[] clear = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(
                clear,
                Entropy,
                DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public static string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return string.Empty;
        }

        try
        {
            byte[] encrypted = Convert.FromBase64String(protectedValue);
            byte[] clear = ProtectedData.Unprotect(
                encrypted,
                Entropy,
                DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(clear);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return string.Empty;
        }
    }
}
