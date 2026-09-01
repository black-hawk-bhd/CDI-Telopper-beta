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

    public override string ToString() => Value;
}

