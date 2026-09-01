using EEWTelop.Application.Abstractions;

namespace EEWTelop.Infrastructure.Identity;

public sealed class GuidIdGenerator : IIdGenerator
{
    public string NewId() => Guid.NewGuid().ToString("N");
}

