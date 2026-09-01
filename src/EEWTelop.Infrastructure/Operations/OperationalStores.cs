using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.Operations;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Persistence;
using EEWTelop.Infrastructure.Settings;

namespace EEWTelop.Infrastructure.Operations;

public sealed class JsonSettingsProfileStore : ISettingsProfileStore
{
    private readonly string _directory;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonSettingsProfileStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public IReadOnlyList<string> List() => new DirectoryInfo(_directory)
        .GetFiles("*.qtprofile.json")
        .Select(static item => Path.GetFileName(item.Name).Replace(".qtprofile.json", string.Empty, StringComparison.OrdinalIgnoreCase))
        .OrderBy(static item => item, StringComparer.CurrentCultureIgnoreCase).ToArray();

    public Task SaveAsync(string name, AppSettings settings, string applicationVersion, CancellationToken cancellationToken = default)
    {
        string normalizedName = NormalizeName(name);
        var document = new SettingsProfileDocument(
            SettingsProfileDocument.CurrentSchemaVersion,
            normalizedName,
            DateTimeOffset.UtcNow,
            applicationVersion,
            RemoveSecrets(JsonSettingsStore.NormalizeDocument(settings)));
        return WriteAsync(GetPath(normalizedName), document, cancellationToken);
    }

    public async Task<SettingsProfileDocument> LoadAsync(string name, AppSettings currentSettings, CancellationToken cancellationToken = default)
    {
        string path = GetPath(NormalizeName(name));
        SettingsProfileDocument document = await ReadDocumentAsync(path, cancellationToken).ConfigureAwait(false);
        return ValidateAndMerge(document, currentSettings);
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = GetPath(NormalizeName(name));
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task ExportAsync(string name, string path, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(GetPath(NormalizeName(name)), cancellationToken).ConfigureAwait(false);
        await AtomicFileWriter.WriteAsync(path, (stream, token) => stream.WriteAsync(bytes, token).AsTask(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SettingsProfileDocument> ImportAsync(string path, AppSettings currentSettings, CancellationToken cancellationToken = default)
    {
        SettingsProfileDocument document = await ReadDocumentAsync(path, cancellationToken).ConfigureAwait(false);
        SettingsProfileDocument merged = ValidateAndMerge(document, currentSettings);
        await WriteAsync(GetPath(NormalizeName(merged.Name)), merged with { Settings = RemoveSecrets(merged.Settings) }, cancellationToken)
            .ConfigureAwait(false);
        return merged;
    }

    private static SettingsProfileDocument ValidateAndMerge(SettingsProfileDocument document, AppSettings current)
    {
        if (document.SchemaVersion <= 0)
            throw new InvalidDataException($"不正なプロファイル形式です: {document.SchemaVersion}");
        if (document.SchemaVersion > SettingsProfileDocument.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"このアプリではプロファイル形式 {document.SchemaVersion} を適用できません。対応上限は {SettingsProfileDocument.CurrentSchemaVersion} です。");
        if (document.Settings is null)
            throw new InvalidDataException("プロファイルに設定本体がありません。");
        var migrationIssues = new List<string>(document.MigrationIssues ?? []);
        if (document.SchemaVersion < SettingsProfileDocument.CurrentSchemaVersion)
            migrationIssues.Add(
                $"プロファイル形式を {document.SchemaVersion} から {SettingsProfileDocument.CurrentSchemaVersion} へ移行しました。");
        int sourceSettingsSchema = document.Settings.SchemaVersion;
        AppSettings normalized = JsonSettingsStore.NormalizeDocument(document.Settings);
        if (sourceSettingsSchema != normalized.SchemaVersion)
            migrationIssues.Add(
                $"設定形式を {sourceSettingsSchema} から {normalized.SchemaVersion} へ移行しました。");
        normalized = normalized with
        {
            Provider = normalized.Provider with
            {
                DmdataProtectedCredential = current.Provider.DmdataProtectedCredential,
                AxisProtectedAccessToken = current.Provider.AxisProtectedAccessToken,
            },
            Obs = normalized.Obs with
            {
                WebSocketProtectedPassword = current.Obs.WebSocketProtectedPassword,
            },
        };
        return document with
        {
            SchemaVersion = SettingsProfileDocument.CurrentSchemaVersion,
            Name = NormalizeName(document.Name),
            Settings = normalized,
            MigrationIssues = migrationIssues.Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    private async Task<SettingsProfileDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        byte[] content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (content.Length == 0) throw new InvalidDataException("プロファイル文書が空です。");
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(content);
            bool wrapped = parsed.RootElement.ValueKind == JsonValueKind.Object &&
                parsed.RootElement.EnumerateObject().Any(property =>
                    string.Equals(property.Name, "settings", StringComparison.OrdinalIgnoreCase));
            if (wrapped)
            {
                SettingsProfileDocument? document = JsonSerializer.Deserialize<SettingsProfileDocument>(content, _json);
                return document ?? throw new InvalidDataException("プロファイル文書を読み取れません。");
            }

            AppSettings? legacySettings = JsonSerializer.Deserialize<AppSettings>(content, _json);
            if (legacySettings is null) throw new InvalidDataException("旧設定ファイルを読み取れません。");
            string fileName = Path.GetFileName(path);
            string name = fileName.EndsWith(".qtprofile.json", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^".qtprofile.json".Length]
                : Path.GetFileNameWithoutExtension(fileName);
            return new SettingsProfileDocument(SettingsProfileDocument.CurrentSchemaVersion,
                NormalizeName(name), DateTimeOffset.UtcNow, "旧設定から移行",
                JsonSettingsStore.NormalizeDocument(legacySettings))
            {
                MigrationIssues =
                [
                    "旧設定ファイルをプロファイル形式へ変換しました。",
                    $"設定形式を {legacySettings.SchemaVersion} から {AppSettings.CurrentSchemaVersion} へ移行しました。",
                ],
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("プロファイルJSONが壊れています。", exception);
        }
    }

    private static AppSettings RemoveSecrets(AppSettings settings) => settings with
    {
        Provider = settings.Provider with
        {
            DmdataProtectedCredential = string.Empty,
            AxisProtectedAccessToken = string.Empty,
        },
        Obs = settings.Obs with { WebSocketProtectedPassword = string.Empty },
    };

    private Task WriteAsync(string path, SettingsProfileDocument document, CancellationToken cancellationToken) =>
        AtomicFileWriter.WriteAsync(path, (stream, token) => JsonSerializer.SerializeAsync(stream, document, _json, token), cancellationToken);

    private string GetPath(string name) => Path.Combine(_directory, name + ".qtprofile.json");

    private static string NormalizeName(string name)
    {
        string result = string.Concat((name ?? string.Empty).Trim().Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        if (string.IsNullOrWhiteSpace(result)) throw new ArgumentException("プロファイル名を入力してください。", nameof(name));
        return result.Length > 80 ? result[..80] : result;
    }
}

public sealed class FileTestCaseLibrary : ITestCaseLibrary
{
    public const string JmaXmlTestProviderName = "test-library-jma-xml";
    public const string DmdataTestProviderName = "dmdata";

    private readonly string _directory;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public FileTestCaseLibrary(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public IReadOnlyList<TestCaseManifest> List() => Directory.EnumerateFiles(_directory, "manifest.json", SearchOption.AllDirectories)
        .Select(TryReadManifest).Where(static value => value is not null).Cast<TestCaseManifest>()
        .OrderBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    public async Task<TestCaseManifest> ImportFilesAsync(string name, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) throw new InvalidDataException("登録するファイルがありません。");
        foreach (string path in paths) ValidatePayload(path);
        string id = Guid.NewGuid().ToString("N");
        string caseDirectory = Path.Combine(_directory, id);
        Directory.CreateDirectory(caseDirectory);
        var copied = new List<string>();
        try
        {
            foreach (string source in paths)
            {
                string targetName = MakeUniqueFileName(caseDirectory, Path.GetFileName(source));
                await using FileStream input = File.OpenRead(source);
                await using FileStream output = File.Create(Path.Combine(caseDirectory, targetName));
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                copied.Add(targetName);
            }
            string referenceImage = copied.FirstOrDefault(static file =>
                file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            string[] payloads = copied.Where(static file =>
                !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToArray();
            var manifest = new TestCaseManifest(
                TestCaseManifest.CurrentSchemaVersion, id, string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
                "未分類", InferProvider(copied), InferTelegramType(paths), string.Empty, [], string.Empty,
                DateTimeOffset.UtcNow, payloads, referenceImage,
                new TestCaseExpectation(string.Empty, string.Empty, null, [], [], [], string.Empty, string.Empty));
            await WriteManifestAsync(caseDirectory, manifest, cancellationToken).ConfigureAwait(false);
            return manifest;
        }
        catch
        {
            if (Directory.Exists(caseDirectory)) Directory.Delete(caseDirectory, recursive: true);
            throw;
        }
    }

    public async Task<IReadOnlyList<TestCaseManifest>> ImportDmdataArchiveAsync(
        string telegramsIndexPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(telegramsIndexPath) || !File.Exists(telegramsIndexPath))
            throw new FileNotFoundException("dmdataのtelegrams.jsonを確認できません。", telegramsIndexPath);
        if (!Path.GetExtension(telegramsIndexPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("dmdataの索引にはtelegrams.jsonを指定してください。");

        string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(telegramsIndexPath))
            ?? throw new InvalidDataException("dmdata生データの保存場所を確認できません。");
        await using FileStream indexStream = File.OpenRead(telegramsIndexPath);
        using JsonDocument index = await JsonDocument.ParseAsync(indexStream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (index.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("telegrams.jsonのルートが配列ではありません。");

        DmdataIndexEntry[] entries = index.RootElement.EnumerateArray()
            .Select(ParseDmdataIndexEntry).ToArray();
        DmdataIndexEntry[] xmlEntries = entries.Where(static entry =>
            entry.Format.Equals("xml", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (xmlEntries.Length == 0)
            throw new InvalidDataException("telegrams.jsonにXML正本が登録されていません。");

        var jsonByOriginalId = entries.Where(static entry =>
                entry.Format.Equals("json", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entry.OriginalId))
            .GroupBy(static entry => entry.OriginalId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var imported = new List<TestCaseManifest>(xmlEntries.Length);
        var createdDirectories = new List<string>(xmlEntries.Length);
        try
        {
            foreach (DmdataIndexEntry xmlEntry in xmlEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string xmlSource = ResolveDmdataSourceFile(sourceDirectory, xmlEntry.FileName);
                ValidatePayload(xmlSource);
                DmdataIndexEntry? jsonEntry = null;
                string? jsonSource = null;
                if (!string.IsNullOrWhiteSpace(xmlEntry.Id) &&
                    jsonByOriginalId.TryGetValue(xmlEntry.Id, out DmdataIndexEntry? pairedJson))
                {
                    jsonEntry = pairedJson;
                    jsonSource = ResolveDmdataSourceFile(sourceDirectory, pairedJson.FileName);
                    ValidatePayload(jsonSource);
                }

                string id = Guid.NewGuid().ToString("N");
                string caseDirectory = Path.Combine(_directory, id);
                Directory.CreateDirectory(caseDirectory);
                createdDirectories.Add(caseDirectory);
                var copied = new List<string>(2);
                string xmlTarget = MakeUniqueFileName(caseDirectory, Path.GetFileName(xmlSource));
                await CopyFileAsync(xmlSource, Path.Combine(caseDirectory, xmlTarget), cancellationToken)
                    .ConfigureAwait(false);
                copied.Add(xmlTarget);
                if (jsonSource is not null)
                {
                    string jsonTarget = MakeUniqueFileName(caseDirectory, Path.GetFileName(jsonSource));
                    await CopyFileAsync(jsonSource, Path.Combine(caseDirectory, jsonTarget), cancellationToken)
                        .ConfigureAwait(false);
                    copied.Add(jsonTarget);
                }

                string[] tags = new[] { "dmdata", xmlEntry.Classification, xmlEntry.TelegramType }
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
                string description = jsonEntry is null
                    ? "dmdataの生データ索引から登録。XML正本だけを表示解析します。"
                    : "dmdataの生データ索引から登録。XML正本だけを表示解析し、対応する変換JSONは参考データとして保持します。";
                var manifest = new TestCaseManifest(
                    TestCaseManifest.CurrentSchemaVersion,
                    id,
                    BuildDmdataCaseName(xmlEntry),
                    string.IsNullOrWhiteSpace(xmlEntry.Classification) ? "dmdata" : xmlEntry.Classification,
                    DmdataTestProviderName,
                    xmlEntry.TelegramType,
                    xmlEntry.EventId,
                    tags,
                    description,
                    DateTimeOffset.UtcNow,
                    copied,
                    string.Empty,
                    new TestCaseExpectation(string.Empty, string.Empty, null, [], [], [], string.Empty, string.Empty));
                await WriteManifestAsync(caseDirectory, manifest, cancellationToken).ConfigureAwait(false);
                imported.Add(manifest);
            }

            return imported;
        }
        catch
        {
            foreach (string directory in createdDirectories)
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = GetCaseDirectory(id);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        TestCaseManifest[] cases = List().ToArray();
        foreach (TestCaseManifest item in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteAsync(item.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ExportAsync(string id, string zipPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string source = GetCaseDirectory(id);
        string temporary = Path.Combine(Path.GetTempPath(), $"qtcase-{Guid.NewGuid():N}.zip");
        try
        {
            ZipFile.CreateFromDirectory(source, temporary, CompressionLevel.Optimal, includeBaseDirectory: false);
            File.Copy(temporary, zipPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return Task.CompletedTask;
    }

    public async Task<TestCaseManifest> ImportPackageAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        string temporary = Path.Combine(Path.GetTempPath(), $"qtcase-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string destination = Path.GetFullPath(Path.Combine(temporary, entry.FullName));
                if (!destination.StartsWith(Path.GetFullPath(temporary) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("ZIP内に不正なパスがあります。");
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: false);
            }
            string manifestPath = Path.Combine(temporary, "manifest.json");
            TestCaseManifest original = TryReadManifest(manifestPath) ?? throw new InvalidDataException("manifest.jsonがありません。");
            foreach (string file in original.PayloadFiles)
                ValidatePayload(ResolveManagedFile(temporary, file));
            if (!string.IsNullOrWhiteSpace(original.ReferenceImageFile))
                ValidatePayload(ResolveManagedFile(temporary, original.ReferenceImageFile));
            string id = Guid.NewGuid().ToString("N");
            string destinationDirectory = GetCaseDirectory(id);
            Directory.Move(temporary, destinationDirectory);
            var imported = original with { Id = id, CreatedAtUtc = DateTimeOffset.UtcNow };
            await WriteManifestAsync(destinationDirectory, imported, cancellationToken).ConfigureAwait(false);
            return imported;
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }

    public async Task<TestCaseManifest> DuplicateAsync(string id, CancellationToken cancellationToken = default)
    {
        string sourceDirectory = GetCaseDirectory(id);
        TestCaseManifest source = TryReadManifest(Path.Combine(sourceDirectory, "manifest.json"))
            ?? throw new InvalidDataException("複製元のテストケースが壊れています。");
        string newId = Guid.NewGuid().ToString("N");
        string destinationDirectory = GetCaseDirectory(newId);
        CopyDirectory(sourceDirectory, destinationDirectory);
        var duplicated = source with
        {
            Id = newId,
            Name = source.Name + " のコピー",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await WriteManifestAsync(destinationDirectory, duplicated, cancellationToken).ConfigureAwait(false);
        return duplicated;
    }

    public async Task<TestCaseManifest> UpdateAsync(TestCaseManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != TestCaseManifest.CurrentSchemaVersion)
            throw new InvalidDataException("未対応のテストケース形式です。");
        string directory = GetCaseDirectory(manifest.Id);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("テストケースがありません。");
        TestCaseManifest normalized = NormalizeManifest(manifest);
        await WriteManifestAsync(directory, normalized, cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public IReadOnlyList<RawProviderMessage> LoadMessages(string id, SourceMode mode)
    {
        string directory = GetCaseDirectory(id);
        TestCaseManifest manifest = TryReadManifest(Path.Combine(directory, "manifest.json"))
            ?? throw new InvalidDataException("テストケースが壊れています。");
        string[] providerXml = manifest.PayloadFiles.Where(static file =>
            file.EndsWith(".provider.xml", StringComparison.OrdinalIgnoreCase)).ToArray();
        IEnumerable<string> selected;
        if (providerXml.Length > 0)
        {
            selected = providerXml;
        }
        else if (manifest.Provider.Equals(DmdataTestProviderName, StringComparison.OrdinalIgnoreCase))
        {
            selected = manifest.PayloadFiles.Where(static file =>
                file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            selected = manifest.PayloadFiles.Where(static file =>
                file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        }
        return selected.Select(file =>
        {
            string path = ResolveManagedFile(directory, file);
            string provider = Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)
                ? JmaXmlTestProviderName
                : manifest.Provider;
            return new RawProviderMessage(provider, File.ReadAllText(path), mode, DateTimeOffset.UtcNow)
            {
                ContentFormat = Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)
                    ? RawProviderContentFormat.JmaXml
                    : RawProviderContentFormat.Json,
            };
        }).ToArray();
    }

    private Task WriteManifestAsync(string directory, TestCaseManifest manifest, CancellationToken cancellationToken) =>
        AtomicFileWriter.WriteAsync(Path.Combine(directory, "manifest.json"),
            (stream, token) => JsonSerializer.SerializeAsync(stream, manifest, _json, token), cancellationToken);

    private TestCaseManifest? TryReadManifest(string path)
    {
        try
        {
            TestCaseManifest? value = JsonSerializer.Deserialize<TestCaseManifest>(File.ReadAllText(path), _json);
            return value?.SchemaVersion == TestCaseManifest.CurrentSchemaVersion
                ? NormalizeManifest(value)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static void ValidatePayload(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".xml")
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using XmlReader reader = XmlReader.Create(path, settings);
            _ = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            return;
        }
        if (extension == ".json") { using JsonDocument _ = JsonDocument.Parse(File.ReadAllText(path)); return; }
        if (extension == ".png") return;
        throw new InvalidDataException($"未対応のテストファイルです: {Path.GetFileName(path)}");
    }

    private string GetCaseDirectory(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new InvalidDataException("テストケースIDが不正です。");
        return Path.Combine(_directory, id);
    }

    private static string InferProvider(IReadOnlyList<string> files)
    {
        if (files.Any(static file => file.EndsWith(".transport.json", StringComparison.OrdinalIgnoreCase)))
            return "axis";
        if (files.Any(static file => file.Equals("telegrams.json", StringComparison.OrdinalIgnoreCase) ||
            file.Contains("dmdata", StringComparison.OrdinalIgnoreCase)))
            return DmdataTestProviderName;
        return JmaXmlTestProviderName;
    }

    private static TestCaseManifest NormalizeManifest(TestCaseManifest manifest) => manifest with
    {
        Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name.Trim(),
        Category = string.IsNullOrWhiteSpace(manifest.Category) ? "未分類" : manifest.Category.Trim(),
        Provider = string.IsNullOrWhiteSpace(manifest.Provider) ? JmaXmlTestProviderName : manifest.Provider.Trim(),
        TelegramType = NormalizeTelegramType(manifest.TelegramType, manifest.PayloadFiles),
        EventId = manifest.EventId?.Trim() ?? string.Empty,
        Tags = manifest.Tags?.Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray() ?? [],
        Description = manifest.Description?.Trim() ?? string.Empty,
        PayloadFiles = manifest.PayloadFiles ?? [],
        ReferenceImageFile = manifest.ReferenceImageFile ?? string.Empty,
        Expectation = manifest.Expectation is null
            ? new TestCaseExpectation(string.Empty, string.Empty, null, [], [], [], string.Empty, string.Empty)
            : manifest.Expectation with
            {
                RequiredBadges = manifest.Expectation.RequiredBadges ?? [],
                RequiredTextFragments = manifest.Expectation.RequiredTextFragments ?? [],
                RequiredAreas = manifest.Expectation.RequiredAreas ?? [],
            },
    };

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
    }

    private static string InferTelegramType(IReadOnlyList<string> paths)
    {
        foreach (string path in paths.Where(static path => Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                string? fileCode = Path.GetFileName(path).Split(
                        ['_', '.'],
                        StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(IsJmaTelegramTypeToken);
                if (!string.IsNullOrWhiteSpace(fileCode)) return fileCode.ToUpperInvariant();
                XDocument document = XDocument.Load(path);
                string? type = document.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName is "Title" or "ReportDateTime")?.Parent?
                    .Elements().FirstOrDefault(element => element.Name.LocalName == "Title")?.Value;
                if (!string.IsNullOrWhiteSpace(type)) return type.Trim();
            }
            catch { }
        }
        return string.Empty;
    }

    private static bool IsJmaTelegramTypeToken(string value) =>
        value.Length == 6 &&
        value[0] is 'V' or 'v' &&
        value[1..].All(char.IsAsciiLetterOrDigit);

    private static string NormalizeTelegramType(
        string? telegramType,
        IReadOnlyList<string>? payloadFiles)
    {
        string normalized = telegramType?.Trim() ?? string.Empty;
        if (IsJmaTelegramTypeToken(normalized))
        {
            return normalized.ToUpperInvariant();
        }

        foreach (string file in payloadFiles ?? [])
        {
            string? fileCode = Path.GetFileName(file).Split(
                    ['_', '.'],
                    StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(IsJmaTelegramTypeToken);
            if (!string.IsNullOrWhiteSpace(fileCode))
            {
                return fileCode.ToUpperInvariant();
            }
        }

        return normalized;
    }

    private static string MakeUniqueFileName(string directory, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string candidate = fileName;
        for (int index = 2; File.Exists(Path.Combine(directory, candidate)); index++) candidate = $"{stem}-{index}{extension}";
        return candidate;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using FileStream input = File.OpenRead(source);
        await using FileStream output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static DmdataIndexEntry ParseDmdataIndexEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("telegrams.jsonにオブジェクト以外の項目があります。");
        string format = GetJsonString(element, "format");
        string fileName = GetJsonString(element, "filename");
        if (string.IsNullOrWhiteSpace(format) || string.IsNullOrWhiteSpace(fileName))
            throw new InvalidDataException("telegrams.jsonにformatまたはfilenameがない項目があります。");
        JsonElement head = GetJsonObject(element, "head");
        JsonElement xmlReport = GetJsonObject(element, "xmlReport");
        JsonElement xmlHead = GetJsonObject(xmlReport, "head");
        return new DmdataIndexEntry(
            GetJsonString(element, "id"),
            GetJsonString(element, "originalId"),
            format,
            fileName,
            GetJsonString(element, "classification"),
            GetJsonString(head, "type"),
            GetJsonString(xmlHead, "eventId"),
            GetJsonString(xmlHead, "serial"));
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out JsonElement value))
            return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => string.Empty,
        };
    }

    private static JsonElement GetJsonObject(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string ResolveDmdataSourceFile(string sourceDirectory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName) ||
            !Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal))
            throw new InvalidDataException($"telegrams.jsonに不正なファイル名があります: {fileName}");
        string root = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidDataException($"dmdata生データの参照先を確認できません: {fileName}");
        return path;
    }

    private static string BuildDmdataCaseName(DmdataIndexEntry entry)
    {
        string report = string.IsNullOrWhiteSpace(entry.Serial) ? string.Empty : $" 第{entry.Serial}報";
        string identity = string.IsNullOrWhiteSpace(entry.EventId)
            ? Path.GetFileNameWithoutExtension(entry.FileName)
            : entry.EventId;
        return $"{entry.TelegramType} {identity}{report}".Trim();
    }

    private sealed record DmdataIndexEntry(
        string Id,
        string OriginalId,
        string Format,
        string FileName,
        string Classification,
        string TelegramType,
        string EventId,
        string Serial);

    private static string ResolveManagedFile(string directory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("テストケース内のファイル名が不正です。");
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidDataException($"テストケース内のファイルを確認できません: {relativePath}");
        return path;
    }
}
