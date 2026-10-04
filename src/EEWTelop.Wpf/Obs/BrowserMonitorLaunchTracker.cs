namespace EEWTelop.Wpf.Obs;

internal sealed class BrowserMonitorLaunchTracker
{
    private string _lastOpenedUrl = string.Empty;

    // Scenario steps and repeated telegrams must not open a new window each
    // time. The manual button always lets the operator reopen a closed monitor.
    public bool ShouldOpen(string url, bool automatic) =>
        !string.IsNullOrWhiteSpace(url) &&
        (!automatic || !string.Equals(url, _lastOpenedUrl, StringComparison.Ordinal));

    public void MarkOpened(string url) => _lastOpenedUrl = url;
}
