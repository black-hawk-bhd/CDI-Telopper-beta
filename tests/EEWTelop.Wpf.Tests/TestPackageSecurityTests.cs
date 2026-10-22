using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EEWTelop.Application.Operations;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class TestPackageSecurityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string _directory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"qt-package-security-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public async Task ImportRejectsTooManyEntriesBeforePublishing()
    {
        string package = CreatePackage(archive =>
        {
            for (int index = 0; index < 1024; index++) archive.CreateEntry($"unused-{index}/");
        });
        var library = CreateLibrary();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => library.ImportPackageAsync(package));

        Assert.IsEmpty(library.List());
        Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(_directory, "library")));
    }

    [TestMethod]
    public async Task ValidExportImportPreservesPayloadsNestedPathsAndExtraImages()
    {
        string package = CreatePackage(archive =>
        {
            WriteEntry(archive, "nested/extra.xml", "<Report />");
            WriteEntry(archive, "reference.png", "reference-image");
            WriteEntry(archive, "extra.png", "extra-image");
        }, referenceImage: "reference.png");
        var library = CreateLibrary();

        TestCaseManifest imported = await library.ImportPackageAsync(package);
        Assert.AreNotEqual("original", imported.Id);
        Assert.AreEqual("reference.png", imported.ReferenceImageFile);
        Assert.HasCount(1, library.LoadMessages(imported.Id, SourceMode.HistoryRehearsal));
        string exported = Path.Combine(_directory, "exported.zip");
        await library.ExportAsync(imported.Id, exported);
        using ZipArchive roundTrip = ZipFile.OpenRead(exported);
        Assert.IsNotNull(roundTrip.GetEntry("nested/extra.xml"));
        Assert.IsNotNull(roundTrip.GetEntry("extra.png"));
        Assert.IsNotNull(roundTrip.GetEntry("reference.png"));
    }

    [TestMethod]
    public async Task ImportRejectsOversizedCompressedFile()
    {
        string package = CreatePackage();
        using (var file = new FileStream(package, FileMode.Open, FileAccess.Write))
            file.SetLength(64L * 1024 * 1024 + 1);
        InvalidDataException error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CreateLibrary().ImportPackageAsync(package));
        StringAssert.Contains(error.Message, "64MiB");
        AssertLibraryEmpty();
    }

    [TestMethod]
    public async Task ImportRejectsOversizedManifest()
    {
        string package = CreatePackage(manifestSuffix: new string(' ', 1024 * 1024));
        InvalidDataException error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CreateLibrary().ImportPackageAsync(package));
        StringAssert.Contains(error.Message, "1MiB");
        AssertLibraryEmpty();
    }

    [TestMethod]
    public async Task ImportRejectsOversizedUnreferencedEntryBeforeExtraction()
    {
        string package = CreatePackage(archive => WriteEntry(archive, "unused.bin", string.Empty));
        PatchDeclaredLength(package, "unused.bin", 32 * 1024 * 1024 + 1);
        InvalidDataException error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CreateLibrary().ImportPackageAsync(package));
        StringAssert.Contains(error.Message, "32MiB");
        AssertLibraryEmpty();
    }

    [TestMethod]
    public async Task ImportRejectsAggregateCompressedEntrySizesBeforeExtraction()
    {
        string package = CreatePackage(archive =>
        {
            WriteEntry(archive, "unused-1.bin", string.Empty);
            WriteEntry(archive, "unused-2.bin", string.Empty);
        });
        PatchDirectoryLength(package, "unused-1.bin", 33 * 1024 * 1024, 20);
        PatchDirectoryLength(package, "unused-2.bin", 33 * 1024 * 1024, 20);
        InvalidDataException error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CreateLibrary().ImportPackageAsync(package));
        StringAssert.Contains(error.Message, "合計圧縮データ");
        AssertLibraryEmpty();
    }

    [TestMethod]
    public async Task ImportRejectsAggregateExpandedSizeBeforeExtraction()
    {
        string package = CreatePackage(archive =>
        {
            for (int index = 0; index < 5; index++) WriteEntry(archive, $"unused-{index}.bin", string.Empty);
        });
        for (int index = 0; index < 5; index++)
            PatchDeclaredLength(package, $"unused-{index}.bin", 32 * 1024 * 1024);
        InvalidDataException error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CreateLibrary().ImportPackageAsync(package));
        StringAssert.Contains(error.Message, "128MiB");
        AssertLibraryEmpty();
    }

    [TestMethod]
    public async Task ImportRejectsActualExpansionBeyondDeclaredSize()
    {
        string package = CreatePackage(archive => WriteEntry(archive, "unused.bin", new string('x', 64 * 1024)));
        PatchDeclaredLength(package, "unused.bin", 1);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CreateLibrary().ImportPackageAsync(package));
        AssertLibraryEmpty();
    }

    [TestMethod]
    public async Task MetadataBudgetRejectsDishonestFooterCountBeforeMaterializingDirectory()
    {
        string package = CreatePackage(archive =>
        {
            string prefix = new('x', 2100);
            for (int index = 0; index < 1024; index++) archive.CreateEntry($"{prefix}{index}/");
        });
        byte[] bytes = await File.ReadAllBytesAsync(package);
        Assert.AreEqual(0x06054b50U, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 22)));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 12), 1);
        await File.WriteAllBytesAsync(package, bytes);

        InvalidDataException error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CreateLibrary().ImportPackageAsync(package));

        StringAssert.Contains(error.Message, "管理情報");
        AssertLibraryEmpty();
    }

    [TestMethod]
    [DataRow("../escape.json")]
    [DataRow("folder/../../escape.json")]
    [DataRow("C:\\escape.json")]
    [DataRow("payload.json:stream")]
    [DataRow("payload.json.")]
    [DataRow("NUL.json")]
    public async Task ImportRejectsUnsafePaths(string name)
    {
        string package = CreatePackage(archive => WriteEntry(archive, name, "{}"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CreateLibrary().ImportPackageAsync(package));
        AssertLibraryEmpty();
        Assert.IsFalse(File.Exists(Path.Combine(_directory, "escape.json")));
    }

    [TestMethod]
    [DataRow("PAYLOAD.JSON")]
    [DataRow("./payload.json")]
    [DataRow("nested/../payload.json")]
    public async Task ImportRejectsDuplicateOrAliasedDestinations(string name)
    {
        string package = CreatePackage(archive => WriteEntry(archive, name, "{}"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CreateLibrary().ImportPackageAsync(package));
        AssertLibraryEmpty();
    }

    [TestMethod]
    public async Task ImportRejectsDuplicateManifestReferences()
    {
        string package = CreatePackage(mutateManifest: manifest => manifest with { PayloadFiles = ["payload.json", "PAYLOAD.JSON"] });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CreateLibrary().ImportPackageAsync(package));
        AssertLibraryEmpty();
    }

    [TestMethod]
    public async Task CancelledImportDoesNotPublishACase()
    {
        string package = CreatePackage();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateLibrary().ImportPackageAsync(package, cancellation.Token));
        AssertLibraryEmpty();
    }

    private void AssertLibraryEmpty()
    {
        Assert.IsEmpty(CreateLibrary().List());
        Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(_directory, "library")));
    }

    private FileTestCaseLibrary CreateLibrary() => new(Path.Combine(_directory, "library"));

    private string CreatePackage(Action<ZipArchive>? configure = null, string referenceImage = "",
        string manifestSuffix = "", Func<TestCaseManifest, TestCaseManifest>? mutateManifest = null)
    {
        string package = Path.Combine(_directory, $"package-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(package, ZipArchiveMode.Create);
        var manifest = new TestCaseManifest(
            TestCaseManifest.CurrentSchemaVersion, "original", "Package", "test", "p2pquake", "551",
            string.Empty, [], string.Empty, DateTimeOffset.UtcNow, ["payload.json"], referenceImage,
            new TestCaseExpectation(string.Empty, string.Empty, null, [], [], [], string.Empty, string.Empty));
        if (mutateManifest is not null) manifest = mutateManifest(manifest);
        WriteEntry(archive, "manifest.json", JsonSerializer.Serialize(manifest, JsonOptions) + manifestSuffix);
        WriteEntry(archive, "payload.json", "{}");
        configure?.Invoke(archive);
        return package;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    private static void PatchDeclaredLength(string package, string name, uint length) =>
        PatchDirectoryLength(package, name, length, 24);

    private static void PatchDirectoryLength(string package, string name, uint length, int fieldOffset)
    {
        byte[] bytes = File.ReadAllBytes(package);
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        for (int index = 0; index <= bytes.Length - 46 - nameBytes.Length; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index)) != 0x02014b50U ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(index + 28)) != nameBytes.Length ||
                !bytes.AsSpan(index + 46, nameBytes.Length).SequenceEqual(nameBytes)) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index + fieldOffset), length);
            File.WriteAllBytes(package, bytes);
            return;
        }
        Assert.Fail("Central directory entry not found.");
    }
}
