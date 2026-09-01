using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

public sealed class PageComposer : IPageComposer
{
    public DisplayProgram Compose(DisasterEvent disasterEvent, DisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(disasterEvent);
        ArgumentNullException.ThrowIfNull(settings);

        DisplayProgram program = disasterEvent switch
        {
            QuakeEvent quake => QuakePageComposer.Compose(quake, settings),
            TsunamiEvent tsunami => TsunamiPageComposer.Compose(tsunami, settings),
            EewEvent eew => EewPageComposer.Compose(eew, settings),
            WeatherWarningEvent weather => WeatherWarningPageComposer.Compose(weather, settings),
            VolcanoEvent volcano => VolcanoPageComposer.Compose(volcano, settings),
            _ => throw new NotSupportedException(
                $"Unsupported disaster event type: {disasterEvent.GetType().FullName}"),
        };
        return SubtitlePhraseCatalog.ApplyOverrides(program, settings);
    }
}
