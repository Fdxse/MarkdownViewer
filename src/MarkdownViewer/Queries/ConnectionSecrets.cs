using System.Security.Cryptography;
using System.Text;

namespace MarkdownViewer.Queries;

/// <summary>
/// Encrypts and decrypts saved connection strings with Windows DPAPI (the same protection Windows uses for
/// saved passwords). Only the same Windows user on the same computer can decrypt them, so the settings file and
/// a Zip all backup never contain a password in readable form. The downside: after moving settings.json to
/// another computer, the connection has to be entered again.
/// </summary>
public static class ConnectionSecrets
{
    /// <summary>
    /// Extra bytes mixed into the encryption, so another program running as the same user cannot simply
    /// ask DPAPI to decrypt our values without knowing them.
    /// </summary>
    private static readonly byte[] AdditionalEntropy = Encoding.UTF8.GetBytes("MarkdownViewer.QueryBookshelf.ConnectionString.v1");

    /// <summary>Encrypts a connection string for the current Windows user and returns it as base64 text.</summary>
    public static string Encrypt(string plainConnectionString)
    {
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainConnectionString);
        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encryptedBytes);
    }

    /// <summary>
    /// Decrypts a value made by <see cref="Encrypt"/>. Returns false (with a reason for the user) if it is
    /// damaged or was encrypted by another Windows user or on another computer.
    /// </summary>
    public static bool TryDecrypt(string encryptedBase64, out string plainConnectionString, out string? problemText)
    {
        plainConnectionString = string.Empty;
        try
        {
            byte[] encryptedBytes = Convert.FromBase64String(encryptedBase64);
            byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
            plainConnectionString = Encoding.UTF8.GetString(plainBytes);
            problemText = null;
            return true;
        }
        catch (Exception decryptError) when (decryptError is CryptographicException or FormatException)
        {
            problemText = "The saved connection could not be decrypted. It was probably saved by another Windows user "
                + "or on another computer (for example from a Zip all backup). Please enter the connection again.";
            return false;
        }
    }
}
