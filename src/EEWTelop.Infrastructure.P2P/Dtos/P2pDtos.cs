using System.Text.Json.Serialization;

namespace EEWTelop.Infrastructure.P2P.Dtos;

internal class P2pBasicDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("_id")]
    public string? LegacyId { get; init; }

    [JsonPropertyName("code")]
    public int? Code { get; init; }

    [JsonPropertyName("time")]
    public string? Time { get; init; }

    public string? EffectiveId => string.IsNullOrWhiteSpace(Id) ? LegacyId : Id;
}

internal sealed class P2pQuakeDto : P2pBasicDto
{
    [JsonPropertyName("issue")]
    public P2pIssueDto? Issue { get; init; }

    [JsonPropertyName("earthquake")]
    public P2pQuakeEarthquakeDto? Earthquake { get; init; }

    [JsonPropertyName("points")]
    public IReadOnlyList<P2pQuakePointDto>? Points { get; init; }

    [JsonPropertyName("comments")]
    public P2pCommentsDto? Comments { get; init; }
}

internal sealed class P2pTsunamiDto : P2pBasicDto
{
    [JsonPropertyName("cancelled")]
    public bool? Cancelled { get; init; }

    [JsonPropertyName("issue")]
    public P2pIssueDto? Issue { get; init; }

    [JsonPropertyName("areas")]
    public IReadOnlyList<P2pTsunamiAreaDto>? Areas { get; init; }
}

internal sealed class P2pEewDto : P2pBasicDto
{
    [JsonPropertyName("test")]
    public bool? Test { get; init; }

    [JsonPropertyName("earthquake")]
    public P2pEewEarthquakeDto? Earthquake { get; init; }

    [JsonPropertyName("issue")]
    public P2pEewIssueDto? Issue { get; init; }

    [JsonPropertyName("cancelled")]
    public bool? Cancelled { get; init; }

    [JsonPropertyName("areas")]
    public IReadOnlyList<P2pEewAreaDto>? Areas { get; init; }
}

internal sealed class P2pIssueDto
{
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("time")]
    public string? Time { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("correct")]
    public string? Correct { get; init; }
}

internal sealed class P2pEewIssueDto
{
    [JsonPropertyName("time")]
    public string? Time { get; init; }

    [JsonPropertyName("eventId")]
    public string? EventId { get; init; }

    [JsonPropertyName("serial")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? Serial { get; init; }
}

internal sealed class P2pQuakeEarthquakeDto
{
    [JsonPropertyName("time")]
    public string? Time { get; init; }

    [JsonPropertyName("hypocenter")]
    public P2pHypocenterDto? Hypocenter { get; init; }

    [JsonPropertyName("maxScale")]
    public double? MaximumScale { get; init; }

    [JsonPropertyName("domesticTsunami")]
    public string? DomesticTsunami { get; init; }

    [JsonPropertyName("foreignTsunami")]
    public string? ForeignTsunami { get; init; }
}

internal sealed class P2pEewEarthquakeDto
{
    [JsonPropertyName("originTime")]
    public string? OriginTime { get; init; }

    [JsonPropertyName("arrivalTime")]
    public string? ArrivalTime { get; init; }

    [JsonPropertyName("condition")]
    public string? Condition { get; init; }

    [JsonPropertyName("hypocenter")]
    public P2pHypocenterDto? Hypocenter { get; init; }
}

internal sealed class P2pHypocenterDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("reduceName")]
    public string? ReducedName { get; init; }

    [JsonPropertyName("latitude")]
    public double? Latitude { get; init; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; init; }

    [JsonPropertyName("depth")]
    public double? Depth { get; init; }

    [JsonPropertyName("magnitude")]
    public double? Magnitude { get; init; }
}

internal sealed class P2pQuakePointDto
{
    [JsonPropertyName("pref")]
    public string? Prefecture { get; init; }

    [JsonPropertyName("addr")]
    public string? Address { get; init; }

    [JsonPropertyName("isArea")]
    public bool? IsArea { get; init; }

    [JsonPropertyName("scale")]
    public double? Scale { get; init; }
}

internal sealed class P2pCommentsDto
{
    [JsonPropertyName("freeFormComment")]
    public string? FreeFormComment { get; init; }
}

internal sealed class P2pTsunamiAreaDto
{
    [JsonPropertyName("grade")]
    public string? Grade { get; init; }

    [JsonPropertyName("immediate")]
    public bool? Immediate { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("firstHeight")]
    public P2pFirstHeightDto? FirstHeight { get; init; }

    [JsonPropertyName("maxHeight")]
    public P2pMaximumHeightDto? MaximumHeight { get; init; }
}

internal sealed class P2pFirstHeightDto
{
    [JsonPropertyName("arrivalTime")]
    public string? ArrivalTime { get; init; }

    [JsonPropertyName("condition")]
    public string? Condition { get; init; }
}

internal sealed class P2pMaximumHeightDto
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("value")]
    public double? Value { get; init; }
}

internal sealed class P2pEewAreaDto
{
    [JsonPropertyName("pref")]
    public string? Prefecture { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("scaleFrom")]
    public double? ScaleFrom { get; init; }

    [JsonPropertyName("scaleTo")]
    public double? ScaleTo { get; init; }

    [JsonPropertyName("kindCode")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? KindCode { get; init; }

    [JsonPropertyName("arrivalTime")]
    public string? ArrivalTime { get; init; }
}
