using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;

namespace EEWTelop.Wpf.ViewModels;

public sealed partial class ControlWindowViewModel
{
    private ListCollectionView? _reviewTelegramsView;
    private TelegramReviewCategory _selectedReviewCategory;

    public IReadOnlyList<TelegramReviewCategoryOption> ReviewCategoryOptions { get; } =
    [
        new(TelegramReviewCategory.All, "すべて"),
        new(TelegramReviewCategory.Weather, "気象（すべて）"),
        new(TelegramReviewCategory.Advisory, "気象：注意報"),
        new(TelegramReviewCategory.Warning, "気象：警報"),
        new(TelegramReviewCategory.SpecialWarning, "気象：特別警報"),
        new(TelegramReviewCategory.Quake, "地震"),
        new(TelegramReviewCategory.TsunamiForecast, "津波予報・注意報・警報・大津波警報"),
        new(TelegramReviewCategory.TsunamiObservation, "津波観測情報"),
        new(TelegramReviewCategory.Volcano, "火山"),
        new(TelegramReviewCategory.Eew, "EEW"),
        new(TelegramReviewCategory.Other, "その他（南海トラフ等）"),
    ];

    // A private view keeps review filtering independent of maps and reception/display settings.
    public ICollectionView ReviewTelegramsView => _reviewTelegramsView ??= new ListCollectionView(ReceivedTelegrams)
    {
        Filter = value => value is ReceivedTelegramViewModel item && MatchesReviewCategory(item),
    };

    public TelegramReviewCategory SelectedReviewCategory
    {
        get => _selectedReviewCategory;
        set
        {
            if (!SetProperty(ref _selectedReviewCategory, value)) return;
            ReviewTelegramsView.Refresh();
            if (SelectedReceivedTelegram is null || !MatchesReviewCategory(SelectedReceivedTelegram))
                SelectedReceivedTelegram = ReceivedTelegrams.FirstOrDefault(MatchesReviewCategory);
        }
    }

    private bool MatchesReviewCategory(ReceivedTelegramViewModel item) =>
        TelegramReviewCategoryFilter.Matches(item.Event, SelectedReviewCategory);
}
