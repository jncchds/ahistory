using System.Security.Cryptography;
using Archive.Core;

namespace Archive.Media;

/// <summary>Result of storing one file.</summary>
/// <param name="Hash">Lowercase hex SHA-256 of the contents.</param>
/// <param name="Extension">Normalized extension, including the leading dot, or empty.</param>
/// <param name="ByteSize">Size of the stored file.</param>
/// <param name="WasNew">False when identical bytes were already stored.</param>
public readonly record struct MediaPutResult(string Hash, string Extension, long ByteSize, bool WasNew);

public interface IMediaStore
{
    string Root { get; }

    Task<MediaPutResult> PutAsync(Stream source, string? extension = null, CancellationToken cancellationToken = default);

    Task<MediaPutResult> PutFileAsync(string path, CancellationToken cancellationToken = default);

    string PathFor(string hash, string? extension = null);

    bool Exists(string hash, string? extension = null);

    Stream OpenRead(string hash, string? extension = null);
}

/// <summary>
/// Content-addressed blob store on the local filesystem.
/// </summary>
/// <remarks>
/// §1: exports repeat the same stickers and forwarded images hundreds of times, so identical
/// bytes are written once and every reference points at the same file.
/// </remarks>
public sealed class FileSystemMediaStore : IMediaStore
{
    /// <summary>
    /// Staging directory for in-progress writes, inside the media root.
    /// </summary>
    /// <remarks>
    /// Inside the root deliberately: the final step is a rename, and a rename is only atomic
    /// within one volume. Staging in the system temp directory would silently degrade to a
    /// copy — and a copy that is interrupted leaves a truncated file at the destination, which
    /// is a corrupt blob under a hash that claims otherwise.
    ///
    /// The leading dot keeps it out of the way of the two-hex-character shard directories.
    /// </remarks>
    private const string StagingDirectory = ".staging";

    public FileSystemMediaStore(ArchiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        Root = options.ResolveMediaDirectory();
        Directory.CreateDirectory(Root);
    }

    public FileSystemMediaStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathFor(string hash, string? extension = null) =>
        MediaAddress.FullPath(Root, hash, extension);

    public bool Exists(string hash, string? extension = null) =>
        File.Exists(PathFor(hash, extension));

    public Stream OpenRead(string hash, string? extension = null) =>
        File.OpenRead(PathFor(hash, extension));

    public async Task<MediaPutResult> PutFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var source = File.OpenRead(path);
        return await PutAsync(source, Path.GetExtension(path), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams <paramref name="source"/> into the store, hashing as it goes.
    /// </summary>
    /// <remarks>
    /// One pass: the content address is not known until the bytes have been read, so they are
    /// written to a staging file and hashed simultaneously, then renamed into place. Reading the
    /// stream twice is not an option — imports read from archives and network-backed streams
    /// that are not seekable.
    /// </remarks>
    public async Task<MediaPutResult> PutAsync(
        Stream source,
        string? extension = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var normalized = MediaAddress.NormalizeExtension(extension);
        var staging = Path.Combine(Root, StagingDirectory);
        Directory.CreateDirectory(staging);

        var stagingPath = Path.Combine(staging, Guid.NewGuid().ToString("N"));

        string hash;
        long byteSize;

        try
        {
            await using (var destination = new FileStream(
                stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hasher = SHA256.Create())
            {
                // CryptoStream over the destination hashes exactly the bytes that get written,
                // so the address can never describe something other than the stored file.
                await using var hashing = new CryptoStream(destination, hasher, CryptoStreamMode.Write, leaveOpen: true);

                await source.CopyToAsync(hashing, cancellationToken).ConfigureAwait(false);
                await hashing.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);

                byteSize = destination.Length;
                hash = Convert.ToHexStringLower(hasher.Hash!);
            }

            var target = PathFor(hash, normalized);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            try
            {
                // overwrite: false is the deduplication. Catching the collision is cheaper and
                // more correct than checking Exists first, which races against a concurrent put
                // and would rewrite a file another caller is reading.
                File.Move(stagingPath, target, overwrite: false);

                return new MediaPutResult(hash, normalized, byteSize, WasNew: true);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Identical bytes are already stored. That is the expected path for stickers and
                // forwards, not an error.
                return new MediaPutResult(hash, normalized, byteSize, WasNew: false);
            }
        }
        finally
        {
            // A failure part-way through must leave nothing behind: no partial file at the
            // destination (it was never moved) and no orphan in staging.
            if (File.Exists(stagingPath))
            {
                try
                {
                    File.Delete(stagingPath);
                }
                catch (IOException)
                {
                    // Losing a staging file is not worth failing an import over.
                }
            }
        }
    }
}
