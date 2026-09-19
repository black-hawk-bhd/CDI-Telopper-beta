using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.History;
using EEWTelop.Domain.Events;
using System.Xml;
using System.Xml.Linq;
#if QTELOPPER_DMDATA_PROVIDER
using EEWTelop.Infrastructure.Dmdata.History;
using EEWTelop.Infrastructure.Dmdata.Normalization;
using EEWTelop.Infrastructure.Time;
#endif

namespace EEWTelop.Wpf.Controls;

internal static class MapXmlLoader
{
    internal static async Task<QuakeEvent> LoadAsync(string path)
    {
#if QTELOPPER_DMDATA_PROVIDER
        await using var source = new LocalJmaXmlHistoryMessageSource(new SystemClock());
        var messages = await source.FetchAsync(new HistoryFetchRequest(
            HistoryApi.LocalJmaXml, 1, AppSettings.CreateDefault().Provider) { LocalXmlFilePath = path });
        using var reader = XmlReader.Create(new StringReader(messages.Single().Payload), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = LocalJmaXmlHistoryMessageSource.MaximumXmlBytes,
        });
        var document = XDocument.Load(reader);
        string status = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Control")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "Status")?.Value ?? "";
        var raw = messages.Single();
        if (status.Contains("訓練", StringComparison.Ordinal) || status.Contains("試験", StringComparison.Ordinal))
            raw = raw with { SourceMode = SourceMode.ManualTest };
        var result = new JmaXmlEventNormalizer(new EventSignatureBuilder()).Normalize(raw);
        if (!result.IsSuccess || result.Event is not QuakeEvent quake)
            throw new InvalidDataException("地図に対応する気象庁形式の地震XMLではありません。震源・震度情報などのXMLを選択してください。");
        return quake;
#else
        await Task.CompletedTask;
        throw new InvalidOperationException("このビルドはXML読み込みに対応していません。");
#endif
    }
}
