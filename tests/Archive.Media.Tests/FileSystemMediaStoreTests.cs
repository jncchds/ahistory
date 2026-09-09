using System.Text;
using Archive.Media;

namespace Archive.Media.Tests;

public sealed class FileSystemMediaStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));

    private FileSystemMediaStore Store() => new(_root);

    private static Stream Bytes(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    /// <summary>Files actually stored, ignoring the staging directory.</summary>
    private string[] StoredFiles() =>
        Directory.Exists(_root)
            ? [.. Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .Where(p => !p.Contains(".staging", StringComparison.Ordinal))]
            : [];

    [Fact]
    public async Task Identical_bytes_produce_one_file()
    {
        var store = Store();

        var first = await store.PutAsync(Bytes("a voice note"), ".ogg");
        var second = await store.PutAsync(Bytes("a voice note"), ".ogg");

        Assert.Equal(first.Hash, second.Hash);
        Assert.True(first.WasNew);
        Assert.False(second.WasNew);
        Assert.Single(StoredFiles());
    }

    [Fact]
    public async Task Different_bytes_produce_different_files()
    {
        var store = Store();

        var first = await store.PutAsync(Bytes("one"), ".txt");
        var second = await store.PutAsync(Bytes("two"), ".txt");

        Assert.NotEqual(first.Hash, second.Hash);
        Assert.Equal(2, StoredFiles().Length);
    }

    [Fact]
    public async Task The_path_is_sharded_by_the_first_four_hex_characters()
    {
        var store = Store();

        var result = await store.PutAsync(Bytes("sharded"), ".bin");
        var relative = Path.GetRelativePath(_root, store.PathFor(result.Hash, result.Extension));
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Assert.Equal(3, segments.Length);
        Assert.Equal(result.Hash[..2], segments[0]);
        Assert.Equal(result.Hash[2..4], segments[1]);
        Assert.Equal(result.Hash + ".bin", segments[2]);
    }

    [Fact]
    public async Task The_stored_bytes_are_readable_and_unchanged()
    {
        var store = Store();

        var result = await store.PutAsync(Bytes("привет"), ".txt");

        using var reader = new StreamReader(store.OpenRead(result.Hash, result.Extension));

        Assert.Equal("привет", await reader.ReadToEndAsync());
        Assert.True(store.Exists(result.Hash, result.Extension));
    }

    /// <summary>
    /// Telegram exports are inconsistent about extension case, and on a case-sensitive
    /// filesystem two spellings would mean two files for one hash.
    /// </summary>
    [Fact]
    public async Task Extension_case_does_not_create_a_second_file()
    {
        var store = Store();

        var lower = await store.PutAsync(Bytes("photo bytes"), ".jpg");
        var upper = await store.PutAsync(Bytes("photo bytes"), ".JPG");

        Assert.Equal(lower.Extension, upper.Extension);
        Assert.False(upper.WasNew);
        Assert.Single(StoredFiles());
    }

    [Fact]
    public async Task A_missing_extension_is_allowed()
    {
        var store = Store();

        var result = await store.PutAsync(Bytes("no extension"));

        Assert.Equal(string.Empty, result.Extension);
        Assert.True(store.Exists(result.Hash));
    }

    /// <summary>
    /// The dedupe must hold when two imports touch the same sticker at the same moment, which is
    /// exactly what a parallel import does.
    /// </summary>
    [Fact]
    public async Task Concurrent_puts_of_the_same_bytes_are_safe()
    {
        var store = Store();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => store.PutAsync(Bytes("a popular sticker"), ".webp")));

        Assert.Single(results.Select(r => r.Hash).Distinct());
        Assert.Single(StoredFiles());

        // Exactly one caller may claim to have created it; the rest must see a deduplicate.
        Assert.Equal(1, results.Count(r => r.WasNew));
    }

    [Fact]
    public async Task A_put_that_fails_midway_leaves_no_partial_file()
    {
        var store = Store();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.PutAsync(new FailingStream(), ".bin"));

        Assert.Empty(StoredFiles());

        var staging = Path.Combine(_root, ".staging");
        Assert.True(!Directory.Exists(staging) || Directory.GetFiles(staging).Length == 0);
    }

    [Fact]
    public async Task Byte_size_is_reported()
    {
        var store = Store();

        var result = await store.PutAsync(Bytes("12345"));

        Assert.Equal(5, result.ByteSize);
    }

    [Fact]
    public void An_invalid_hash_is_rejected_rather_than_building_a_nonsense_path()
    {
        Assert.Throws<ArgumentException>(() => MediaAddress.RelativePath("not-a-hash"));
        Assert.Throws<ArgumentException>(() => MediaAddress.RelativePath(new string('A', 64)));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp directory; the OS will reclaim it.
        }
    }

    /// <summary>A stream that dies part-way through, standing in for a truncated export file.</summary>
    private sealed class FailingStream : Stream
    {
        private int _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read++ == 0)
            {
                buffer.AsSpan(offset, Math.Min(count, 8)).Fill(1);
                return Math.Min(count, 8);
            }

            throw new InvalidOperationException("stream died");
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
