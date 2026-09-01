using System.Collections.ObjectModel;
using System.Windows;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Mvvm;

namespace EEWTelop.Wpf.ViewModels;

public sealed class OverlayViewModel : ObservableObject
{
    private bool _hasProgram;
    private string _programId = "";
    private string _rehearsalLabel = "";
    private string _pageIndicator = "";
    private string _accessibleText = "";
    private BackgroundMode _backgroundMode;
    private double _fontScale = 1;
    private double _lineSpacing = 1;
    private double _letterSpacingEm;
    private bool _isEewProgram;
    private bool _isConcurrentEewProgram;
    private bool _isSubtitleProgram;
    private OutputTransformSettings _outputTransform = OutputTransformSettings.Default;

    public ObservableCollection<OverlayBlockViewModel> Blocks { get; } = [];

    public bool HasProgram { get => _hasProgram; private set => SetProperty(ref _hasProgram, value); }

    public bool HasRehearsalLabel => !string.IsNullOrWhiteSpace(RehearsalLabel);

    public string ProgramId { get => _programId; private set => SetProperty(ref _programId, value); }

    public string RehearsalLabel
    {
        get => _rehearsalLabel;
        private set
        {
            if (SetProperty(ref _rehearsalLabel, value))
            {
                OnPropertyChanged(nameof(HasRehearsalLabel));
            }
        }
    }

    public bool HasPageIndicator => !string.IsNullOrWhiteSpace(PageIndicator);

    public string PageIndicator
    {
        get => _pageIndicator;
        private set
        {
            if (SetProperty(ref _pageIndicator, value))
            {
                OnPropertyChanged(nameof(HasPageIndicator));
            }
        }
    }

    public string AccessibleText { get => _accessibleText; private set => SetProperty(ref _accessibleText, value); }

    public BackgroundMode BackgroundMode { get => _backgroundMode; private set => SetProperty(ref _backgroundMode, value); }

    public double FontScale
    {
        get => _fontScale;
        private set
        {
            if (SetProperty(ref _fontScale, value))
            {
                RaiseTypographyProperties();
            }
        }
    }

    public double LineSpacing
    {
        get => _lineSpacing;
        private set
        {
            if (SetProperty(ref _lineSpacing, value))
            {
                RaiseTypographyProperties();
            }
        }
    }

    public double LetterSpacingEm
    {
        get => _letterSpacingEm;
        private set
        {
            if (SetProperty(ref _letterSpacingEm, value))
            {
                RaiseTypographyProperties();
            }
        }
    }

    public bool IsEewProgram { get => _isEewProgram; private set => SetProperty(ref _isEewProgram, value); }

    public bool IsConcurrentEewProgram
    {
        get => _isConcurrentEewProgram;
        private set
        {
            if (SetProperty(ref _isConcurrentEewProgram, value))
            {
                RaiseTypographyProperties();
            }
        }
    }

    public bool IsSubtitleProgram { get => _isSubtitleProgram; private set => SetProperty(ref _isSubtitleProgram, value); }

    public double OutputScale => OutputTransform.Scale;

    public double OutputOffsetX => OutputTransform.OffsetX;

    public double OutputOffsetY => OutputTransform.OffsetY;

    public Rect OutputClipRect => new(
        OutputTransform.CropLeft,
        OutputTransform.CropTop,
        Math.Max(1, 1920 - OutputTransform.CropLeft - OutputTransform.CropRight),
        Math.Max(1, 1080 - OutputTransform.CropTop - OutputTransform.CropBottom));

    public double SubtitleFontSize => 58 * FontScale;

    public double SubtitleNoteFontSize => SubtitleFontSize * 0.6;

    public double SubtitleLineHeight => SubtitleFontSize * 1.25 * LineSpacing;

    public double SubtitleNoteLineHeight => SubtitleNoteFontSize * 1.25 * LineSpacing;

    public double SubtitleLetterSpacing => SubtitleFontSize * (0.02 + LetterSpacingEm);

    public double StrokeThickness => 6 * FontScale;

    public double BadgeFontSize => 52 * FontScale;

    public double BadgeMinWidth => BadgeFontSize * 2.4;

