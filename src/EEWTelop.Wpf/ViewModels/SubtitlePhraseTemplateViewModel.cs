using System.Collections.ObjectModel;
using EEWTelop.Application.Display;
using EEWTelop.Wpf.Mvvm;

namespace EEWTelop.Wpf.ViewModels;

public sealed class SubtitlePhraseTemplateViewModel
{
    public SubtitlePhraseTemplateViewModel(
        IReadOnlyDictionary<string, string>? overrides)
    {
        foreach (SubtitlePhraseDefinition definition in SubtitlePhraseCatalog.All)
        {
            string value = overrides is not null && overrides.TryGetValue(definition.Id, out string? custom)
                ? custom ?? string.Empty
                : definition.DefaultText;
            Phrases.Add(new EditableSubtitlePhraseViewModel(definition, value));
        }
    }

    public ObservableCollection<EditableSubtitlePhraseViewModel> Phrases { get; } = [];

    public IReadOnlyDictionary<string, string> BuildOverrides() => Phrases
        .Where(static phrase => !string.Equals(
            phrase.Text,
            phrase.DefaultText,
            StringComparison.Ordinal))
        .ToDictionary(
            static phrase => phrase.Id,
            static phrase => phrase.Text,
            StringComparer.Ordinal);

    public void ResetAll()
    {
        foreach (EditableSubtitlePhraseViewModel phrase in Phrases)
        {
            phrase.Reset();
        }
    }
}

public sealed class EditableSubtitlePhraseViewModel : ObservableObject
{
    private string _text;

    public EditableSubtitlePhraseViewModel(
        SubtitlePhraseDefinition definition,
        string text)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Id = definition.Id;
        Category = definition.Category;
        Label = definition.Label;
        DefaultText = definition.DefaultText;
        _text = text;
    }

    public string Id { get; }

    public string Category { get; }

    public string Label { get; }

    public string DefaultText { get; }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value ?? string.Empty);
    }

    public void Reset() => Text = DefaultText;
}
