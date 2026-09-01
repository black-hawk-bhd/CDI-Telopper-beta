using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

public interface IPageComposer
{
    DisplayProgram Compose(DisasterEvent disasterEvent, DisplaySettings settings);
}
