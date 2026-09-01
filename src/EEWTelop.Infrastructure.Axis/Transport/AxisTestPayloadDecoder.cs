using EEWTelop.Application.Events;

namespace EEWTelop.Infrastructure.Axis.Transport;

/// <summary>
/// Reuses the production AXIS envelope decoder for isolated, operator-selected
/// transport.json regression cases. It never opens an AXIS connection.
/// </summary>
public static class AxisTestPayloadDecoder
{
    public static bool TryDecode(
        string payload,
        out string providerPayload,
        out RawProviderContentFormat contentFormat,
        out string reason)
    {
        try
        {
            AxisDecodedFrame frame = AxisEnvelopeDecoder.Decode(
                payload,
                Configuration.AxisProviderOptions.DefaultChannel);
            if (frame.Kind != AxisFrameKind.Data ||
                string.IsNullOrWhiteSpace(frame.ProviderPayload))
            {
                providerPayload = string.Empty;
                contentFormat = RawProviderContentFormat.Json;
                reason = $"データ電文ではありません ({frame.Kind})。";
                return false;
            }

            providerPayload = frame.ProviderPayload;
            contentFormat = frame.ContentFormat;
            reason = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException or System.Xml.XmlException)
        {
            providerPayload = string.Empty;
            contentFormat = RawProviderContentFormat.Json;
            reason = exception.Message;
            return false;
        }
    }
}
