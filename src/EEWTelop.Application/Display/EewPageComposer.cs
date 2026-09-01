using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

internal static class EewPageComposer
{
    public static DisplayProgram Compose(EewEvent eew, DisplaySettings settings)
    {
        var blocks = new List<DisplayBlock>();
        if (eew.IsCancelled)
        {
            blocks.Add(new DisplayBlock(
                string.Empty,
                "緊急地震速報（取消）",
                string.Empty,
                DisplayStyleTokens.EewHeaderCancel));
            blocks.Add(new DisplayBlock(
                string.Empty,
                PageComposerSupport.GetCancellationText("緊急地震速報"),
                string.Empty,
                DisplayStyleTokens.EewWarning));
        }
        else
        {
            blocks.Add(new DisplayBlock(
                string.Empty,
                "緊急地震速報（気象庁）",
                string.Empty,
                DisplayStyleTokens.EewHeader));

            string hypocenterName = string.IsNullOrWhiteSpace(eew.Earthquake?.Hypocenter?.Name)
                ? "震源地不明"
                : eew.Earthquake.Hypocenter.Name;
            blocks.Add(new DisplayBlock(
                string.Empty,
                eew.IsWarning
                    ? $"{hypocenterName}で地震　強い揺れに警戒"
                    : $"{hypocenterName}で地震　今後の情報に注意",
                string.Empty,
                DisplayStyleTokens.EewWarning));

            IReadOnlyList<string> areaLabels = EewAreaLabelFormatter.Format(eew.Areas);
            blocks.Add(new DisplayBlock(
                string.Empty,
                areaLabels.Count == 0 ? "対象地域情報なし" : string.Join('　', areaLabels),
                string.Empty,
                DisplayStyleTokens.EewAreas));
        }

        return PageComposerSupport.CreateProgram(
            eew,
            settings,
            OverlayPriority.Eew,
            EndPolicy.AutoHide,
            [new PageDraft(blocks)]);
    }
}
