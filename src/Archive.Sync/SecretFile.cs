using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Archive.Sync;

/// <summary>
/// A file holding something that is not a setting but a key to an account.
/// </summary>
/// <remarks>
/// <para>
/// A Telegram session is not a credential you can change without consequence — whoever holds it
/// is signed in as the user and can read and send as them. It is a worse thing to leak than an API
/// key, so it gets more than <c>ai.json</c>'s treatment: never in the save (P7 and the README's
/// promise that a copied save carries no key), and encrypted at rest where the platform gives us
/// something to encrypt it with.
/// </para>
/// <para>
/// On Windows that is DPAPI, scoped to the user account, which is managed code and needs no native
/// dependency. On Linux and macOS the keystores are per-desktop C libraries, and pulling one in
/// would put a native, per-RID binary into a build for a feature most users never switch on — the
/// thing D32 refused for model runtimes. So there the file is written with owner-only permissions
/// and the settings page says plainly that it is not encrypted, rather than implying a protection
/// that is not there.
/// </para>
/// </remarks>
public sealed class SecretFile(string path)
{
    private readonly string _path = !string.IsNullOrWhiteSpace(path)
        ? path
        : throw new ArgumentException("A secret needs a path.", nameof(path));

    /// <summary>Additional data DPAPI ties the ciphertext to, so another app's blob will not decrypt.</summary>
    private static readonly byte[] Entropy = "ahistory/connector"u8.ToArray();

    public string Path => _path;

    public bool Exists => File.Exists(_path);

    /// <summary>Whether the contents are encrypted, as opposed to merely owner-readable.</summary>
    public static bool IsEncryptedAtRest => OperatingSystem.IsWindows();

    public byte[]? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(_path);

        if (!OperatingSystem.IsWindows())
        {
            return bytes;
        }

        try
        {
            return Unprotect(bytes);
        }
        catch (CryptographicException)
        {
            // Written by another Windows account, or restored from a machine backup. Not something
            // to repair: the answer is to sign in again, and pretending the file is readable would
            // fail later with something far less obvious.
            return null;
        }
    }

    public void Write(byte[] contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var directory = System.IO.Path.GetDirectoryName(_path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(_path, OperatingSystem.IsWindows() ? Protect(contents) : contents);

        RestrictToOwner(_path);
    }

    /// <summary>Removes the file, and with it the session it holds.</summary>
    public void Delete()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] contents) =>
        ProtectedData.Protect(contents, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] Unprotect(byte[] contents) =>
        ProtectedData.Unprotect(contents, Entropy, DataProtectionScope.CurrentUser);

    /// <summary>
    /// Owner-only permissions, where the file system has them.
    /// </summary>
    /// <remarks>
    /// The umask usually does this already; usually is not a guarantee, and this file is the one
    /// place in the app where the difference matters.
    /// </remarks>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // A file system without Unix modes. Nothing to do, and not worth failing a sign-in over.
        }
    }
}