    public Thickness BadgePadding => new(BadgeFontSize * 0.5, BadgeFontSize * 0.12, BadgeFontSize * 0.5, BadgeFontSize * 0.12);

    public double PageFontSize => 26 * FontScale;

    public Thickness PagePadding => new(PageFontSize * 0.5, PageFontSize * 0.12, PageFontSize * 0.5, PageFontSize * 0.12);

    public double BannerFontSize => 52 * FontScale;

    public double BannerLetterSpacing => BannerFontSize * (0.04 + LetterSpacingEm);

    public double BannerLineHeight => BannerFontSize * 1.1 * LineSpacing;

    public Thickness BannerPadding => new(BannerFontSize * 0.55, BannerFontSize * 0.16, BannerFontSize * 0.55, BannerFontSize * 0.16);

    public double EewHeaderFontSize => (IsConcurrentEewProgram ? 50 : 76) * FontScale;

    public double EewBodyFontSize => (IsConcurrentEewProgram ? 38 : 58) * FontScale;

    public double EewDetailFontSize => (IsConcurrentEewProgram ? 24 : 30) * FontScale;

    public void Apply(CoordinatorSnapshot snapshot, DisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        BackgroundMode = settings.BackgroundMode;
        FontScale = settings.FontScale;
        LineSpacing = settings.LineSpacing;
        LetterSpacingEm = settings.LetterSpacingEm;
        OutputTransform = settings.OutputTransform;
        Blocks.Clear();
        if (snapshot.CurrentProgram is null || snapshot.CurrentPage is null)
        {
            HasProgram = false;
            IsEewProgram = false;
            IsConcurrentEewProgram = false;
            IsSubtitleProgram = false;
            ProgramId = "";
            RehearsalLabel = "";
            PageIndicator = "";
            AccessibleText = "";
            return;
        }

        DisplayProgram program = snapshot.CurrentProgram;
        DisplayPage page = snapshot.CurrentPage;
        HasProgram = true;
        IsEewProgram = program.Kind == EventKind.Eew;
        IsConcurrentEewProgram = IsEewProgram && page.Blocks.Count(static block =>
            block.StyleToken is DisplayStyleTokens.EewHeader or
                DisplayStyleTokens.EewHeaderCancel or
                DisplayStyleTokens.EewHeaderTest) > 1;
        IsSubtitleProgram = program.Kind != EventKind.Eew;
        ProgramId = program.ProgramId;
        RehearsalLabel = program.RehearsalLabel;
        PageIndicator = settings.ShowPageIndicator && program.Pages.Count > 1
            ? $"{snapshot.CurrentPageIndex + 1} / {program.Pages.Count}"
            : "";
        AccessibleText = page.AccessibleText;
        string previousBadge = string.Empty;
        foreach (DisplayBlock block in page.Blocks.Where(
            static block => block.StyleToken != DisplayStyleTokens.PageIndicator))
        {
            bool reservesPreviousBadge = string.IsNullOrWhiteSpace(block.Badge) &&
                (block.StyleToken is DisplayStyleTokens.Intensity or DisplayStyleTokens.Tsunami ||
                 block.StyleToken.StartsWith("weather-", StringComparison.Ordinal) ||
                 block.StyleToken.StartsWith("volcano-", StringComparison.Ordinal) ||
                 block.StyleToken == DisplayStyleTokens.EruptionFlash) &&
                !string.IsNullOrWhiteSpace(previousBadge);
            if (!string.IsNullOrWhiteSpace(block.Badge))
            {
                previousBadge = block.Badge;
            }
            Blocks.Add(new OverlayBlockViewModel(
                block.Badge,
                string.IsNullOrWhiteSpace(block.Badge) ? previousBadge : block.Badge,
                !string.IsNullOrWhiteSpace(block.Badge) || reservesPreviousBadge,
                block.PrimaryText,
                block.SecondaryText,
                block.StyleToken));
        }
    }

    public void ApplySettings(DisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        BackgroundMode = settings.BackgroundMode;
        FontScale = settings.FontScale;
        LineSpacing = settings.LineSpacing;
        LetterSpacingEm = settings.LetterSpacingEm;
        OutputTransform = settings.OutputTransform;
    }

