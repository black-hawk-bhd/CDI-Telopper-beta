using System.Globalization;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Formatting;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

internal static class EewPageComposer
{
    internal const string EstimatedMagnitudePrefix = "推定マグニチュード ";

    public static DisplayProgram Compose(EewEvent eew, DisplaySettings settings)
    {
        var blocks = new List<DisplayBlock>();
        if (eew.IsCancelled)
        {
            blocks.Add(new DisplayBlock(
                string.Empty,
                "緊急地震速報（取消）",
                string.Empty,
                DisplayStyleTokens.EewHeaderCancel)
            {
                EewCard = new EewWarningCard("緊急地震速報（取消）",
                    PageComposerSupport.GetCancellationText("緊急地震速報"), string.Empty, [], true),
            });
            blocks.Add(new DisplayBlock(
                string.Empty,
                PageComposerSupport.GetCancellationText("緊急地震速報"),
                string.Empty,
                DisplayStyleTokens.EewWarning));
        }
        else
        {
            string hypocenterName = string.IsNullOrWhiteSpace(eew.Earthquake?.Hypocenter?.Name)
                ? "震源地不明"
                : eew.Earthquake.Hypocenter.Name;
            IReadOnlyList<string> areaLabels = EewAreaLabelFormatter.Format(eew.Areas);
            blocks.Add(new DisplayBlock(
                string.Empty,
                "緊急地震速報（気象庁）",
                string.Empty,
                DisplayStyleTokens.EewHeader)
            {
                EewCard = eew.IsWarning
                    ? new EewWarningCard("緊急地震速報（気象庁）", $"{hypocenterName}で地震",
                        "強い揺れに警戒", areaLabels.Count == 0 ? ["対象地域情報なし"] : areaLabels, false)
                    : null,
            });
            blocks.Add(new DisplayBlock(
                string.Empty,
                eew.IsWarning
                    ? $"{hypocenterName}で地震　強い揺れに警戒"
                    : $"{hypocenterName}で地震　今後の情報に注意",
                string.Empty,
                DisplayStyleTokens.EewWarning));

            blocks.Add(new DisplayBlock(
                string.Empty,
                areaLabels.Count == 0 ? "対象地域情報なし" : string.Join('　', areaLabels),
                string.Empty,
                DisplayStyleTokens.EewAreas)
            {
                EewEstimateText = eew.IsWarning ? FormatEstimates(eew) : null,
            });
        }

        return PageComposerSupport.CreateProgram(
            eew,
            OverlayPriority.Eew,
            EndPolicy.AutoHide,
            [new PageDraft(blocks)]);
    }

    private static string? FormatEstimates(EewEvent eew)
    {
        var details = new List<string>(2);
        if (eew.MaximumForecastIntensity is { } range)
        {
            string intensity = range switch
            {
                { IsOpenEnded: true } => ScaleFormatter.Format(range.From) + "以上",
                { From: JmaScale.Unknown } => "上限 " + ScaleFormatter.Format(range.To),
                { To: JmaScale.Unknown } => "下限 " + ScaleFormatter.Format(range.From),
                _ when range.From == range.To => ScaleFormatter.Format(range.From),
                _ => $"{ScaleFormatter.Format(range.From)}〜{ScaleFormatter.Format(range.To)}",
            };
            details.Add($"予想最大震度 {intensity}");
        }
        else if (eew.Earthquake?.MaximumScale is { } scale && scale != JmaScale.Unknown &&
                 scale != JmaScale.FiveLowerOrMore)
        {
            details.Add($"予想最大震度 {ScaleFormatter.Format(scale)}");
        }

        if (eew.Earthquake?.Hypocenter is { Condition: not HypocenterInfo.ProvisionalCondition, Magnitude: { } magnitude } &&
            double.IsFinite(magnitude) && magnitude > 0)
        {
            details.Add(EstimatedMagnitudePrefix + magnitude.ToString("0.0", CultureInfo.InvariantCulture));
        }

        return details.Count == 0 ? null : string.Join("　", details);
    }
}
