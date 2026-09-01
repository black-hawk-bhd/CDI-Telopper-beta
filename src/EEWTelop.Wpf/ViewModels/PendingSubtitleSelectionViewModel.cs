using System.Collections.ObjectModel;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Mvvm;

namespace EEWTelop.Wpf.ViewModels;

public sealed class PendingSubtitleSelectionViewModel : ObservableObject
{
    private PendingSubtitleItemViewModel? _selectedItem;

    public PendingSubtitleSelectionViewModel(IEnumerable<DisplayProgram> programs)
    {
        ArgumentNullException.ThrowIfNull(programs);
        foreach (DisplayProgram program in programs)
        {
            Items.Add(new PendingSubtitleItemViewModel(program));
        }

        SelectedItem = Items.FirstOrDefault();
    }

    public ObservableCollection<PendingSubtitleItemViewModel> Items { get; } = [];

    public PendingSubtitleItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }
}

public sealed class PendingSubtitleItemViewModel
{
    public PendingSubtitleItemViewModel(DisplayProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        Program = program;
        string summary = program.Pages
            .Select(static page => page.AccessibleText)
            .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text)) ?? "（本文なし）";
        summary = summary.ReplaceLineEndings(" ").Trim();
        if (summary.Length > 55)
        {
            summary = $"{summary[..55]}…";
        }

        Label = $"{GetKindLabel(program.Kind)}　{program.IssuedAt.ToLocalTime():HH:mm:ss}　{summary}";
    }

    public DisplayProgram Program { get; }

    public string Label { get; }

    private static string GetKindLabel(EventKind kind) => kind switch
    {
        EventKind.Eew => "緊急地震速報",
        EventKind.Quake => "地震情報",
        EventKind.Tsunami => "津波情報",
        EventKind.WeatherWarning => "気象情報",
        EventKind.Volcano => "火山情報",
        _ => "その他の情報",
    };
}
