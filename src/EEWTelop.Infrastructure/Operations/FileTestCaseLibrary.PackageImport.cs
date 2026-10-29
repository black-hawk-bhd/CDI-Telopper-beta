using System.IO.Compression;
using EEWTelop.Application.Operations;

namespace EEWTelop.Infrastructure.Operations;

public sealed partial class FileTestCaseLibrary
{
    private const long MaximumPackageBytes = 64 * 1024 * 1024;
    private const long MaximumPackageEntryBytes = 32 * 1024 * 1024;
    private const long MaximumPackageExpandedBytes = 128 * 1024 * 1024;
    private const long MaximumPackageManifestBytes = 1024 * 1024;
    private const int MaximumPackageEntries = 1024;
    private static readonly uint[] PackageCrcTable = CreatePackageCrcTable();

    // ZipArchive materializes its directory before Entries.Count can be checked.
    // Bound those reads too, including dishonest entry counts in the ZIP footer.
    private const long MaximumPackageMetadataBytes = 2 * 1024 * 1024;

    public Task<TestCaseManifest> ImportPackageAsync(string zipPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportPackageCoreAsync(zipPath, cancellationToken), cancellationToken);

    private async Task<TestCaseManifest> ImportPackageCoreAsync(string zipPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string temporary = Path.Combine(Path.GetTempPath(), $"qtcase-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            // Keep the checked file open without write sharing throughout import.
            using var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaximumPackageBytes)
                throw new InvalidDataException("ZIPの圧縮サイズは64MiB以下にしてください。");
            using var input = new PackageReadStream(file, cancellationToken);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries;
            if (entries.Count > MaximumPackageEntries)
                throw new InvalidDataException("ZIP内のファイル・フォルダーは合計1024件以下にしてください。");
            input.FinishMetadataRead();

            var destinations = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            long declaredTotal = 0;
            long compressedTotal = 0;
            foreach (ZipArchiveEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = GetPackageDestination(temporary, entry.FullName);
                if (!destinations.TryAdd(destination, entry))
                    throw new InvalidDataException("ZIP内のファイル名が重複しています。");
                // Count each entry, even if malformed directory records reuse the
                // same compressed range, to bound repeated decompression work.
                if (entry.CompressedLength > MaximumPackageBytes - compressedTotal)
                    throw new InvalidDataException("ZIP内の合計圧縮データは64MiB以下にしてください。");
                if (entry.Length > MaximumPackageEntryBytes ||
                    entry.Length > MaximumPackageExpandedBytes - declaredTotal)
                    throw new InvalidDataException("ZIPの展開量は1ファイル32MiB・合計128MiB以下にしてください。");
                if (IsPackageDirectory(entry) && entry.Length != 0)
                    throw new InvalidDataException("ZIP内のフォルダーに不正なデータがあります。");
                declaredTotal += entry.Length;
                compressedTotal += entry.CompressedLength;
            }

            string manifestPath = Path.Combine(temporary, "manifest.json");
            if (!destinations.TryGetValue(manifestPath, out ZipArchiveEntry? manifestEntry) ||
                IsPackageDirectory(manifestEntry))
                throw new InvalidDataException("manifest.jsonがありません。");
            if (manifestEntry.Length > MaximumPackageManifestBytes)
                throw new InvalidDataException("manifest.jsonは1MiB以下にしてください。");
            long expandedTotal = await ExtractPackageEntryAsync(
                manifestEntry, manifestPath, MaximumPackageManifestBytes, cancellationToken).ConfigureAwait(false);
            TestCaseManifest original = TryReadManifest(manifestPath)
                ?? throw new InvalidDataException("manifest.jsonの形式が不正です。");
            ValidatePackageReferences(temporary, original, destinations);

            // Preserve all entries: existing exports can contain extra reference images.
            foreach ((string destination, ZipArchiveEntry entry) in destinations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry == manifestEntry) continue;
                if (IsPackageDirectory(entry)) { Directory.CreateDirectory(destination); continue; }
                long budget = Math.Min(MaximumPackageEntryBytes, MaximumPackageExpandedBytes - expandedTotal);
                expandedTotal += await ExtractPackageEntryAsync(entry, destination, budget, cancellationToken)
                    .ConfigureAwait(false);
            }
            foreach (string path in original.PayloadFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidatePayload(ResolveManagedFile(temporary, path));
            }
            if (!string.IsNullOrWhiteSpace(original.ReferenceImageFile))
                ValidatePayload(ResolveManagedFile(temporary, original.ReferenceImageFile));

