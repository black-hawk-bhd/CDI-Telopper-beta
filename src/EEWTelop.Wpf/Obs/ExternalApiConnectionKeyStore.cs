using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EEWTelop.Wpf.Obs;

internal sealed class ExternalApiConnectionKeyStore(string path)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CDI-Telopper/ExternalApi/Production/v1");

    public string? Load()
    {
        byte[] encrypted;
        try
        {
            encrypted = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        if (encrypted.Length == 0) throw new CryptographicException("The external API key file is empty.");
        byte[] key = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            if (key.Length != 32) throw new CryptographicException("Invalid external API key length.");
            return Convert.ToHexString(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void Save(string token)
    {
        byte[] key = Convert.FromHexString(token);
        byte[] encrypted;
        try
        {
            encrypted = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException) { }
    }
}
