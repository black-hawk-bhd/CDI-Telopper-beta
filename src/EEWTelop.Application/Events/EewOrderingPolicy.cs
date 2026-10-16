using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Events;

public enum EventVersionDisposition { Accepted, Duplicate, Stale, ReviewRequired }

public sealed record EventVersionDecision(
    EventVersionDisposition Disposition,
    ValidationIssue? Issue = null)
{
    public bool Accepted => Disposition == EventVersionDisposition.Accepted;
    public bool RequiresReview => Disposition == EventVersionDisposition.ReviewRequired;
    public bool PreserveRaw => Issue is not null;
}

// Optional snapshot metadata. Legacy signatures without it retain their old behavior.
public sealed record EewOrderingState(
    int? HighestSerial, DateTimeOffset? SourceMessageTime, DateTimeOffset IssuedAt, bool IsCancelled);

internal static class EewOrderingPolicy
{
    public static EventVersionDecision Check(EewOrderingState? previous, EewEvent item, int? serial)
    {
        DateTimeOffset? time = item.SourceMessageTime;
        if (time == DateTimeOffset.MinValue || time - item.ReceivedAt > TimeSpan.FromMinutes(5))
            return Review("EEWの送信元時刻が不正または受信時刻より5分を超えて未来です。");
        if (previous is null) return new(EventVersionDisposition.Accepted);

        bool comparable = time.HasValue && previous.SourceMessageTime.HasValue;
        if (comparable && time < previous.SourceMessageTime)
            return new(EventVersionDisposition.Stale);

        if (previous.IsCancelled)
        {
            if (item.IsCancelled) return new(EventVersionDisposition.Duplicate);
            // A late normal report must never resurrect a cancelled warning.
            if (!comparable) return Review("取消後の通常報の新旧を送信元時刻で確認できません。");
            if (time <= previous.SourceMessageTime) return new(EventVersionDisposition.Stale);
        }

        bool regressed = serial is int number && previous.HighestSerial is int highest && number < highest;
        if (regressed)
        {
            if (!item.IsCancelled) return new(EventVersionDisposition.Stale);
            if (!comparable || time <= previous.SourceMessageTime)
                return Review("EEW取消の報番号が逆行しましたが、新しい送信元時刻を確認できません。");
            return new(EventVersionDisposition.Accepted, new ValidationIssue("eew.ordering.serialRegression",
                $"EEW取消の報番号逆行（{previous.HighestSerial}→{serial}）を検出。送信元時刻が新しいため採用しました。原文の番号は変更していません。",
                ValidationSeverity.Warning));
        }

        if (item.IsCancelled && serial is null && (!comparable || time <= previous.SourceMessageTime))
            return Review("EEW取消の報番号がなく、新しい送信元時刻も確認できません。");
        return new(EventVersionDisposition.Accepted);
    }

    private static EventVersionDecision Review(string reason) => new(EventVersionDisposition.ReviewRequired,
        new ValidationIssue("eew.ordering.reviewRequired", "要確認：" + reason + " 表示・音声・外部APIの更新を保留し、原文を保存します。",
            ValidationSeverity.Warning));
}
