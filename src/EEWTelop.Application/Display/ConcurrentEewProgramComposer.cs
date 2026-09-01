using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

/// <summary>
/// Keeps independently issued EEW warnings visible at the same time.
/// Continued reports update their existing slot because the provider event ID is stable.
/// </summary>
public sealed class ConcurrentEewProgramComposer
{
    private const int MaximumVisibleWarnings = 2;
    private readonly object _gate = new();
    private readonly Dictionary<ActiveEewKey, ActiveEew> _activeWarnings = [];

    public DisplayProgram Compose(
        EewEvent eew,
        DisplayProgram singleProgram,
        DisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(eew);
        ArgumentNullException.ThrowIfNull(singleProgram);
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            PurgeExpired(eew.ReceivedAt, settings.EffectiveEewAutoHideSeconds);

            var key = new ActiveEewKey(eew.SourceMode, eew.Id.Value);
            if (eew.IsCancelled)
            {
                // Keep the cancellation card in the same event slot for the
                // configured EEW display period. Other event slots are untouched.
                _activeWarnings[key] = new ActiveEew(eew, singleProgram);
                TrimSource(eew.SourceMode);
                ActiveEew[] visibleAfterCancellation = GetVisible(eew.SourceMode);
                return visibleAfterCancellation.Length < 2
                    ? singleProgram
                    : Combine(eew.SourceMode, visibleAfterCancellation);
            }

            if (!eew.IsWarning)
            {
                return singleProgram;
            }

            _activeWarnings[key] = new ActiveEew(eew, singleProgram);
            TrimSource(eew.SourceMode);

            ActiveEew[] visible = GetVisible(eew.SourceMode);

            return visible.Length < 2
                ? singleProgram
                : Combine(eew.SourceMode, visible);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _activeWarnings.Clear();
        }
    }

    private void PurgeExpired(DateTimeOffset receivedAt, int autoHideSeconds)
    {
        if (autoHideSeconds <= 0)
        {
            return;
        }

        DateTimeOffset threshold = receivedAt.AddSeconds(-autoHideSeconds);
        ActiveEewKey[] expired = _activeWarnings
            .Where(pair => pair.Value.Event.ReceivedAt <= threshold)
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (ActiveEewKey key in expired)
        {
            _activeWarnings.Remove(key);
        }
    }

    private void TrimSource(SourceMode sourceMode)
    {
        ActiveEewKey[] obsolete = _activeWarnings
            .Where(pair => pair.Key.SourceMode == sourceMode)
            .OrderByDescending(static pair => pair.Value.Event.IssuedAt)
            .ThenByDescending(static pair => pair.Value.Event.ReceivedAt)
            .Skip(MaximumVisibleWarnings)
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (ActiveEewKey key in obsolete)
        {
            _activeWarnings.Remove(key);
        }
    }

    private ActiveEew[] GetVisible(SourceMode sourceMode) => _activeWarnings
        .Where(pair => pair.Key.SourceMode == sourceMode)
        .Select(static pair => pair.Value)
        .OrderBy(static item => item.Event.IssuedAt)
        .ThenBy(static item => item.Event.ReceivedAt)
        .TakeLast(MaximumVisibleWarnings)
        .ToArray();

    private static DisplayProgram Combine(
        SourceMode sourceMode,
        IReadOnlyList<ActiveEew> visible)
    {
        var blocks = new List<DisplayBlock>();
        foreach (ActiveEew active in visible)
        {
            DisplayPage page = active.Program.Pages[0];
            foreach (DisplayBlock block in page.Blocks)
            {
                if (block.StyleToken != DisplayStyleTokens.PageIndicator)
                {
                    blocks.Add(block);
                }
            }
        }

        string accessibleText = string.Join(
            "。",
            blocks
                .SelectMany(static block => new[] { block.Badge, block.PrimaryText, block.SecondaryText })
                .Where(static text => !string.IsNullOrWhiteSpace(text)));
        ActiveEew newest = visible
            .OrderByDescending(static item => item.Event.IssuedAt)
            .ThenByDescending(static item => item.Event.ReceivedAt)
            .First();
        string programSignature = string.Join("|", visible.Select(static item => item.Program.ProgramId));

        return new DisplayProgram(
            ProgramId: $"concurrent-eew:{(int)sourceMode}:{programSignature}",
            EventId: EventId.Create($"concurrent-eew-{(int)sourceMode}"),
            Kind: EventKind.Eew,
            SourceMode: sourceMode,
            IssuedAt: newest.Event.IssuedAt,
            Priority: OverlayPriority.Eew,
            Pages: [new DisplayPage(1, blocks, accessibleText, null)],
            StartedAtUtc: newest.Program.StartedAtUtc,
            EndPolicy: EndPolicy.AutoHide,
            RehearsalLabel: newest.Program.RehearsalLabel);
    }

    private readonly record struct ActiveEewKey(SourceMode SourceMode, string EventId);

    private sealed record ActiveEew(EewEvent Event, DisplayProgram Program);
}
