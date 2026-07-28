using System.Security.Cryptography;
using System.Text;

namespace SelectAndRead;

/// <summary>
/// The OpenAI API key, stored separately from config.json and encrypted with DPAPI.
///
/// Deliberately not a Config property: config.json is plain text that the user is expected
/// to be able to open and edit, and a bearer token that grants spend on their account does
/// not belong in it. DPAPI at CurrentUser scope ties the ciphertext to the Windows account,
/// so copying the file to another machine or another user yields nothing.
///
/// This is obfuscation against casual disclosure, not a secret store: any code running as
/// the user can call Unprotect just as this does. That is the same guarantee the browsers'
/// saved-password stores give, and it is the right level for a tray utility.
/// </summary>
internal static class ApiKeyStore
{
    private static readonly byte[] Entropy = "SelectAndRead.ApiKey.v1"u8.ToArray();

    internal static string FilePath => Path.Combine(Config.Directory, "apikey.dat");

    /// <summary>Never throws: a missing, corrupt or foreign-user file reads as absent.</summary>
    internal static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            var plaintext = ProtectedData.Unprotect(
                File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);

            var key = Encoding.UTF8.GetString(plaintext).Trim();
            return key.Length == 0 ? null : key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Saves the key, or deletes the file when given null or blank.</summary>
    internal static void Save(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            Delete();
            return;
        }

        System.IO.Directory.CreateDirectory(Config.Directory);

        var ciphertext = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);

        // Same atomic write as Config.Save: a crash mid-write must not leave a truncated
        // file, which here would decrypt-fail and silently disable the cloud engine.
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, ciphertext);

        if (File.Exists(FilePath))
            File.Replace(temp, FilePath, destinationBackupFileName: null);
        else
            File.Move(temp, FilePath);
    }

    private static void Delete()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving a stale key behind is not worth failing the whole settings save.
        }
    }
}
