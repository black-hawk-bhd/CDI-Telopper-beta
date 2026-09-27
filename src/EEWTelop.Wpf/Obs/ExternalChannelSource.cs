using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.Obs;

internal static class ExternalChannelSource
{
    internal static bool Accepts(DisasterEvent item, bool training) => training
        ? item.SourceMode is SourceMode.ManualTest or SourceMode.Sandbox or SourceMode.HistoryRehearsal
        : item.SourceMode == SourceMode.Production;

    internal static string Mode(DisasterEvent item) => item.SourceMode switch
    {
        SourceMode.Production => "production",
        SourceMode.ManualTest when item.Provider == "cdi-disaster-simulator" => "simulator",
        SourceMode.ManualTest => "manualTest",
        SourceMode.Sandbox => "sandbox",
        SourceMode.HistoryRehearsal => "historyRehearsal",
        _ => "unknownTraining",
    };
}
