using System.Collections.ObjectModel;
using EEWTelop.Application.Display;
using EEWTelop.Wpf.Mvvm;

namespace EEWTelop.Wpf.ViewModels;

public sealed class SubtitleEditorViewModel : ObservableObject
{
    private readonly DisplayProgram _sourceProgram;
    private EditableSubtitlePageViewModel? _selectedPage;

    public SubtitleEditorViewModel(DisplayProgram sourceProgram)
        : this(sourceProgram, sourceProgram)
    {
    }

    public SubtitleEditorViewModel(
        DisplayProgram sourceProgram,
        DisplayProgram displayedProgram)
    {
        ArgumentNullException.ThrowIfNull(sourceProgram);
        ArgumentNullException.ThrowIfNull(displayedProgram);
        _sourceProgram = sourceProgram;
        LoadPages(displayedProgram);
    }

    public ObservableCollection<EditableSubtitlePageViewModel> Pages { get; } = [];

    public EditableSubtitlePageViewModel? SelectedPage
    {
        get => _selectedPage;
        set => SetProperty(ref _selectedPage, value);
    }

    public string ProgramDescription =>
        $"{_sourceProgram.Kind} / {_sourceProgram.Pages.Count}ページ";

    public DisplayProgram BuildEditedProgram()
    {
        DisplayPage[] pages = Pages
            .OrderBy(static page => page.Index)
            .Select(static page => page.BuildPage())
            .ToArray();
        return _sourceProgram with { Pages = pages };
    }

    public void Reset()
    {
        LoadPages(_sourceProgram);
    }

    private void LoadPages(DisplayProgram program)
    {
        Pages.Clear();
        foreach (DisplayPage page in program.Pages.OrderBy(static page => page.Index))
        {
            Pages.Add(new EditableSubtitlePageViewModel(page));
        }

        SelectedPage = Pages.FirstOrDefault();
    }
}

public sealed class EditableSubtitlePageViewModel : ObservableObject
{
    private readonly DisplayPage _sourcePage;

    public EditableSubtitlePageViewModel(DisplayPage sourcePage)
    {
        ArgumentNullException.ThrowIfNull(sourcePage);
        _sourcePage = sourcePage;
        foreach (DisplayBlock block in sourcePage.Blocks)
        {
            Blocks.Add(new EditableSubtitleBlockViewModel(block));
        }
    }

    public int Index => _sourcePage.Index;

    public string Label => $"ページ {Index + 1}";

    public ObservableCollection<EditableSubtitleBlockViewModel> Blocks { get; } = [];

    public DisplayPage BuildPage()
    {
        DisplayBlock[] blocks = Blocks.Select(static block => block.BuildBlock()).ToArray();
        string accessibleText = string.Join(
            Environment.NewLine,
            blocks.SelectMany(static block => new[]
                {
                    block.Badge,
                    block.PrimaryText,
                    block.SecondaryText,
                })
                .Where(static text => !string.IsNullOrWhiteSpace(text)));
        return _sourcePage with
        {
            Blocks = blocks,
            AccessibleText = accessibleText,
        };
    }
}

public sealed class EditableSubtitleBlockViewModel : ObservableObject
{
    private readonly DisplayBlock _sourceBlock;
    private string _badge;
    private string _primaryText;
    private string _secondaryText;

    public EditableSubtitleBlockViewModel(DisplayBlock sourceBlock)
    {
        ArgumentNullException.ThrowIfNull(sourceBlock);
        _sourceBlock = sourceBlock;
        _badge = sourceBlock.Badge;
        _primaryText = sourceBlock.PrimaryText;
        _secondaryText = sourceBlock.SecondaryText;
    }

    public string Badge
    {
        get => _badge;
        set => SetProperty(ref _badge, value ?? string.Empty);
    }

    public string PrimaryText
    {
        get => _primaryText;
        set => SetProperty(ref _primaryText, value ?? string.Empty);
    }

    public string SecondaryText
    {
        get => _secondaryText;
        set => SetProperty(ref _secondaryText, value ?? string.Empty);
    }

    public string StyleDescription => _sourceBlock.StyleToken;

    public bool IsEditable => _sourceBlock.StyleToken != DisplayStyleTokens.PageIndicator;

    public DisplayBlock BuildBlock() => _sourceBlock with
    {
        Badge = IsEditable ? Badge : _sourceBlock.Badge,
        PrimaryText = IsEditable ? PrimaryText : _sourceBlock.PrimaryText,
        SecondaryText = IsEditable ? SecondaryText : _sourceBlock.SecondaryText,
    };
}
