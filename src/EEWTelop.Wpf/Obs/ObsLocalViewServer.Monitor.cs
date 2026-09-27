using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EEWTelop.Wpf.Obs;

public sealed partial class ObsLocalViewServer
{
    private static readonly JsonSerializerOptions MonitorJsonOptions = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
    public string MonitorUrl => IsRunning ? $"http://127.0.0.1:{Port}/monitor/?token={_token}" : string.Empty;

    private async Task HandleMonitorAsync(NetworkStream stream, Uri uri, CancellationToken ct)
    {
        string path = uri.AbsolutePath;
        if (path is "/monitor" or "/monitor/" or "/monitor/view")
        {
            string resource = path == "/monitor/view" ? HtmlResource : "EEWTelop.Wpf.Obs.Assets.monitor.html";
            await WriteResponseAsync(stream, 200, "text/html; charset=utf-8", ReadResource(resource), ct,
                securityPolicy: true, monitorFrames: true).ConfigureAwait(false);
            return;
        }
        if (path == "/monitor/script.js")
        {
            await WriteResponseAsync(stream, 200, "text/javascript; charset=utf-8",
                ReadResource("EEWTelop.Wpf.Obs.Assets.monitor.js"), ct).ConfigureAwait(false);
            return;
        }
        object? result = null;
        if (path == "/monitor/data") result = new
        {
            serverTime = _clock.UtcNow,
            connection = _receptionService?.Connection.State.ToString() ?? "Unavailable",
            channels = _snapshotStore.ReadMonitorChannels(_clock.UtcNow),
            telegrams = _snapshotStore.ReadMonitorList(),
        };
        if (path == "/monitor/item" && TryGetQueryParameter(uri.Query, "id", out string id) &&
            long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long number))
            result = _snapshotStore.ReadMonitorTelegram(number, _clock.UtcNow);
        await WriteResponseAsync(stream, result is null ? 404 : 200, "application/json; charset=utf-8",
            JsonSerializer.Serialize(result, MonitorJsonOptions), ct).ConfigureAwait(false);
    }
}
