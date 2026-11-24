using EEWTelop.Infrastructure.Persistence;

namespace EEWTelop.Infrastructure.Settings;

public sealed class DataDirectorySelection
{
    private const string SettingsFileName = "settings.json";
    private static readonly string[] SettingsFiles =
        ["dmdata-oauth.json", "external-api-key.dpapi", SettingsFileName];

    private readonly string _appDataDirectory;
    private readonly string _portableDirectory;
    private readonly string _markerPath;

    public DataDirectorySelection(string appDataDirectory, string programDirectory, string? overrideDirectory = null)
    {
        _appDataDirectory = Path.GetFullPath(appDataDirectory);
        _portableDirectory = Path.Combine(Path.GetFullPath(programDirectory), "Settings");
        _markerPath = Path.Combine(_portableDirectory, ".portable");
        CanChange = string.IsNullOrWhiteSpace(overrideDirectory);
        ActiveDirectory = CanChange
            ? UsePortableOnNextLaunch ? _portableDirectory : _appDataDirectory
            : Path.GetFullPath(overrideDirectory!);
    }

    public string ActiveDirectory { get; }
    public bool CanChange { get; }
    public bool UsePortableOnNextLaunch => File.Exists(_markerPath);

    public async Task SelectForNextLaunchAsync(bool usePortable, CancellationToken cancellationToken = default)
    {
        if (!CanChange)
        {
            throw new InvalidOperationException("The data directory was explicitly overridden.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string destination = usePortable ? _portableDirectory : _appDataDirectory;
        Directory.CreateDirectory(destination);
        // Verify the destination can be written before committing the selection, even if it already contains settings.
        using (File.Create(Path.Combine(destination, $".write-test-{Guid.NewGuid():N}"), 1, FileOptions.DeleteOnClose))
        {
        }

        if (!File.Exists(Path.Combine(destination, SettingsFileName)))
        {
            foreach (string name in SettingsFiles)
            {
                await CopySavedFileAsync(Path.Combine(ActiveDirectory, name), Path.Combine(destination, name),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (usePortable)
        {
            await AtomicFileWriter.WriteAsync(_markerPath, (_, _) => Task.CompletedTask, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (UsePortableOnNextLaunch)
        {
            File.Delete(_markerPath);
        }
    }

    private static async Task CopySavedFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (!File.Exists(source) || File.Exists(destination))
        {
            return;
        }

        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            bufferSize: 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            await AtomicFileWriter.WriteAsync(destination, (output, token) => input.CopyToAsync(output, token),
                cancellationToken, overwrite: false).ConfigureAwait(false);
        }
        catch (IOException) when (File.Exists(destination))
        {
            // Another writer created the destination during the copy; keep that file.
        }
    }
}