    private OutputTransformSettings OutputTransform
    {
        get => _outputTransform;
        set
        {
            if (SetProperty(ref _outputTransform, value))
            {
                OnPropertyChanged(nameof(OutputScale));
                OnPropertyChanged(nameof(OutputOffsetX));
                OnPropertyChanged(nameof(OutputOffsetY));
                OnPropertyChanged(nameof(OutputClipRect));
            }
        }
    }

    private void RaiseTypographyProperties()
    {
        OnPropertyChanged(nameof(SubtitleFontSize));
        OnPropertyChanged(nameof(SubtitleNoteFontSize));
        OnPropertyChanged(nameof(SubtitleLineHeight));
        OnPropertyChanged(nameof(SubtitleNoteLineHeight));
        OnPropertyChanged(nameof(SubtitleLetterSpacing));
        OnPropertyChanged(nameof(StrokeThickness));
        OnPropertyChanged(nameof(BadgeFontSize));
        OnPropertyChanged(nameof(BadgeMinWidth));
        OnPropertyChanged(nameof(BadgePadding));
        OnPropertyChanged(nameof(PageFontSize));
        OnPropertyChanged(nameof(PagePadding));
        OnPropertyChanged(nameof(BannerFontSize));
        OnPropertyChanged(nameof(BannerLetterSpacing));
        OnPropertyChanged(nameof(BannerLineHeight));
        OnPropertyChanged(nameof(BannerPadding));
        OnPropertyChanged(nameof(EewHeaderFontSize));
        OnPropertyChanged(nameof(EewBodyFontSize));
        OnPropertyChanged(nameof(EewDetailFontSize));
    }
}

public sealed record OverlayBlockViewModel(
    string Badge,
    string BadgePlaceholder,
    bool HasBadgeColumn,
    string PrimaryText,
    string SecondaryText,
    string StyleToken)
{
    public bool IsBadgeVisible => !string.IsNullOrWhiteSpace(Badge);

    public double BadgeOpacity => IsBadgeVisible ? 1 : 0;

    public bool IsTsunami => StyleToken == DisplayStyleTokens.Tsunami;

    public string BadgeBackground => Badge switch
    {
        "長周期階級1" => "#FF075CFF",
        "長周期階級2" => "#FFFFE600",
        "長周期階級3" => "#FFF04416",
        "長周期階級4" => "#FFA00032",
        "震度1" => "#FF3B8FD4",
        "震度2" => "#FF3FB85F",
        "震度3" => "#FFFFE000",
        "震度4" => "#FFFFB000",
        "震度5弱" => "#FFFF7A1A",
        "震度5弱以上" => "#FFFF5500",
        "震度5強" => "#FFFF8000",
        "震度6弱" => "#FFFF3B1F",
        "震度6強" => "#FFD0004A",
        "震度7" => "#FFA000A0",
        "大津波警報" => "#FFC00060",
        "津波警報" => "#FFFF2D1A",
        "津波注意報" => "#FFFFD000",
        "訂正" => "#FFD35400",
        _ => StyleToken switch
        {
            DisplayStyleTokens.WeatherSpecialWarning => "#FF08050A",
            DisplayStyleTokens.WeatherDangerWarning => "#FF8F1AA6",
            DisplayStyleTokens.WeatherWarning => "#FFD00020",
            DisplayStyleTokens.WeatherAdvisory => "#FFFFD000",
            DisplayStyleTokens.WeatherCancel => "#FF555555",
            DisplayStyleTokens.VolcanoForecast => "#FF777777",
            DisplayStyleTokens.VolcanoWarning => "#FFD00020",
            DisplayStyleTokens.EruptionFlash => "#FFC00000",
            _ => "#FF777777",
        },
    };

    public string BadgeForeground => Badge is "震度3" or "津波注意報" or "長周期階級2" ||
        StyleToken == DisplayStyleTokens.WeatherAdvisory
        ? "#FF222222"
        : "#FFFFFFFF";

    public string BadgeBorderBrush =>
        StyleToken == DisplayStyleTokens.WeatherSpecialWarning
            ? "#FFD9D9D9"
            : "#00000000";

    public double BadgeBorderThickness =>
        StyleToken == DisplayStyleTokens.WeatherSpecialWarning ? 2 : 0;
}
