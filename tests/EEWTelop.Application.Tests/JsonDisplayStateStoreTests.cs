using System.Text.Json;
using System.Text.Json.Nodes;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Application.Persistence;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class JsonDisplayStateStoreTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private string _directory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"eewtelop-state-null-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("recentSignatures", "")]
    [DataRow("pending", "")]
    [DataRow("current", "pages")]
    [DataRow("persistentTsunami", "pages")]
    [DataRow("pending", "pages")]
    [DataRow("current", "blocks")]
    [DataRow("persistentTsunami", "blocks")]
    [DataRow("pending", "blocks")]
    public async Task NullArrayEntryIsBackedUpAndRecoveredAsEmptyState(
        string member,
        string nestedMember)
    {
        JsonObject document = JsonSerializer.SerializeToNode(CreateState(), WebJson)!.AsObject();
        if (nestedMember.Length == 0)
        {
            document[member] = JsonNode.Parse("[null]");
        }
        else
        {
            JsonNode program = member == "pending" ? document[member]![0]! : document[member]!;
            JsonNode target = nestedMember == "blocks" ? program["pages"]![0]! : program;
            target[nestedMember] = JsonNode.Parse("[null]");
        }

        string json = document.ToJsonString();
        string path = Path.Combine(_directory, "state.json");
        await File.WriteAllTextAsync(path, json);
        var logs = new UiLogBuffer();
        var store = new JsonDisplayStateStore(path, logs);

        DisplayStateDocument recovered = await store.LoadAsync();

        Assert.AreEqual(DisplayStateDocument.CurrentSchemaVersion, recovered.SchemaVersion);
        Assert.IsNull(recovered.Current);
        Assert.IsNull(recovered.PersistentTsunami);
        Assert.IsEmpty(recovered.Pending);
        Assert.IsEmpty(recovered.RecentSignatures);
        Assert.IsFalse(File.Exists(path));
        string[] backups = Directory.GetFiles(_directory, "state.corrupt-*.json");
        Assert.HasCount(1, backups);
        Assert.AreEqual(json, await File.ReadAllTextAsync(backups[0]));
        AppLogEntry warning = logs.GetSnapshot().Single(static entry => entry.EventName == "StateRecovered");
        Assert.AreEqual(AppLogLevel.Warning, warning.Level);
    }

    [TestMethod]
    public async Task ValidStatePreservesProgramsAndSignatureMetadata()
    {
        string path = Path.Combine(_directory, "state.json");
        var logs = new UiLogBuffer();
        var store = new JsonDisplayStateStore(path, logs);
        DisplayStateDocument expected = CreateState();

        await store.SaveAsync(expected);
        DisplayStateDocument actual = await store.LoadAsync();

        Assert.IsNotNull(actual.Current);
        Assert.IsNotNull(actual.PersistentTsunami);
        Assert.AreEqual(expected.Current?.ProgramId, actual.Current.ProgramId);
        Assert.AreEqual(expected.PersistentTsunami?.ProgramId, actual.PersistentTsunami.ProgramId);
        Assert.HasCount(1, actual.Pending);
        Assert.AreEqual(expected.Pending[0].ProgramId, actual.Pending[0].ProgramId);
        Assert.HasCount(1, actual.RecentSignatures);
        Assert.AreEqual(expected.RecentSignatures[0], actual.RecentSignatures[0]);
        Assert.IsTrue(File.Exists(path));
        Assert.IsEmpty(Directory.GetFiles(_directory, "state.corrupt-*.json"));
        Assert.IsEmpty(logs.GetSnapshot());
    }

    [TestMethod]
    public async Task LegitimateEmptyStateAllowsNullCurrentAndPersistentPrograms()
    {
        string path = Path.Combine(_directory, "state.json");
        var logs = new UiLogBuffer();
        var store = new JsonDisplayStateStore(path, logs);
        await store.SaveAsync(DisplayStateDocument.Empty(DateTimeOffset.UtcNow));

        DisplayStateDocument actual = await store.LoadAsync();

        Assert.IsNull(actual.Current);
        Assert.IsNull(actual.PersistentTsunami);
        Assert.IsEmpty(actual.Pending);
        Assert.IsEmpty(actual.RecentSignatures);
        Assert.IsTrue(File.Exists(path));
        Assert.IsEmpty(Directory.GetFiles(_directory, "state.corrupt-*.json"));
        Assert.IsEmpty(logs.GetSnapshot());
    }

    private static DisplayStateDocument CreateState()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DisplayProgram program = CoordinatorTestSupport.Program("stored-tsunami", EventKind.Tsunami,
            OverlayPriority.TsunamiWarning, issuedAt: now, pageCount: 1,
            endPolicy: EndPolicy.LoopUntilReplaced);
        StoredDisplayProgram stored = StoredDisplayProgram.From(program, now);
        return DisplayStateDocument.Empty(now) with
        {
            Current = stored,
            PersistentTsunami = stored,
            Pending = [stored],
            RecentSignatures = [new StoredEventSignature("axis", EventKind.Eew, "stored-event", "signature")
            {
                SourceMode = SourceMode.Sandbox,
                IsTest = true,
            }],
        };
    }
}
