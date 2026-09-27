using System.Security.Cryptography;
using Reach.Protocol;

namespace Reach.Agent.Storage;

/// <summary>The PC's static Noise key pair. The secret key is stored encrypted with DPAPI (current user).</summary>
public static class IdentityStore
{
    private const int SecretKeyLength = 32;

    public static KeyPair LoadOrCreate(string path) => LoadOrCreate(path, out _);

    /// <param name="replaced">
    /// True if the stored key could not be used (damaged, or DPAPI data from another Windows user or
    /// install) and was moved aside to identity.key.corrupt: every phone has to pair again.
    /// </param>
    public static KeyPair LoadOrCreate(string path, out bool replaced)
    {
        replaced = false;
        if (File.Exists(path))
        {
            var secret = TryUnprotect(File.ReadAllBytes(path));
            if (secret is { Length: SecretKeyLength }) return KeyPair.FromSecret(secret);
            File.Move(path, path + ".corrupt", overwrite: true);
            replaced = true;
        }
        var pair = KeyPair.Generate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ProtectedData.Protect(pair.SecretKey, null, DataProtectionScope.CurrentUser));
        return pair;
    }

    private static byte[]? TryUnprotect(byte[] data)
    {
        try
        {
            return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
