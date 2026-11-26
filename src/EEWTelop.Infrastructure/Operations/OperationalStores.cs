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
        string telegramType = string.Empty;
        string eventId = string.Empty;
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            XDocument? document = ValidatePayload(path);
            if (document is not null && telegramType.Length == 0)
            {
                XElement? control = ReadReportElement(document.Root, "Control");
                XElement? head = ReadReportElement(document.Root, "Head");
                string? title = ReadReportElement(control, "Title")?.Value ?? ReadReportElement(head, "Title")?.Value;
                telegramType = NormalizeTelegramType(title, [path]);
                eventId = ReadReportElement(head, "EventID")?.Value.Trim() ?? string.Empty;
            }
        }
        string id = Guid.NewGuid().ToString("N");
        string caseDirectory = Path.Combine(_directory, id);
        Directory.CreateDirectory(caseDirectory);
        var copied = new List<string>();
        try
        {
            foreach (string source in paths)
            {
                string targetName = MakeUniqueFileName(caseDirectory, Path.GetFileName(source));
                await CopyFileAsync(source, Path.Combine(caseDirectory, targetName), cancellationToken).ConfigureAwait(false);
                copied.Add(targetName);
            }
            var manifest = new TestCaseManifest(
                TestCaseManifest.CurrentSchemaVersion, id, string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
                TestCaseClassification.GetLabel(TestCaseClassification.GetKind(telegramType)), InferProvider(copied),
                telegramType, eventId, [], string.Empty,
                DateTimeOffset.UtcNow, copied,
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

    public async Task<TestXmlFolderImportResult> ImportXmlFolderAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string root = Path.GetFullPath(directory);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };
        // Take the list before writing any cases so the import cannot pick up its own copies.
        var files = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*.xml", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(file);
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);

        int imported = 0;
        var skipped = new List<string>();
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ImportFilesAsync(Path.GetFileNameWithoutExtension(file), [file], cancellationToken).ConfigureAwait(false);
                imported++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or XmlException)
            {
                skipped.Add(Path.GetRelativePath(root, file));
            }
        }

        return new TestXmlFolderImportResult(imported, skipped);
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

    private static XDocument? ValidatePayload(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".xml")
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using XmlReader reader = XmlReader.Create(path, settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        if (extension == ".json") { using JsonDocument _ = JsonDocument.Parse(File.ReadAllText(path)); return null; }
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

    private static TestCaseManifest NormalizeManifest(TestCaseManifest manifest)
    {
        string[] payloadFiles = (manifest.PayloadFiles ?? []).Where(IsTelegramFile).ToArray();
        string telegramType = NormalizeTelegramType(manifest.TelegramType, payloadFiles);
        string? category = manifest.Category?.Trim();
        return manifest with
        {
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name.Trim(),
            Category = string.IsNullOrEmpty(category) || category == TestCaseClassification.Unclassified
                ? TestCaseClassification.GetLabel(TestCaseClassification.GetKind(telegramType)) : category,
            Provider = string.IsNullOrWhiteSpace(manifest.Provider) ? JmaXmlTestProviderName : manifest.Provider.Trim(),
            TelegramType = telegramType,
            EventId = manifest.EventId?.Trim() ?? string.Empty,
            Tags = manifest.Tags?.Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray() ?? [],
            Description = manifest.Description?.Trim() ?? string.Empty,
            PayloadFiles = payloadFiles,
            Expectation = manifest.Expectation is null
                ? new TestCaseExpectation(string.Empty, string.Empty, null, [], [], [], string.Empty, string.Empty)
                : manifest.Expectation with
                {
                    RequiredBadges = manifest.Expectation.RequiredBadges ?? [],
                    RequiredTextFragments = manifest.Expectation.RequiredTextFragments ?? [],
                    RequiredAreas = manifest.Expectation.RequiredAreas ?? [],
                },
        };
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(IsTelegramFile))
            File.Copy(file, file.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTelegramFile(string? file) => file is not null &&
        (file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

    private static XElement? ReadReportElement(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(element => element.Name.LocalName == name);

    private static bool IsJmaTelegramTypeToken(string value) =>
        value.Length == 6 &&
        value[0] is 'V' or 'v' &&
        char.IsAsciiLetter(value[1]) && char.IsAsciiLetter(value[2]) && char.IsAsciiLetter(value[3]) &&
        char.IsAsciiDigit(value[4]) && char.IsAsciiDigit(value[5]);

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
