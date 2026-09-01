using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class TsunamiEventStateAccumulatorTests
{
    private static readonly DateTimeOffset IssuedAt =
        new(2026, 8, 11, 1, 0, 0, TimeSpan.FromHours(9));

    [TestMethod]
    public void ComplementaryTelegramsAreMergedByRoleWithoutDuplicatingForecastArea()
    {
        var accumulator = new TsunamiEventStateAccumulator();
        TsunamiEvent vtse41 = Create(
            "VTSE41",
            [Area("静岡県", TsunamiInformationRole.ForecastArea, TsunamiGrade.Warning)]);
        TsunamiEvent vtse51 = Create(
            "VTSE51",
            [
                Area("静岡県", TsunamiInformationRole.ForecastArea, TsunamiGrade.Watch),
                Area("御前崎", TsunamiInformationRole.StationForecast, TsunamiGrade.Warning),
                Area("御前崎", TsunamiInformationRole.CoastalObservation),
            ],
            minute: 2,
            observationAsOf: IssuedAt.AddMinutes(1));
        TsunamiEvent vtse52 = Create(
            "VTSE52",
            [Area("静岡御前崎沖", TsunamiInformationRole.OffshoreObservation)],
            minute: 3);

        accumulator.Merge(vtse41);
        accumulator.Merge(vtse51);
        TsunamiEvent merged = accumulator.Merge(vtse52);

        Assert.HasCount(4, merged.Areas);
        Assert.AreEqual(1, merged.Areas.Count(static area =>
            area.Role == TsunamiInformationRole.ForecastArea));
        Assert.AreEqual(1, merged.Areas.Count(static area =>
            area.Role == TsunamiInformationRole.StationForecast));
        Assert.AreEqual(1, merged.Areas.Count(static area =>
            area.Role == TsunamiInformationRole.CoastalObservation));
        Assert.AreEqual(1, merged.Areas.Count(static area =>
            area.Role == TsunamiInformationRole.OffshoreObservation));
        Assert.AreEqual(
            TsunamiGrade.Warning,
            merged.Areas.Single(static area =>
                area.Role == TsunamiInformationRole.ForecastArea).Grade,
            "VTSE51内の重複する予報情報でVTSE41の警報状態を上書きしないこと。");
        Assert.AreEqual(IssuedAt.AddMinutes(1), merged.ObservationAsOf);
    }

    [TestMethod]
    public void CancellationClearsStoredComplementaryState()
    {
        var accumulator = new TsunamiEventStateAccumulator();
        accumulator.Merge(Create(
            "VTSE41",
            [Area("静岡県", TsunamiInformationRole.ForecastArea, TsunamiGrade.Warning)]));
        TsunamiEvent cancellation = Create("VTSE41", [], minute: 4, cancelled: true);

        TsunamiEvent cancelled = accumulator.Merge(cancellation);
        TsunamiEvent afterCancellation = accumulator.Merge(Create(
            "VTSE52",
            [Area("静岡御前崎沖", TsunamiInformationRole.OffshoreObservation)],
            minute: 5));

        Assert.IsTrue(cancelled.IsCancelled);
        Assert.HasCount(1, afterCancellation.Areas);
        Assert.AreEqual(
            TsunamiInformationRole.OffshoreObservation,
            afterCancellation.Areas[0].Role);
    }

    [TestMethod]
    public void WarningChangeMarkerIsNotRetainedForLaterObservationTelegram()
    {
        var accumulator = new TsunamiEventStateAccumulator();
        TsunamiEvent warningUpdate = Create(
            "VTSE41",
            [Area("石川県能登", TsunamiInformationRole.ForecastArea, TsunamiGrade.MajorWarning)])
            with
            {
                WarningStateChanged = true,
            };

        TsunamiEvent changed = accumulator.Merge(warningUpdate);
        TsunamiEvent observation = accumulator.Merge(Create(
            "VTSE52",
            [Area("能登半島沖", TsunamiInformationRole.OffshoreObservation)],
            minute: 2));

        Assert.IsTrue(changed.WarningStateChanged);
        Assert.IsFalse(observation.WarningStateChanged);
    }

    private static TsunamiArea Area(
        string name,
        TsunamiInformationRole role,
        TsunamiGrade grade = TsunamiGrade.Unknown) => new(
            grade,
            Immediate: false,
            name,
            FirstHeight: null,
            MaximumHeight: null)
        {
            Role = role,
            ParentAreaName = role == TsunamiInformationRole.ForecastArea
                ? string.Empty
                : "静岡県",
        };

    private static TsunamiEvent Create(
        string rawType,
        IReadOnlyList<TsunamiArea> areas,
        int minute = 1,
        bool cancelled = false,
        DateTimeOffset? observationAsOf = null)
    {
        DateTimeOffset issuedAt = IssuedAt.AddMinutes(minute);
        var issue = new IssueInfo(
            "気象庁",
            issuedAt,
            rawType,
            CorrectionType.None,
            minute.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new TsunamiEvent(
            EventId.Create("20260811010000"),
            "nii-jma-xml",
            issuedAt,
            issuedAt.AddSeconds(1),
            $"{rawType}-{minute}",
            SourceMode.Production,
            issue,
            areas,
            cancelled,
            expireAt: null,
            observationAsOf);
    }
}
