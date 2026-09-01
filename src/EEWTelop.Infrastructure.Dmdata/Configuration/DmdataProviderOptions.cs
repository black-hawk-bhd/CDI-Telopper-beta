using EEWTelop.Application.Configuration;
using EEWTelop.Infrastructure.Dmdata.Security;

namespace EEWTelop.Infrastructure.Dmdata.Configuration;

public sealed record DmdataProviderOptions(
    Uri ApiBaseUri,
    string Credential,
    DmdataAuthenticationMode AuthenticationMode,
    bool IncludeTestTelegrams)
{
    private static readonly string[] EewWarningTelegramTypes = ["VXSE43"];

    private static readonly string[] EewForecastTelegramTypes = ["VXSE45"];

    private static readonly string[] QuakeTelegramTypes =
    [
        "VXSE51", "VXSE52", "VXSE53", "VXSE62", "VYSE60",
    ];

    private static readonly string[] TsunamiTelegramTypes =
        ["VTSE41", "VTSE51", "VTSE52"];

    private static readonly string[] NankaiTroughTelegramTypes = ["VYSE50"];

    private static readonly string[] CurrentWeatherWarningTelegramTypes =
    [
        "VPWW55", "VPWW56", "VPWW57", "VPWW58", "VPWW59", "VPWW60", "VPWW61",
        "VPOA50", "VPBS50", "VPBS51", "VPHW50", "VPHW51",
    ];

    private static readonly string[] LegacyWeatherWarningTelegramTypes =
        ["VPWW53", "VPWW54", "VPOA50", "VPBS50", "VPBS51", "VPHW50", "VPHW51"];

    private static readonly string[] VolcanoTelegramTypes = ["VFVO50", "VFVO56"];

    public bool ReceiveEewWarnings { get; init; }

    public DmdataEewContractType EewContractType { get; init; } =
        DmdataEewContractType.Warning;

    public bool ReceiveQuakeTelegrams { get; init; }

    public bool ReceiveTsunamiTelegrams { get; init; }

    public bool ReceiveNankaiTroughTelegrams { get; init; }

    public bool ReceiveEarthquakeTelegrams => ReceiveQuakeTelegrams ||
        ReceiveTsunamiTelegrams || ReceiveNankaiTroughTelegrams;

    public bool ReceiveWeatherWarnings { get; init; }

    public bool ReceiveVolcanoTelegrams { get; init; }

    public bool UseLegacyWeatherWarningTelegrams { get; init; }

    public IReadOnlyList<string> Classifications
    {
        get
        {
            var values = new List<string>(4);
            if (ReceiveEewWarnings)
            {
                values.Add(EewContractType == DmdataEewContractType.Warning
                    ? "eew.warning"
                    : "eew.forecast");
            }
            if (ReceiveEarthquakeTelegrams) values.Add("telegram.earthquake");
            if (ReceiveWeatherWarnings) values.Add("telegram.weather");
            if (ReceiveVolcanoTelegrams) values.Add("telegram.volcano");
            return values;
        }
    }

    public IReadOnlyList<string> TelegramTypes =>
        (ReceiveEewWarnings
            ? EewContractType == DmdataEewContractType.Warning
                ? EewWarningTelegramTypes
                : EewForecastTelegramTypes
            : [])
        .Concat(ReceiveQuakeTelegrams ? QuakeTelegramTypes : [])
        .Concat(ReceiveTsunamiTelegrams ? TsunamiTelegramTypes : [])
        .Concat(ReceiveNankaiTroughTelegrams ? NankaiTroughTelegramTypes : [])
        .Concat(ReceiveWeatherWarnings
            ? UseLegacyWeatherWarningTelegrams
                ? LegacyWeatherWarningTelegramTypes
                : CurrentWeatherWarningTelegramTypes
            : [])
        .Concat(ReceiveVolcanoTelegrams ? VolcanoTelegramTypes : [])
        .ToArray();

    public static DmdataProviderOptions FromSettings(
        ProviderSettings settings,
        bool allowExtendedCategories = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string credential = DmdataCredentialProtector.Unprotect(
            settings.DmdataProtectedCredential);
        if (string.IsNullOrWhiteSpace(credential) &&
            !string.IsNullOrWhiteSpace(settings.DmdataCredentialEnvironmentVariable))
        {
            // Compatibility path for settings created before schema 22. New
            // credentials are entered directly and stored through DPAPI.
            credential = Environment.GetEnvironmentVariable(
                settings.DmdataCredentialEnvironmentVariable.Trim()) ?? string.Empty;
        }

        bool usesDmdataRouting = settings.Routing.Uses(ReceptionProvider.Dmdata);
        bool legacyEarthquakeSelection = !usesDmdataRouting &&
            settings.DmdataReceiveEarthquakeTelegrams;
        return new DmdataProviderOptions(
            new Uri(EnsureTrailingSlash(settings.DmdataApiBaseUrl), UriKind.Absolute),
            credential.Trim(),
            settings.DmdataAuthenticationMode,
            settings.DmdataIncludeTestTelegrams)
        {
            ReceiveEewWarnings = usesDmdataRouting
                ? settings.Routing.Eew == ReceptionProvider.Dmdata
                : settings.DmdataReceiveEewWarnings,
            EewContractType = settings.DmdataEewContractType,
            ReceiveQuakeTelegrams = usesDmdataRouting
                ? settings.Routing.Quake == ReceptionProvider.Dmdata
                : legacyEarthquakeSelection,
            ReceiveTsunamiTelegrams = usesDmdataRouting
                ? settings.Routing.Tsunami == ReceptionProvider.Dmdata
                : legacyEarthquakeSelection,
            ReceiveNankaiTroughTelegrams = usesDmdataRouting
                ? settings.Routing.NankaiTrough == ReceptionProvider.Dmdata
                : legacyEarthquakeSelection,
            ReceiveWeatherWarnings = allowExtendedCategories &&
                (usesDmdataRouting
                    ? settings.Routing.Weather == ReceptionProvider.Dmdata
                    : settings.DmdataReceiveWeatherWarnings),
            ReceiveVolcanoTelegrams = allowExtendedCategories &&
                (usesDmdataRouting
                    ? settings.Routing.Volcano == ReceptionProvider.Dmdata
                    : settings.DmdataReceiveVolcanoTelegrams),
            UseLegacyWeatherWarningTelegrams = allowExtendedCategories &&
                settings.DmdataUseLegacyWeatherWarningTelegrams &&
                (usesDmdataRouting
                    ? settings.Routing.Weather == ReceptionProvider.Dmdata
                    : settings.DmdataReceiveWeatherWarnings),
        };
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ApiBaseUri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("DMDATA.JP API URL must use https.");
        }

        if (string.IsNullOrWhiteSpace(Credential))
        {
            errors.Add("DMDATA.JP credential is required.");
        }

        if (!Enum.IsDefined(EewContractType))
        {
            errors.Add("DMDATA.JP EEW contract type is invalid.");
        }

        if (Classifications.Count == 0 || TelegramTypes.Count == 0)
        {
            errors.Add("At least one DMDATA.JP contract category must be enabled.");
        }

        return errors;
    }

    private static string EnsureTrailingSlash(string value) =>
        value.Trim().TrimEnd('/') + "/";
}
