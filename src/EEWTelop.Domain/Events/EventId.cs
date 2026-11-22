namespace EEWTelop.Domain.Events;

public readonly record struct EventId
{
    private EventId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static EventId Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new EventId(value.Trim());
    }

    public static EventId? FromJmaEarthquakeId(string? value) =>
        value is { Length: 14 } && value.All(char.IsAsciiDigit) ? new EventId(value) : null;

    public override string ToString() => Value;
}

