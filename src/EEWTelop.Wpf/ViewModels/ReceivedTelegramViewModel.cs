using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.ViewModels;

public sealed class ReceivedTelegramViewModel
{
    public ReceivedTelegramViewModel(DisasterEvent disasterEvent, DisplayProgram program)
    {
        Event = disasterEvent;
        Program = program;
    }

    public DisasterEvent Event { get; }

    public DisplayProgram Program { get; }

    public string TelegramType => Event switch
    {
        EewEvent eew => eew.Issue.RawType,
        QuakeEvent quake => quake.Issue.RawType,
        TsunamiEvent tsunami => tsunami.Issue.RawType,
        WeatherWarningEvent weather => weather.Issue.RawType,
        VolcanoEvent volcano => volcano.Issue.RawType,
        _ => string.Empty,
    };

    public string SourceText => Event.SourceMode switch
    {
        SourceMode.Production => "本番受信",
        SourceMode.HistoryRehearsal => "過去電文",
        SourceMode.Sandbox => "テスト",
        _ => Event.SourceMode.ToString(),
    };

    public string DisplayText =>
        $"{Event.ReceivedAt.ToLocalTime():MM/dd HH:mm:ss}  {SourceText}  " +
        $"{GetKindText(Event.Kind)}  {TelegramType}  {Program.Pages.Count}ページ";

    public string DetailText => string.Join(
        Environment.NewLine,
        $"区分: {SourceText}",
        $"情報種別: {GetKindText(Event.Kind)}",
        $"電文コード: {(string.IsNullOrWhiteSpace(TelegramType) ? "—" : TelegramType)}",
        $"発表時刻: {Event.IssuedAt.ToLocalTime():yyyy/MM/dd HH:mm:ss}",
        $"受信時刻: {Event.ReceivedAt.ToLocalTime():yyyy/MM/dd HH:mm:ss}",
        $"提供元: {Event.Provider}",
        $"イベントID: {Event.Id.Value}",
        $"状態: {(Event.IsCancelled ? "解除・取消" : Event.IsCorrection ? "訂正" : "通常")}");

    public IReadOnlyList<TelegramPageReviewViewModel> Pages => Program.Pages
        .Select((page, index) => new TelegramPageReviewViewModel(
            $"ページ {index + 1} / {Program.Pages.Count}",
            page.AccessibleText))
        .ToArray();

    private static string GetKindText(EventKind kind) => kind switch
    {
        EventKind.Eew => "緊急地震速報",
        EventKind.Quake => "地震情報",
        EventKind.Tsunami => "津波情報",
        EventKind.WeatherWarning => "気象情報",
        EventKind.Volcano => "火山情報",
        _ => kind.ToString(),
    };
}

public sealed record TelegramPageReviewViewModel(string Header, string Text);