            var imported = original with { Id = Guid.NewGuid().ToString("N"), CreatedAtUtc = DateTimeOffset.UtcNow };
            await WriteManifestAsync(temporary, imported, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporary, GetCaseDirectory(imported.Id));
            return imported;
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }

    private static void ValidatePackageReferences(
        string directory, TestCaseManifest manifest, IReadOnlyDictionary<string, ZipArchiveEntry> destinations)
    {
        if (manifest.PayloadFiles.Count > MaximumPackageEntries)
            throw new InvalidDataException("テストケースの参照ファイルが多すぎます。");
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in manifest.PayloadFiles)
        {
            string destination = GetPackageDestination(directory, path);
            if (!references.Add(destination))
                throw new InvalidDataException("テストケースの参照ファイルが重複しています。");
            RequirePackageFile(destination, destinations);
        }
        if (!string.IsNullOrWhiteSpace(manifest.ReferenceImageFile))
            RequirePackageFile(GetPackageDestination(directory, manifest.ReferenceImageFile), destinations);
    }

    private static void RequirePackageFile(string path, IReadOnlyDictionary<string, ZipArchiveEntry> destinations)
    {
        if (!destinations.TryGetValue(path, out ZipArchiveEntry? entry) || IsPackageDirectory(entry))
            throw new InvalidDataException("テストケースの参照ファイルがZIP内にありません。");
    }

    private static string GetPackageDestination(string directory, string name)
    {
        string relative = name.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("ZIP内に不正なパスがあります。");
        foreach (string part in relative.Split('/'))
        {
            if (string.IsNullOrEmpty(part) || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(part))
                throw new InvalidDataException("ZIP内に不正なファイル名があります。");
        }
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        string destination = Path.GetFullPath(Path.Combine(directory, relative));
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ZIP内に不正なパスがあります。");
        return destination;
    }

    private static bool IsDeviceName(string name)
    {
        string stem = name.Split('.')[0].TrimEnd(' ');
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³') &&
                (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsPackageDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    private static async Task<long> ExtractPackageEntryAsync(
        ZipArchiveEntry entry, string destination, long budget, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using Stream input = entry.Open();
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 16 * 1024, FileOptions.Asynchronous);
        byte[] buffer = new byte[16 * 1024];
        long copied = 0;
        uint crc = uint.MaxValue;
        while (true)
        {
            // Read at most one byte beyond the budget and reject before writing it.
            int count = (int)Math.Min(buffer.Length, budget - copied + 1);
            int read = await input.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (read > budget - copied || read > entry.Length - copied)
                throw new InvalidDataException("ZIP内の実際の展開量が上限または申告サイズを超えました。");
            crc = AppendPackageCrc(crc, buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
        }
        if (copied != entry.Length) throw new InvalidDataException("ZIP内のファイルサイズが一致しません。");
        // Some runtime decoders stop at the advertised size. Verify the checksum
        // as well so a truncated or corrupted payload is not silently accepted.
        if (~crc != entry.Crc32) throw new InvalidDataException("ZIP内のファイルのCRCが一致しません。");
        return copied;
    }

    private static uint AppendPackageCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) crc = PackageCrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        return crc;
    }

    private static uint[] CreatePackageCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320U;
            table[index] = value;
        }
        return table;
    }

    private sealed class PackageReadStream(Stream inner, CancellationToken importCancellation) : Stream
    {
        private long? _metadataRemaining = MaximumPackageMetadataBytes;

        public void FinishMetadataRead() => _metadataRemaining = null;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => Seek(value, SeekOrigin.Begin); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            importCancellation.ThrowIfCancellationRequested();
            int read = inner.Read(buffer[..LimitRead(buffer.Length)]);
            AccountRead(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            importCancellation.ThrowIfCancellationRequested();
            int read = await inner.ReadAsync(buffer[..LimitRead(buffer.Length)], cancellationToken).ConfigureAwait(false);
            AccountRead(read);
            return read;
        }

        private int LimitRead(int count) => _metadataRemaining is long remaining
            ? (int)Math.Min(count, remaining + 1) : count;

        private void AccountRead(int count)
        {
            if (_metadataRemaining is not long remaining) return;
            if (count > remaining) throw new InvalidDataException("ZIPの管理情報が2MiBの上限を超えました。");
            _metadataRemaining = remaining - count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            importCancellation.ThrowIfCancellationRequested();
            return inner.Seek(offset, origin);
        }

        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
