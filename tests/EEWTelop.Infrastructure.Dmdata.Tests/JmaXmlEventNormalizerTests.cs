using System.Globalization;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Dmdata.History;
using EEWTelop.Infrastructure.Dmdata.Normalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Dmdata.Tests;

[TestClass]
public sealed class JmaXmlEventNormalizerTests
{
    [TestMethod]
    public void TestLibraryJmaXmlProviderIsNormalizedForDisconnectedRehearsal()
    {
        const string xml = """
            <Report>
              <Control>
                <Title>緊急地震速報（警報）</Title>
                <Status>通常</Status>
                <PublishingOffice>気象庁</PublishingOffice>
              </Control>
              <Head>
                <Title>緊急地震速報（警報）</Title>
                <ReportDateTime>2024-01-01T16:10:10+09:00</ReportDateTime>
                <EventID>20240101161010</EventID>
                <Serial>1</Serial>
                <InfoType>発表</InfoType>
                <Headline>
                  <Text>石川県で地震　北陸で強い揺れ</Text>
                  <Information type="緊急地震速報（地方予報区）">
                    <Item>
                      <Kind><Name>緊急地震速報（警報）</Name><Code>31</Code></Kind>
                      <Areas codeType="緊急地震速報／地方予報区"><Area><Name>北陸</Name><Code>9934</Code></Area></Areas>
                    </Item>
                  </Information>
                </Headline>
              </Head>
              <Body>
                <Earthquake>
                  <OriginTime>2024-01-01T16:10:00+09:00</OriginTime>
                  <Hypocenter><Area><Name>石川県能登地方</Name></Area></Hypocenter>
                  <Magnitude>7.6</Magnitude>
                </Earthquake>
                <Intensity><Forecast>
                  <ForecastInt><From>5-</From><To>7</To></ForecastInt>
                  <Pref><Name>石川</Name><Area><Name>石川県能登</Name><ForecastInt><From>5-</From><To>7</To></ForecastInt></Area></Pref>
                </Forecast></Intensity>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "test-library-jma-xml",
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2024-01-01T07:10:11Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.AreEqual(NormalizeStatus.Success, result.Status);
        EewEvent eew = Assert.IsInstanceOfType<EewEvent>(result.Event);
        DisplayProgram program = new PageComposer().Compose(eew, AppSettings.CreateDefault().Display);
        Assert.IsGreaterThan(0, program.Pages.Count);
    }

    [TestMethod]
    public void Vyse60NormalizesHeadlineAndUsesDedicatedFourPageLayout()
    {
        const string xml = """
            <Report>
              <Control>
                <Title>北海道・三陸沖後発地震注意情報</Title>
                <Status>通常</Status>
                <PublishingOffice>気象庁</PublishingOffice>
              </Control>
              <Head>
                <Title>北海道・三陸沖後発地震注意情報</Title>
                <ReportDateTime>2026-04-20T19:30:00+09:00</ReportDateTime>
                <TargetDateTime>2026-04-20T19:30:00+09:00</TargetDateTime>
                <EventID>20260420193000</EventID>
                <InfoType>発表</InfoType>
                <InfoKind>北海道・三陸沖後発地震注意情報</InfoKind>
                <Headline><Text>
                  本日（２０日）１６時５２分に三陸沖を震源とするモーメントマグニチュード（Ｍｗ）７．４の地震が発生しました。この地震の発生により、北海道の根室沖から東北地方の三陸沖にかけての巨大地震の想定震源域では、新たな大規模地震の発生可能性が平常時と比べて相対的に高まっていると考えられます。今後の政府や自治体などからの呼びかけ等に応じた防災対応をとってください。
                </Text></Headline>
              </Head>
              <Body />
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "test-library-jma-xml",
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2026-04-20T10:30:03Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.AreEqual(NormalizeStatus.Success, result.Status);
        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.AreEqual(QuakeIssueType.SubsequentEarthquakeAdvisory, quake.IssueType);
        Assert.AreEqual("VYSE60", quake.Issue.RawType);
        StringAssert.Contains(quake.Headline, "モーメントマグニチュード（Ｍｗ）７．４");

        DisplayProgram program = new PageComposer().Compose(
            quake,
            AppSettings.CreateDefault().Display);
        Assert.HasCount(4, program.Pages);
        StringAssert.Contains(program.Pages[0].AccessibleText, "北海道・三陸沖後発地震注意情報");
        StringAssert.Contains(program.Pages[3].AccessibleText, "防災対応をとってください");
    }

    [TestMethod]
    [DataRow("調査中")]
    [DataRow("巨大地震警戒")]
    [DataRow("巨大地震注意")]
    [DataRow("調査終了")]
    public void Vyse50NormalizesEveryTemporaryInformationCategory(string category)
    {
        string xml = $$"""
            <Report>
              <Control><Title>南海トラフ地震臨時情報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head>
                <Title>南海トラフ地震臨時情報（{{category}}）</Title>
                <ReportDateTime>2026-08-29T12:00:00+09:00</ReportDateTime>
                <TargetDateTime>2026-08-29T12:00:00+09:00</TargetDateTime>
                <EventID>20260829120000</EventID><InfoType>発表</InfoType>
                <Headline><Text>南海トラフ地震との関連性についての重要な情報です。今後の情報に注意してください。</Text></Headline>
              </Head>
              <Body><EarthquakeInfo type="南海トラフ地震に関連する情報"><InfoSerial><Name>{{category}}</Name></InfoSerial></EarthquakeInfo></Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "test-library-jma-xml",
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2026-08-29T03:00:00Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.AreEqual(NormalizeStatus.Success, result.Status);
        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.AreEqual(QuakeIssueType.NankaiTroughTemporaryInformation, quake.IssueType);
        Assert.AreEqual("VYSE50", quake.Issue.RawType);
        StringAssert.StartsWith(quake.Headline, $"南海トラフ地震臨時情報（{category}）");

        DisplayProgram program = new PageComposer().Compose(
            quake,
            AppSettings.CreateDefault().Display);
        Assert.IsGreaterThanOrEqualTo(2, program.Pages.Count);
        Assert.AreEqual(
            $"南海トラフ地震臨時情報（{category}）",
            program.Pages[0].AccessibleText);
        Assert.IsTrue(program.Pages.Skip(1).Any(static page =>
            page.AccessibleText.Contains("今後の情報に注意", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Vyse50CancellationUsesTelegramSpecificMessage()
    {
        const string xml = """
            <Report>
              <Control><Title>南海トラフ地震臨時情報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head><Title>南海トラフ地震臨時情報（調査中）</Title><ReportDateTime>2022-03-18T19:38:00+09:00</ReportDateTime><EventID>20220318193600</EventID><InfoType>取消</InfoType><Headline><Text>先ほど発表した情報は誤りですので取り消します。</Text></Headline></Head>
              <Body><Text>先ほど発表した情報は誤りですので取り消します。</Text></Body>
            </Report>
            """;

        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());
        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "test-library-jma-xml",
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2022-03-18T10:38:10Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.AreEqual(NormalizeStatus.Success, result.Status);
        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.IsTrue(quake.IsCancelled);
        DisplayProgram program = new PageComposer().Compose(
            quake,
            AppSettings.CreateDefault().Display);
        Assert.HasCount(1, program.Pages);
        Assert.AreEqual(
            "先ほどの、南海トラフ地震臨時情報を取り消します",
            program.Pages[0].AccessibleText);
    }

    [TestMethod]
    public void LocalJmaXmlProviderIsNormalizedThroughTheSharedXmlPath()
    {
        const string xml = """
            <Report uuid="20260814064517_0_VFVO50_400000">
              <Control>
                <Title>噴火警報・予報</Title>
                <Status>通常</Status>
                <PublishingOffice>福岡管区気象台</PublishingOffice>
              </Control>
              <Head>
                <Title>火山名 阿蘇山 火口周辺警報</Title>
                <ReportDateTime>2026-08-14T15:45:16+09:00</ReportDateTime>
                <EventID>503</EventID>
                <InfoType>発表</InfoType>
              </Head>
              <Body>
                <VolcanoInfo type="噴火警報・予報（対象火山）">
                  <Item>
                    <Kind><Name>レベル３（入山規制）</Name><Code>13</Code><Condition>引上げ</Condition></Kind>
                    <Areas codeType="火山名"><Area><Name>阿蘇山</Name><Code>503</Code></Area></Areas>
                  </Item>
                </VolcanoInfo>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            LocalJmaXmlHistoryMessageSource.ProviderName,
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2026-08-14T06:45:17Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.AreEqual(NormalizeStatus.Success, result.Status);
        VolcanoEvent volcano = Assert.IsInstanceOfType<VolcanoEvent>(result.Event);
        Assert.AreEqual("VFVO50", volcano.Issue.RawType);
        Assert.AreEqual("阿蘇山", volcano.VolcanoName);
        Assert.AreEqual(SourceMode.HistoryRehearsal, volcano.SourceMode);
    }

    [TestMethod]
    public void Vfvo50NormalizesVolcanoWarningForecast()
    {
        const string xml = """
            <Report>
              <Control>
                <Title>噴火警報・予報</Title>
                <Status>通常</Status>
                <PublishingOffice>福岡管区気象台</PublishingOffice>
              </Control>
              <Head>
                <Title>火山名 桜島 火口周辺警報</Title>
                <ReportDateTime>2026-07-27T10:00:14+09:00</ReportDateTime>
                <EventID>20260727100014_506</EventID>
                <Serial>1</Serial>
                <InfoType>発表</InfoType>
                <Headline><Text>桜島に火口周辺警報を発表</Text></Headline>
              </Head>
              <Body>
                <VolcanoInfo type="噴火警報・予報（対象火山）">
                  <Item>
                    <Kind><Name>レベル３（入山規制）</Name><Code>13</Code><Condition>引上げ</Condition></Kind>
                    <LastKind><Name>レベル１（活火山であることに留意）</Name><Code>11</Code><Condition /></LastKind>
                    <Areas codeType="火山名"><Area><Name>桜島</Name><Code>506</Code></Area></Areas>
                  </Item>
                </VolcanoInfo>
                <VolcanoInfo type="噴火警報・予報（対象市町村等）">
                  <Item>
                    <Kind><Name>火口周辺警報</Name><Code>02</Code><Condition>発表</Condition></Kind>
                    <LastKind><Name>噴火予報</Name><Code>05</Code><Condition /></LastKind>
                    <Areas codeType="気象・地震・火山情報／市町村等"><Area><Name>鹿児島市</Name><Code>4620100</Code></Area></Areas>
                  </Item>
                </VolcanoInfo>
                <VolcanoInfo type="噴火警報・予報（対象市町村の防災対応等）">
                  <Item>
                    <Kind><Name>火口周辺警報：入山規制等</Name><Code>43</Code><Condition>発表</Condition></Kind>
                    <LastKind><Name>活火山であることに留意</Name><Code>45</Code><Condition /></LastKind>
                    <Areas codeType="気象・地震・火山情報／市町村等"><Area><Name>鹿児島市</Name><Code>4620100</Code></Area></Areas>
                  </Item>
                </VolcanoInfo>
                <VolcanoInfoContent>
                  <VolcanoHeadline>噴火警戒レベルを３に引き上げました。</VolcanoHeadline>
                  <VolcanoActivity>活発な噴火活動が続いています。</VolcanoActivity>
                  <VolcanoPrevention>火口からおおむね２kmの範囲では警戒してください。</VolcanoPrevention>
                </VolcanoInfoContent>
              </Body>
            </Report>
            """;

        VolcanoEvent volcano = NormalizeVolcano(xml);

        Assert.AreEqual(VolcanoInformationType.WarningForecast, volcano.InformationType);
        Assert.AreEqual("VFVO50", volcano.Issue.RawType);
        Assert.AreEqual("桜島", volcano.VolcanoName);
        Assert.AreEqual("506", volcano.VolcanoCode);
        Assert.AreEqual(VolcanoAlertLevel.Level3, volcano.AlertLevel);
        Assert.AreEqual("レベル３（入山規制）", volcano.AlertLevelText);
        Assert.AreEqual("13", volcano.AlertLevelCode);
        Assert.AreEqual("引上げ", volcano.AlertCondition);
        Assert.AreEqual("レベル１（活火山であることに留意）", volcano.PreviousAlertLevelText);
        Assert.AreEqual("11", volcano.PreviousAlertLevelCode);
        Assert.IsTrue(volcano.IsWarning);
        Assert.HasCount(1, volcano.TargetAreas);
        Assert.AreEqual("鹿児島市", volcano.TargetAreas[0].Name);
        Assert.AreEqual("火口周辺警報", volcano.TargetAreas[0].KindName);
        Assert.AreEqual("02", volcano.TargetAreas[0].KindCode);
        Assert.AreEqual("噴火予報", volcano.TargetAreas[0].PreviousKindName);
        Assert.AreEqual("05", volcano.TargetAreas[0].PreviousKindCode);
        Assert.AreEqual("活発な噴火活動が続いています。", volcano.Activity);
        Assert.IsFalse(volcano.IsCancelled);
    }

    [TestMethod]
    public void Vfvo56NormalizesEruptionFlashAndOccurrenceTime()
    {
        const string xml = """
            <Report>
              <Control>
                <Title>噴火速報</Title>
                <Status>通常</Status>
                <PublishingOffice>福岡管区気象台</PublishingOffice>
              </Control>
              <Head>
                <Title>火山名 諏訪之瀬島 噴火速報</Title>
                <ReportDateTime>2024-01-14T00:29:37+09:00</ReportDateTime>
                <TargetDateTime>2024-01-14T00:29:00+09:00</TargetDateTime>
                <TargetDTDubious>頃</TargetDTDubious>
                <EventID>20240114002900_511</EventID>
                <InfoType>発表</InfoType>
                <Headline><Text>諏訪之瀬島で噴火が発生</Text></Headline>
              </Head>
              <Body>
                <VolcanoInfo type="噴火速報">
                  <Item>
                    <EventTime><EventDateTime significant="yyyy-mm-ddThh:mm" dubious="頃">2024-01-14T00:29:00+09:00</EventDateTime></EventTime>
                    <Kind><Name>噴火</Name><Code>52</Code></Kind>
                    <Areas codeType="火山名"><Area><Name>諏訪之瀬島</Name><Code>511</Code></Area></Areas>
                  </Item>
                </VolcanoInfo>
                <VolcanoInfo type="噴火速報（対象市町村等）">
                  <Item>
                    <Kind><Name>噴火</Name><Code>52</Code></Kind>
                    <Areas codeType="気象・地震・火山情報／市町村等"><Area><Name>鹿児島県十島村</Name><Code>4630400</Code></Area></Areas>
                  </Item>
                </VolcanoInfo>
                <VolcanoInfoContent>
                  <VolcanoHeadline>諏訪之瀬島で噴火が発生</VolcanoHeadline>
                  <VolcanoActivity>御岳火口で噴火が発生しました。</VolcanoActivity>
                </VolcanoInfoContent>
              </Body>
            </Report>
            """;

        VolcanoEvent volcano = NormalizeVolcano(xml);

        Assert.AreEqual(VolcanoInformationType.EruptionFlash, volcano.InformationType);
        Assert.AreEqual("VFVO56", volcano.Issue.RawType);
        Assert.AreEqual("諏訪之瀬島", volcano.VolcanoName);
        Assert.AreEqual("鹿児島県十島村", volcano.TargetAreas.Single().Name);
        Assert.AreEqual(
            DateTimeOffset.Parse("2024-01-14T00:29:00+09:00", CultureInfo.InvariantCulture),
            volcano.EventTime);
        Assert.IsTrue(volcano.EventTimeIsApproximate);
        Assert.AreEqual("yyyy-mm-ddThh:mm", volcano.EventTimePrecision);
        Assert.IsFalse(volcano.IsTelegramCancellation);
        Assert.IsFalse(volcano.IsCancelled);
    }

    [TestMethod]
    public void Vfvo50RecognizesNonLevelVolcanoWarningFromStructuredWarningTitle()
    {
        const string xml = """
            <Report>
              <Control><Title>噴火警報・予報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head>
                <Title>火山名  福徳岡ノ場  噴火警報（周辺海域）</Title>
                <ReportDateTime>2026-08-14T12:00:00+09:00</ReportDateTime>
                <EventID>331</EventID><InfoType>発表</InfoType>
                <Headline><Text>福徳岡ノ場に噴火警報（周辺海域）を発表</Text></Headline>
              </Head>
              <Body>
                <VolcanoInfo type="噴火警報・予報（対象火山）">
                  <Item>
                    <Kind><Name>周辺海域警戒</Name><Code>36</Code><Condition>引上げ</Condition></Kind>
                    <LastKind><Name>活火山であることに留意（海底火山）</Name><Code>35</Code><Condition /></LastKind>
                    <Areas codeType="火山名"><Area><Name>福徳岡ノ場</Name><Code>331</Code></Area></Areas>
                  </Item>
                </VolcanoInfo>
                <VolcanoInfoContent><VolcanoHeadline>福徳岡ノ場に噴火警報（周辺海域）を発表</VolcanoHeadline></VolcanoInfoContent>
              </Body>
            </Report>
            """;

        VolcanoEvent volcano = NormalizeVolcano(xml);

        Assert.AreEqual(VolcanoAlertLevel.Unknown, volcano.AlertLevel);
        Assert.AreEqual("周辺海域警戒", volcano.AlertLevelText);
        Assert.AreEqual("36", volcano.AlertLevelCode);
        Assert.IsTrue(volcano.IsWarning);
        Assert.IsFalse(volcano.IsCancelled);
    }

    [TestMethod]
    public void Vfvo50WarningReleaseIsNotTelegramCancellation()
    {
        const string xml = """
            <Report>
              <Control><Title>噴火警報・予報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head>
                <Title>火山名  草津白根山  噴火予報：警報解除</Title>
                <ReportDateTime>2026-08-14T12:00:00+09:00</ReportDateTime>
                <EventID>350</EventID><InfoType>発表</InfoType>
                <Headline><Text>草津白根山に噴火予報：警報解除を発表</Text></Headline>
              </Head>
              <Body>
                <VolcanoInfo type="噴火警報・予報（対象火山）">
                  <Item>
                    <Kind><Name>レベル１（活火山であることに留意）</Name><Code>11</Code><Condition>引下げ</Condition></Kind>
                    <LastKind><Name>レベル２（火口周辺規制）</Name><Code>12</Code><Condition /></LastKind>
                    <Areas codeType="火山名"><Area><Name>草津白根山</Name><Code>350</Code></Area></Areas>
                  </Item>
                </VolcanoInfo>
                <VolcanoInfo type="噴火警報・予報（対象市町村等）">
                  <Item>
                    <Kind><Name>噴火予報：警報解除</Name><Code>04</Code><Condition>解除</Condition></Kind>
                    <LastKind><Name>火口周辺警報</Name><Code>02</Code><Condition /></LastKind>
                    <Areas codeType="気象・地震・火山情報／市町村等"><Area><Name>群馬県草津町</Name><Code>1042600</Code></Area></Areas>
                  </Item>
                </VolcanoInfo>
                <VolcanoInfoContent><VolcanoHeadline>草津白根山に噴火予報：警報解除を発表</VolcanoHeadline></VolcanoInfoContent>
              </Body>
            </Report>
            """;

        VolcanoEvent volcano = NormalizeVolcano(xml);

        Assert.IsTrue(volcano.IsCancelled);
        Assert.IsFalse(volcano.IsTelegramCancellation);
        Assert.IsFalse(volcano.IsWarning);
        Assert.AreEqual("引下げ", volcano.AlertCondition);
        Assert.AreEqual("レベル２（火口周辺規制）", volcano.PreviousAlertLevelText);
        Assert.AreEqual("解除", volcano.TargetAreas.Single().Status);
    }

    [TestMethod]
    public void Vfvo50PartialMunicipalityReleaseDoesNotCancelRemainingWarning()
    {
        const string xml = """
            <Report>
              <Control><Title>噴火警報・予報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head>
                <Title>火山名  桜島  火口周辺警報</Title>
                <ReportDateTime>2026-08-14T12:00:00+09:00</ReportDateTime>
                <EventID>506</EventID><InfoType>発表</InfoType>
                <Headline><Text>桜島の火口周辺警報を更新</Text></Headline>
              </Head>
              <Body>
                <VolcanoInfo type="噴火警報・予報（対象火山）">
                  <Item><Kind><Name>レベル３（入山規制）</Name><Code>13</Code><Condition>継続</Condition></Kind><Areas codeType="火山名"><Area><Name>桜島</Name><Code>506</Code></Area></Areas></Item>
                </VolcanoInfo>
                <VolcanoInfo type="噴火警報・予報（対象市町村等）">
                  <Item><Kind><Name>火口周辺警報</Name><Code>02</Code><Condition>継続</Condition></Kind><Areas codeType="気象・地震・火山情報／市町村等"><Area><Name>鹿児島市</Name><Code>4620100</Code></Area></Areas></Item>
                  <Item><Kind><Name>噴火予報：警報解除</Name><Code>04</Code><Condition>解除</Condition></Kind><LastKind><Name>火口周辺警報</Name><Code>02</Code></LastKind><Areas codeType="気象・地震・火山情報／市町村等"><Area><Name>垂水市</Name><Code>4621400</Code></Area></Areas></Item>
                </VolcanoInfo>
              </Body>
            </Report>
            """;

        VolcanoEvent volcano = NormalizeVolcano(xml);

        Assert.IsFalse(volcano.IsCancelled);
        Assert.IsTrue(volcano.IsWarning);
        Assert.HasCount(2, volcano.TargetAreas);
    }

    [TestMethod]
    public void Vfvo56CancellationUsesBodyTextAndDoesNotInventOccurrenceTime()
    {
        const string xml = """
            <Report>
              <Control><Title>噴火速報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head>
                <Title>火山名　御嶽山　噴火速報</Title>
                <ReportDateTime>2014-09-27T12:10:00+09:00</ReportDateTime>
                <TargetDateTime>2014-09-27T11:53:00+09:00</TargetDateTime>
                <EventID>20140927120000_312</EventID><InfoType>取消</InfoType>
              </Head>
              <Body><Text>先に発表した御嶽山の噴火速報は取り消します。</Text></Body>
            </Report>
            """;

        VolcanoEvent volcano = NormalizeVolcano(xml);

        Assert.IsTrue(volcano.IsCancelled);
        Assert.IsTrue(volcano.IsTelegramCancellation);
        Assert.AreEqual("取消", volcano.Issue.InformationType);
        Assert.IsNull(volcano.EventTime);
        Assert.AreEqual("先に発表した御嶽山の噴火速報は取り消します。", volcano.BodyText);
        Assert.AreEqual(volcano.BodyText, volcano.Headline);
    }

    [TestMethod]
    public void Vfvo56CorrectionPreservesCorrectionText()
    {
        const string xml = """
            <Report>
              <Control><Title>噴火速報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head>
                <Title>火山名　御嶽山　噴火速報</Title>
                <ReportDateTime>2014-09-27T12:10:00+09:00</ReportDateTime>
                <EventID>20140927120000_312</EventID><InfoType>訂正</InfoType>
              </Head>
              <Body>
                <VolcanoInfo type="噴火速報"><Item><EventTime><EventDateTime significant="yyyy-mm-ddThh:mm" dubious="頃">2014-09-27T11:52:00+09:00</EventDateTime></EventTime><Kind><Name>噴火したもよう</Name><Code>62</Code></Kind><Areas codeType="火山名"><Area><Name>御嶽山</Name><Code>312</Code></Area></Areas></Item></VolcanoInfo>
                <VolcanoInfoContent>
                  <VolcanoHeadline>御嶽山で噴火が発生したもよう</VolcanoHeadline>
                  <Text>噴火時刻を１１時５２分に修正</Text>
                </VolcanoInfoContent>
              </Body>
            </Report>
            """;

        VolcanoEvent volcano = NormalizeVolcano(xml);

        Assert.AreEqual(CorrectionType.Generic, volcano.Issue.Correction);
        Assert.AreEqual("噴火時刻を１１時５２分に修正", volcano.ContentText);
    }

    [TestMethod]
    public void Vxse53NormalizesQuakePointsAndTsunamiComment()
    {
        const string xml = """
            <Report xmlns:jmx_eb="http://xml.kishou.go.jp/jmaxml1/elementBasis1/">
              <Control>
                <Title>震源・震度に関する情報</Title>
                <Status>通常</Status>
                <PublishingOffice>気象庁</PublishingOffice>
              </Control>
              <Head>
                <ReportDateTime>2026-08-01T12:00:00+09:00</ReportDateTime>
                <EventID>20260801120000</EventID>
                <Serial>2</Serial>
                <InfoType>発表</InfoType>
              </Head>
              <Body>
                <Earthquake>
                  <OriginTime>2026-08-01T11:59:00+09:00</OriginTime>
                  <Hypocenter>
                    <Area>
                      <Name>能登半島沖</Name>
                      <jmx_eb:Coordinate>+37.5+137.2-10000/</jmx_eb:Coordinate>
                    </Area>
                  </Hypocenter>
                  <jmx_eb:Magnitude>4.8</jmx_eb:Magnitude>
                </Earthquake>
                <Intensity>
                  <Observation>
                    <MaxInt>4</MaxInt>
                    <Pref><Name>石川県</Name><Area><Name>能登</Name><City><Name>珠洲市</Name><MaxInt>4</MaxInt></City></Area></Pref>
                  </Observation>
                </Intensity>
                <Comments>
                  <ForecastComment>この地震による津波の心配はありません。</ForecastComment>
                </Comments>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "dmdata.jp",
            xml,
            SourceMode.Production,
            DateTimeOffset.Parse("2026-08-01T03:00:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.IsTrue(result.IsSuccess);
        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.AreEqual(QuakeIssueType.DetailScale, quake.IssueType);
        Assert.AreEqual(JmaScale.Four, quake.Earthquake.MaximumScale);
        Assert.AreEqual(DomesticTsunami.None, quake.Earthquake.DomesticTsunami);
        Assert.HasCount(1, quake.Points);
        Assert.AreEqual("石川県珠洲市", quake.Points[0].DisplayName);
    }

    [TestMethod]
    public void Vxse52DisplaysMagnitudeDescriptionWhenMagnitudeIsUnknown()
    {
        const string xml = """
            <Report xmlns:jmx_eb="http://xml.kishou.go.jp/jmaxml1/elementBasis1/">
              <Control>
                <Title>震源に関する情報</Title>
                <Status>訓練</Status>
                <PublishingOffice>気象庁</PublishingOffice>
              </Control>
              <Head>
                <ReportDateTime>2024-06-13T12:01:00+09:00</ReportDateTime>
                <EventID>20240613120000</EventID>
                <Serial>1</Serial>
                <InfoType>発表</InfoType>
              </Head>
              <Body>
                <Earthquake>
                  <OriginTime>2024-06-13T12:00:00+09:00</OriginTime>
                  <Hypocenter>
                    <Area>
                      <Name>岐阜県美濃中西部</Name>
                      <jmx_eb:Coordinate>+35.4+136.7-10000/</jmx_eb:Coordinate>
                    </Area>
                  </Hypocenter>
                  <jmx_eb:Magnitude type="Mj" condition="不明"
                                    description="Ｍ８を超える巨大地震">NaN</jmx_eb:Magnitude>
                </Earthquake>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "test-library-jma-xml",
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2024-06-13T03:01:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.IsTrue(result.IsSuccess);
        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.AreEqual(QuakeIssueType.Destination, quake.IssueType);
        Assert.IsNull(quake.Earthquake.Hypocenter?.Magnitude);
        Assert.AreEqual(
            "Ｍ８を超える巨大地震",
            quake.Earthquake.Hypocenter?.MagnitudeDescription);

        DisplayProgram display = new PageComposer().Compose(
            quake,
            AppSettings.CreateDefault().Display);
        Assert.IsTrue(display.Pages
            .SelectMany(static page => page.Blocks)
            .Any(static block =>
                block.PrimaryText.Contains("Ｍ８を超える巨大地震", StringComparison.Ordinal) ||
                block.SecondaryText.Contains("Ｍ８を超える巨大地震", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Vxse53PreservesEewNoStrongShakingFixedComment()
    {
        const string xml = """
            <Report xmlns:jmx_eb="http://xml.kishou.go.jp/jmaxml1/elementBasis1/">
              <Control><Title>震源・震度に関する情報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head><ReportDateTime>2026-08-01T12:00:00+09:00</ReportDateTime><EventID>20260801120000</EventID><Serial>1</Serial><InfoType>発表</InfoType></Head>
              <Body>
                <Earthquake>
                  <OriginTime>2026-08-01T11:59:00+09:00</OriginTime>
                  <Hypocenter><Area><Name>和歌山県北部</Name><jmx_eb:Coordinate>+34.2+135.3-10000/</jmx_eb:Coordinate></Area></Hypocenter>
                  <jmx_eb:Magnitude>2.3</jmx_eb:Magnitude>
                </Earthquake>
                <Comments>
                  <ForecastComment codeType="固定付加文">
                    <Text>この地震による津波の心配はありません。
            この地震で緊急地震速報を発表しましたが、強い揺れは観測されませんでした。</Text>
                    <Code>0215 0245</Code>
                  </ForecastComment>
                </Comments>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "dmdata.jp",
            xml,
            SourceMode.Production,
            DateTimeOffset.Parse("2026-08-01T03:00:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.IsTrue(result.IsSuccess);
        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.AreEqual(DomesticTsunami.None, quake.Earthquake.DomesticTsunami);
        Assert.HasCount(0, quake.Points);
        Assert.AreEqual(
            "この地震で緊急地震速報を発表しましたが、強い揺れは観測されませんでした。",
            quake.FreeFormComment);
    }

    [TestMethod]
    public void Vxse62NormalizesLongPeriodClassWithoutDisplayingExplanatoryComment()
    {
        const string xml = """
            <Report>
              <Control>
                <Title>長周期地震動に関する観測情報</Title>
                <Status>通常</Status>
                <PublishingOffice>気象庁</PublishingOffice>
              </Control>
              <Head>
                <ReportDateTime>2026-08-01T11:50:00+09:00</ReportDateTime>
                <EventID>20260801114813</EventID>
                <InfoType>発表</InfoType>
              </Head>
              <Body>
                <Earthquake>
                  <OriginTime>2026-08-01T11:48:00+09:00</OriginTime>
                </Earthquake>
                <Intensity>
                  <Observation>
                    <MaxInt>4</MaxInt>
                    <MaxLgInt>1</MaxLgInt>
                    <Pref>
                      <Name>青森県</Name>
                      <MaxInt>4</MaxInt>
                      <MaxLgInt>1</MaxLgInt>
                      <Area>
                        <Name>青森県津軽北部</Name>
                        <MaxInt>4</MaxInt>
                        <MaxLgInt>1</MaxLgInt>
                        <City>
                          <Name>青森市</Name>
                          <IntensityStation>
                            <Name>青森市花園</Name><Int>3</Int><LgInt>1</LgInt>
                          </IntensityStation>
                        </City>
                      </Area>
                    </Pref>
                  </Observation>
                </Intensity>
                <Comments>
                  <FreeFormComment>
                    各長周期地震動階級に対する簡易な現象表現
                    https://www.data.jma.go.jp/eew/data/ltpgm/event.php
                  </FreeFormComment>
                </Comments>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "nii-jma-xml",
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2026-08-01T02:50:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.AreEqual(QuakeIssueType.LongPeriodObservation, quake.IssueType);
        Assert.IsNotNull(quake.LongPeriodIntensity);
        Assert.AreEqual(1, quake.LongPeriodIntensity.MaximumClass);
        Assert.HasCount(1, quake.LongPeriodIntensity.Areas);
        Assert.AreEqual("青森県", quake.LongPeriodIntensity.Areas[0].Prefecture);
        Assert.AreEqual("青森県津軽北部", quake.LongPeriodIntensity.Areas[0].Area);
        Assert.AreEqual(1, quake.LongPeriodIntensity.Areas[0].Class);
        Assert.AreEqual(string.Empty, quake.FreeFormComment);

        DisplayProgram program = new PageComposer().Compose(
            quake,
            AppSettings.CreateDefault().Display);
        Assert.IsFalse(program.Pages.Any(page =>
            page.AccessibleText.Contains("data.jma.go.jp", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DmdataVxse51UsesTargetTimeAndShowsTsunamiCaution()
    {
        const string xml = """
            <Report>
              <Control><Title>震度速報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head>
                <ReportDateTime>2025-12-02T23:47:00+09:00</ReportDateTime>
                <TargetDateTime>2025-12-02T23:44:00+09:00</TargetDateTime>
                <EventID>20251202234457</EventID><InfoType>発表</InfoType>
              </Head>
              <Body>
                <Intensity><Observation>
                  <MaxInt>4</MaxInt>
                  <Pref><Name>静岡県</Name><MaxInt>4</MaxInt>
                    <Area><Name>静岡県東部</Name><MaxInt>4</MaxInt></Area>
                    <Area><Name>静岡県中部</Name><MaxInt>3</MaxInt></Area>
                  </Pref>
                </Observation></Intensity>
                <Comments>
                  <ForecastComment><Text>今後の情報に注意してください。</Text><Code>0217</Code></ForecastComment>
                  <FreeFormComment><Text>固定追加情報</Text><Code>9999</Code></FreeFormComment>
                </Comments>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "dmdata.jp",
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2025-12-02T14:47:13Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.AreEqual(QuakeIssueType.ScalePrompt, quake.IssueType);
        Assert.AreEqual(
            DateTimeOffset.Parse("2025-12-02T23:44:00+09:00", CultureInfo.InvariantCulture),
            quake.Earthquake.OriginTime);
        Assert.AreEqual(JmaScale.Four, quake.Earthquake.MaximumScale);
        Assert.AreEqual(DomesticTsunami.Unknown, quake.Earthquake.DomesticTsunami);
        Assert.HasCount(2, quake.Points);
        Assert.IsTrue(quake.Points.All(static point => point.IsArea));
        Assert.AreEqual("固定追加情報", quake.FreeFormComment);
        Assert.DoesNotContain("9999", quake.FreeFormComment);

        DisplayProgram program = new PageComposer().Compose(
            quake,
            AppSettings.CreateDefault().Display);
        StringAssert.StartsWith(program.Pages[0].AccessibleText, "23時44分頃");
        Assert.IsTrue(program.Pages
            .SelectMany(static page => page.Blocks)
            .Any(static block => block.Badge == "震度4"));
        Assert.IsTrue(program.Pages
            .SelectMany(static page => page.Blocks)
            .Any(static block => block.PrimaryText == "念のため津波に注意してください。"));
    }

    [TestMethod]
    public void Vxse51CancellationIsNormalizedAndUsesOnlyCancellationPage()
    {
        const string xml = """
            <Report>
              <Control><Title>震度速報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head>
                <ReportDateTime>2024-01-01T14:13:47+09:00</ReportDateTime>
                <TargetDateTime>2024-01-01T14:03:00+09:00</TargetDateTime>
                <EventID>20240101140300</EventID><InfoType>取消</InfoType>
                <Headline><Text>震度速報を取り消します。</Text></Headline>
              </Head>
              <Body />
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "dmdata.jp",
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2024-01-01T05:13:48Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.IsTrue(quake.IsCancelled);
        Assert.AreEqual("取消", quake.Issue.InformationType);
        Assert.AreEqual(QuakeIssueType.ScalePrompt, quake.IssueType);

        DisplayProgram program = new PageComposer().Compose(
            quake,
            AppSettings.CreateDefault().Display);
        Assert.HasCount(1, program.Pages);
        Assert.AreEqual("先ほどの、震度速報を取り消します", program.Pages[0].AccessibleText);
    }

    [TestMethod]
    public void Vxse43CancellationKeepsSerialAndTrainingFlag()
    {
        const string xml = """
            <Report>
              <Control><Title>緊急地震速報（警報）</Title><Status>訓練</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head>
                <ReportDateTime>2026-08-01T12:00:00+09:00</ReportDateTime>
                <EventID>20260801120000</EventID><Serial>3</Serial><InfoType>取消</InfoType>
                <Headline><Text>緊急地震速報を取り消します</Text></Headline>
              </Head>
              <Body><NextAdvisory>この情報をもって終了します。</NextAdvisory></Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "dmdata.jp",
            xml,
            SourceMode.Sandbox,
            DateTimeOffset.Parse("2026-08-01T03:00:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        EewEvent eew = Assert.IsInstanceOfType<EewEvent>(result.Event);
        Assert.IsTrue(eew.IsCancelled);
        Assert.IsTrue(eew.IsFinal);
        Assert.IsTrue(eew.IsTest);
        Assert.AreEqual("3", eew.Issue.Serial);
        Assert.AreEqual("取消", eew.Issue.InformationType);
    }

    [TestMethod]
    public void ProductionCorrectionTelegramCreatesCorrectionDisplay()
    {
        const string xml = """
            <Report xmlns:jmx_eb="http://xml.kishou.go.jp/jmaxml1/elementBasis1/">
              <Control>
                <Title>震源・震度に関する情報</Title>
                <Status>通常</Status>
                <PublishingOffice>気象庁</PublishingOffice>
              </Control>
              <Head>
                <ReportDateTime>2026-08-02T14:00:00+09:00</ReportDateTime>
                <EventID>20260802134400</EventID>
                <Serial>2</Serial>
                <InfoType>訂正</InfoType>
              </Head>
              <Body>
                <Earthquake>
                  <OriginTime>2026-08-02T13:44:00+09:00</OriginTime>
                  <Hypocenter>
                    <Area>
                      <Name>相模湾</Name>
                      <jmx_eb:Coordinate>+35.0+139.3-30000/</jmx_eb:Coordinate>
                    </Area>
                  </Hypocenter>
                  <jmx_eb:Magnitude>5.8</jmx_eb:Magnitude>
                </Earthquake>
                <Intensity><Observation><MaxInt>4</MaxInt></Observation></Intensity>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "dmdata.jp",
            xml,
            SourceMode.Production,
            DateTimeOffset.Parse("2026-08-02T05:00:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.IsTrue(quake.IsCorrection);
        Assert.AreEqual(CorrectionType.Generic, quake.Issue.Correction);
        DisplayProgram program = new PageComposer().Compose(
            quake,
            AppSettings.CreateDefault().Display);
        DisplayBlock correction = program.Pages
            .SelectMany(static page => page.Blocks)
            .Single(static block => block.StyleToken == DisplayStyleTokens.Correction);
        Assert.AreEqual("訂正", correction.Badge);
        Assert.AreEqual("内容を訂正します", correction.PrimaryText);
    }

    [TestMethod]
    public void Vpww54NormalizesMunicipalityWarningsAndReleaseState()
    {
        const string xml = """
            <Report>
              <Control>
                <Title>気象警報・注意報（Ｈ２７）</Title>
                <Status>通常</Status>
                <PublishingOffice>熊本地方気象台</PublishingOffice>
              </Control>
              <Head>
                <ReportDateTime>2026-08-10T10:15:00+09:00</ReportDateTime>
                <InfoType>発表</InfoType>
                <Headline><Text>熊本県では大雨に警戒してください。</Text></Headline>
              </Head>
              <Body>
                <Warning type="気象警報・注意報（市町村等）">
                  <Item>
                    <Kind><Name>大雨特別警報</Name><Code>33</Code><Status>発表</Status></Kind>
                    <Areas><Area><Name>熊本市</Name><Code>4310000</Code></Area></Areas>
                  </Item>
                  <Item>
                    <Kind><Name>雷注意報</Name><Code>14</Code><Status>継続</Status></Kind>
                    <Areas><Area><Name>八代市</Name><Code>4320200</Code></Area></Areas>
                  </Item>
                  <Item>
                    <Kind><Name>強風注意報</Name><Code>15</Code><Status>解除</Status></Kind>
                    <Areas><Area><Name>天草市</Name><Code>4321500</Code></Area></Areas>
                  </Item>
                </Warning>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "dmdata.jp",
            xml,
            SourceMode.Production,
            DateTimeOffset.Parse("2026-08-10T01:15:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        WeatherWarningEvent weather = Assert.IsInstanceOfType<WeatherWarningEvent>(result.Event);
        Assert.AreEqual(WeatherWarningLevel.SpecialWarning, weather.MaximumLevel);
        Assert.HasCount(3, weather.Items);
        Assert.IsFalse(weather.IsCancelled);
        Assert.IsFalse(weather.Items.Single(item => item.AreaName == "天草市").IsActive);

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
        Assert.AreEqual(OverlayPriority.WeatherSpecialWarning, program.Priority);
        Assert.IsTrue(program.Pages.SelectMany(page => page.Blocks).Any(block =>
            block.StyleToken == DisplayStyleTokens.WeatherSpecialWarning &&
            block.Badge == "大雨特別警報"));
    }

    [TestMethod]
    public void Vpww55DirectAreaReleaseDoesNotLeakHeadlineCodesIntoCaption()
    {
        const string xml = """
            <Report>
              <Control>
                <Title>気象警報・注意報（Ｒ０６）（大雨）</Title>
                <Status>通常</Status>
                <PublishingOffice>宇都宮地方気象台</PublishingOffice>
              </Control>
              <Head>
                <ReportDateTime>2026-08-10T00:47:00+09:00</ReportDateTime>
                <InfoType>発表</InfoType>
                <Headline>
                  <Text>注意報を解除します。</Text>
                  <Information type="気象警報・注意報（府県予報区等）">
                    <Item>
                      <Kind><Name>解除</Name><Code>00</Code></Kind>
                      <Areas><Area><Name>栃木県</Name><Code>090000</Code></Area></Areas>
                    </Item>
                  </Information>
                </Headline>
              </Head>
              <Body>
                <Warning type="気象警報・注意報（市町村等）">
                  <Item>
                    <Kind>
                      <Name>レベル２大雨注意報</Name><Code>10</Code><Status>解除</Status>
                      <LastKind><Name>レベル２大雨注意報</Name><Code>10</Code></LastKind>
                    </Kind>
                    <Area><Name>足利市</Name><Code>0920200</Code></Area>
                  </Item>
                </Warning>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            NiiJmaXmlHistoryMessageSource.ProviderName,
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2026-08-09T15:47:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        WeatherWarningEvent weather = Assert.IsInstanceOfType<WeatherWarningEvent>(result.Event);
        Assert.IsTrue(weather.IsCancelled);
        Assert.AreEqual("注意報を解除します。", weather.Headline);
        Assert.HasCount(1, weather.Items);
        Assert.AreEqual("足利市", weather.Items[0].AreaName);
        Assert.AreEqual("レベル２大雨注意報", weather.Items[0].KindName);
        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
        Assert.AreEqual(
            "栃木県足利市のレベル２大雨注意報は解除されました",
            program.Pages[0].Blocks[0].PrimaryText);
        Assert.DoesNotContain("090000", program.Pages[0].AccessibleText);
        Assert.DoesNotContain("0920200", program.Pages[0].AccessibleText);
    }

    [TestMethod]
    public void Vpww56MixedNoWarningStatusesKeepsActiveMunicipalityAlert()
    {
        const string xml = """
            <Report>
              <Control>
                <Title>気象警報・注意報（Ｒ０６）（土砂）</Title>
                <Status>通常</Status>
                <PublishingOffice>山形地方気象台</PublishingOffice>
              </Control>
              <Head>
                <ReportDateTime>2026-08-10T16:14:00+09:00</ReportDateTime>
                <InfoType>発表</InfoType>
                <Headline>
                  <Text>最上では、土砂災害に注意してください。</Text>
                  <Information type="気象警報・注意報（府県予報区等）">
                    <Item>
                      <Kind><Name>レベル２土砂災害注意報</Name><Code>29</Code></Kind>
                      <Areas><Area><Name>山形県</Name><Code>060000</Code></Area></Areas>
                    </Item>
                  </Information>
                </Headline>
              </Head>
              <Body>
                <Warning type="気象警報・注意報（一次細分区域等）">
                  <Item>
                    <Kind><Status>発表警報・注意報はなし</Status></Kind>
                    <Area><Name>村山</Name><Code>060010</Code></Area>
                  </Item>
                </Warning>
                <Warning type="気象警報・注意報（市町村等）">
                  <Item>
                    <Kind><Name>レベル２土砂災害注意報</Name><Code>29</Code><Status>発表</Status></Kind>
                    <Area><Name>最上町</Name><Code>0636200</Code></Area>
                  </Item>
                  <Item>
                    <Kind><Status>発表警報・注意報はなし</Status></Kind>
                    <Area><Name>山形市</Name><Code>0620100</Code></Area>
                  </Item>
                </Warning>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            NiiJmaXmlHistoryMessageSource.ProviderName,
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2026-08-10T07:14:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        WeatherWarningEvent weather = Assert.IsInstanceOfType<WeatherWarningEvent>(result.Event);
        Assert.IsFalse(weather.IsCancelled);
        Assert.AreEqual("最上では、土砂災害に注意してください。", weather.Headline);
        Assert.HasCount(1, weather.Items);
        Assert.AreEqual("最上町", weather.Items[0].AreaName);
        Assert.AreEqual("レベル２土砂災害注意報", weather.Items[0].KindName);
        Assert.IsTrue(weather.Items[0].IsActive);
        DisplayBlock block = new PageComposer()
            .Compose(weather, AppSettings.CreateDefault().Display)
            .Pages[0]
            .Blocks[0];
        Assert.AreEqual("レベル２土砂災害注意報", block.Badge);
        Assert.AreEqual("山形県　最上町　新たに発表", block.PrimaryText);
        Assert.DoesNotContain("060000", block.PrimaryText);
        Assert.DoesNotContain("0636200", block.PrimaryText);
    }

    [TestMethod]
    public void Vpww58ReorganizedTelegramIsRecognized()
    {
        const string xml = """
            <Report>
              <Control>
                <Title>気象警報・注意報（Ｒ０６）（暴風）</Title>
                <Status>通常</Status>
                <PublishingOffice>札幌管区気象台</PublishingOffice>
              </Control>
              <Head>
                <ReportDateTime>2026-08-10T12:00:00+09:00</ReportDateTime>
                <InfoType>発表</InfoType>
              </Head>
              <Body>
                <MeteorologicalInfos type="気象警報・注意報">
                  <MeteorologicalInfo>
                    <Item>
                      <Kind><Name>暴風警報</Name><Code>05</Code><Status>発表</Status></Kind>
                      <Areas><Area><Name>札幌市</Name><Code>0110000</Code></Area></Areas>
                    </Item>
                  </MeteorologicalInfo>
                </MeteorologicalInfos>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "dmdata.jp",
            xml,
            SourceMode.Production,
            DateTimeOffset.Parse("2026-08-10T03:00:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        WeatherWarningEvent weather = Assert.IsInstanceOfType<WeatherWarningEvent>(result.Event);
        Assert.AreEqual("VPWW58", weather.Issue.RawType);
        Assert.AreEqual(WeatherWarningLevel.Warning, weather.MaximumLevel);
        Assert.AreEqual("札幌市", weather.Items.Single().AreaName);
    }

    [TestMethod]
    [DataRow("VPWW58", "暴風", "暴風特別警報", WeatherWarningLevel.SpecialWarning)]
    [DataRow("VPWW59", "波浪", "波浪警報", WeatherWarningLevel.Warning)]
    [DataRow("VPWW61", "その他", "雷注意報", WeatherWarningLevel.Advisory)]
    [DataRow("VPWW55", "大雨", "レベル５大雨特別警報", WeatherWarningLevel.SpecialWarning)]
    [DataRow("VPWW55", "大雨", "レベル４大雨危険警報", WeatherWarningLevel.Warning)]
    [DataRow("VPWW55", "大雨", "レベル３大雨警報", WeatherWarningLevel.Warning)]
    [DataRow("VPWW55", "大雨", "レベル２大雨注意報", WeatherWarningLevel.Advisory)]
    [DataRow("VPWW56", "土砂", "レベル５土砂災害特別警報", WeatherWarningLevel.SpecialWarning)]
    [DataRow("VPWW56", "土砂", "レベル４土砂災害危険警報", WeatherWarningLevel.Warning)]
    [DataRow("VPWW56", "土砂", "レベル３土砂災害警報", WeatherWarningLevel.Warning)]
    [DataRow("VPWW56", "土砂", "レベル２土砂災害注意報", WeatherWarningLevel.Advisory)]
    [DataRow("VPWW57", "高潮", "レベル５高潮特別警報", WeatherWarningLevel.SpecialWarning)]
    [DataRow("VPWW57", "高潮", "レベル４高潮危険警報", WeatherWarningLevel.Warning)]
    [DataRow("VPWW57", "高潮", "レベル３高潮警報", WeatherWarningLevel.Warning)]
    [DataRow("VPWW57", "高潮", "レベル２高潮注意報", WeatherWarningLevel.Advisory)]
    public void ReorganizedAlertLevelNamesAreClassified(
        string telegramType,
        string category,
        string warningName,
        WeatherWarningLevel expectedLevel)
    {
        string xml = $$"""
            <Report>
              <Control>
                <Title>気象警報・注意報（Ｒ０６）（{{category}}）</Title>
                <Status>通常</Status>
                <PublishingOffice>気象庁</PublishingOffice>
              </Control>
              <Head>
                <EventID>weather-level-test-{{telegramType}}</EventID>
                <ReportDateTime>2026-08-10T12:00:00+09:00</ReportDateTime>
                <InfoType>発表</InfoType>
              </Head>
              <Body>
                <MeteorologicalInfos type="気象警報・注意報">
                  <MeteorologicalInfo>
                    <Item>
                      <Kind><Name>{{warningName}}</Name><Code>test</Code><Status>発表</Status></Kind>
                      <Areas><Area><Name>東京都</Name><Code>130000</Code></Area></Areas>
                    </Item>
                  </MeteorologicalInfo>
                </MeteorologicalInfos>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "dmdata.jp",
            xml,
            SourceMode.Production,
            DateTimeOffset.Parse("2026-08-10T03:00:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        WeatherWarningEvent weather =
            Assert.IsInstanceOfType<WeatherWarningEvent>(result.Event);
        Assert.AreEqual(telegramType, weather.Issue.RawType);
        Assert.AreEqual(expectedLevel, weather.MaximumLevel);
        Assert.AreEqual(warningName, weather.Items.Single().KindName);
        Assert.IsTrue(weather.Items.Single().IsActive);
        Assert.IsFalse(weather.IsCancelled);
    }

    [TestMethod]
    public void UnknownActiveWeatherKindIsReportedAsValidationIssue()
    {
        const string xml = """
            <Report>
              <Control><Title>気象警報・注意報（Ｒ０６）（大雨）</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice><Type>VPWW55</Type></Control>
              <Head><EventID>weather-unknown</EventID><ReportDateTime>2026-08-10T12:00:00+09:00</ReportDateTime><InfoType>発表</InfoType></Head>
              <Body><MeteorologicalInfos type="気象警報・注意報"><MeteorologicalInfo><Item>
                <Kind><Name>将来追加された警戒情報</Name><Code>future</Code><Status>発表</Status></Kind>
                <Areas><Area><Name>東京都</Name><Code>130000</Code></Area></Areas>
              </Item></MeteorologicalInfo></MeteorologicalInfos></Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "axis",
            xml,
            SourceMode.Production,
            DateTimeOffset.Parse("2026-08-10T03:00:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.AreEqual(NormalizeStatus.Success, result.Status);
        Assert.HasCount(1, result.Issues);
        Assert.Contains("未知", result.Issues[0].Message);
        WeatherWarningEvent weather = Assert.IsInstanceOfType<WeatherWarningEvent>(result.Event);
        Assert.AreEqual(WeatherWarningLevel.Unknown, weather.Items.Single().Level);
    }

    [TestMethod]
    public void ReorganizedWarningReleaseXmlComposesMunicipalitySpecificCaption()
    {
        const string xml = """
            <Report>
              <Control>
                <Title>気象警報・注意報（Ｒ０６）（大雨）</Title>
                <Status>通常</Status>
                <PublishingOffice>熊本地方気象台</PublishingOffice>
              </Control>
              <Head>
                <EventID>weather-release-vpww55</EventID>
                <ReportDateTime>2026-08-11T00:00:00+09:00</ReportDateTime>
                <InfoType>発表</InfoType>
              </Head>
              <Body>
                <MeteorologicalInfos type="気象警報・注意報">
                  <MeteorologicalInfo>
                    <Item>
                      <Kind><Name>レベル４大雨危険警報</Name><Code>L4</Code><Status>解除</Status></Kind>
                      <Areas><Area><Name>熊本市</Name><Code>4310000</Code></Area></Areas>
                    </Item>
                  </MeteorologicalInfo>
                </MeteorologicalInfos>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            NiiJmaXmlHistoryMessageSource.ProviderName,
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2026-08-10T15:00:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        WeatherWarningEvent weather = Assert.IsInstanceOfType<WeatherWarningEvent>(result.Event);
        Assert.IsTrue(weather.IsCancelled);
        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
        Assert.AreEqual(
            "熊本県熊本市のレベル４大雨危険警報は解除されました",
            program.Pages[0].Blocks[0].PrimaryText);
    }

    [TestMethod]
    public void Vpww54FallsBackWhenMunicipalityContainerHasNoDisplayableItems()
    {
        const string xml = """
            <Report>
              <Control>
                <Title>気象警報・注意報（Ｈ２７）</Title>
                <Status>通常</Status>
                <PublishingOffice>仙台管区気象台</PublishingOffice>
              </Control>
              <Head>
                <ReportDateTime>2026-08-10T13:00:00+09:00</ReportDateTime>
                <InfoType>発表</InfoType>
              </Head>
              <Body>
                <Warning type="気象警報・注意報（市町村等）" />
                <Warning type="気象警報・注意報（府県予報区）">
                  <Item>
                    <Kind><Name>大雨警報</Name><Code>03</Code><Status>発表</Status></Kind>
                    <Areas><Area><Name>宮城県</Name><Code>040000</Code></Area></Areas>
                  </Item>
                </Warning>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "axis",
            xml,
            SourceMode.Production,
            DateTimeOffset.Parse("2026-08-10T04:00:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        WeatherWarningEvent weather = Assert.IsInstanceOfType<WeatherWarningEvent>(result.Event);
        Assert.HasCount(1, weather.Items);
        Assert.AreEqual("宮城県", weather.Items[0].AreaName);
        Assert.AreEqual("大雨警報", weather.Items[0].KindName);
    }

    [TestMethod]
    public void Vpww54WithoutDisplayableItemsIsIgnoredInsteadOfInvalid()
    {
        const string xml = """
            <Report>
              <Control>
                <Title>気象警報・注意報（Ｈ２７）</Title>
                <Status>通常</Status>
                <PublishingOffice>気象庁</PublishingOffice>
              </Control>
              <Head>
                <ReportDateTime>2026-08-10T13:00:00+09:00</ReportDateTime>
                <InfoType>発表</InfoType>
              </Head>
              <Body>
                <Warning type="気象警報・注意報（市町村等）" />
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "axis",
            xml,
            SourceMode.Production,
            DateTimeOffset.Parse("2026-08-10T04:00:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.AreEqual(NormalizeStatus.Ignored, result.Status);
        Assert.IsNull(result.Event);
        Assert.IsTrue(result.Issues.Any(issue =>
            issue.Message.Contains("no displayable warning area item", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Vpww54WithoutBodyRemainsInvalid()
    {
        const string xml = """
            <Report>
              <Control>
                <Title>気象警報・注意報（Ｈ２７）</Title>
                <Status>通常</Status>
                <PublishingOffice>気象庁</PublishingOffice>
              </Control>
              <Head>
                <ReportDateTime>2026-08-10T13:00:00+09:00</ReportDateTime>
                <InfoType>発表</InfoType>
              </Head>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "axis",
            xml,
            SourceMode.Production,
            DateTimeOffset.Parse("2026-08-10T04:00:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.AreEqual(NormalizeStatus.Invalid, result.Status);
        Assert.IsNull(result.Event);
    }

    [TestMethod]
    public void AxisVpoa50NormalizesRecordShortDurationHeavyRain()
    {
        const string xml = """
            <Report uuid="20260808095051_0_VPOA50_160000">
              <Control><Title>記録的短時間大雨情報</Title><Status>通常</Status><PublishingOffice>富山地方気象台</PublishingOffice></Control>
              <Head>
                <ReportDateTime>2026-08-10T12:00:00+09:00</ReportDateTime>
                <EventID>JPKS260001</EventID><InfoType>発表</InfoType>
                <Title>富山県記録的短時間大雨情報</Title>
                <Headline><Text>１８時４０分、富山県富山市山間部西で記録的短時間大雨。富山市八尾町丸山で１時間に１０９ミリ。富山市山間部西付近で１時間に約１００ミリ。猛烈な雨が降っており、災害発生の危険度が急激に高まっています。</Text></Headline>
              </Head>
              <Body><MeteorologicalInfos><Item><Areas><Area><Name>富山県</Name><Code>160000</Code></Area></Areas></Item></MeteorologicalInfos></Body>
            </Report>
            """;

        WeatherWarningEvent weather = NormalizeAxisWeather(xml);

        Assert.AreEqual(WeatherInformationType.RecordShortDurationHeavyRain, weather.InformationType);
        Assert.AreEqual("VPOA50", weather.Issue.RawType);
        Assert.AreEqual("富山県", weather.Items.Single().AreaName);
        StringAssert.Contains(weather.Headline, "富山市八尾町丸山で１時間に１０９ミリ");
        StringAssert.Contains(weather.Headline, "災害発生の危険度が急激に高まっています");
    }

    [TestMethod]
    public void AxisVpbs50NormalizesDisasterPreventionBulletin()
    {
        const string xml = """
            <Report uuid="20260810030100_0_VPBS50_120000">
              <Control><Title>府県気象防災速報</Title><Status>通常</Status><PublishingOffice>銚子地方気象台</PublishingOffice></Control>
              <Head>
                <ReportDateTime>2026-08-10T12:01:00+09:00</ReportDateTime>
                <EventID>JPDC260001</EventID><InfoType>発表</InfoType>
                <Title>千葉県気象防災速報（線状降水帯発生）</Title>
                <Headline><Text>千葉県で線状降水帯が発生しました。</Text></Headline>
              </Head>
              <Body><MeteorologicalInfos><Item><Areas><Area><Name>千葉県</Name><Code>120000</Code></Area></Areas></Item></MeteorologicalInfos></Body>
            </Report>
            """;

        WeatherWarningEvent weather = NormalizeAxisWeather(xml);

        Assert.AreEqual(WeatherInformationType.DisasterPreventionBulletin, weather.InformationType);
        Assert.AreEqual("VPBS50", weather.Issue.RawType);
        StringAssert.Contains(weather.Items.Single().KindName, "線状降水帯発生");
    }

    [TestMethod]
    public void Vpbs51NormalizesTidalDisasterPreventionBulletinWithoutMetadataType()
    {
        const string xml = """
            <Report>
              <Control><Title>府県気象防災速報（潮位）</Title><Status>通常</Status><PublishingOffice>富山地方気象台</PublishingOffice></Control>
              <Head>
                <ReportDateTime>2026-08-10T12:05:00+09:00</ReportDateTime>
                <EventID>JPTD260001</EventID><InfoType>発表</InfoType>
                <Title>富山県気象防災速報（潮位）</Title>
                <Headline><Text>富山県の沿岸で顕著な海面昇降が発生しています。海岸付近の低地での浸水に警戒してください。</Text></Headline>
              </Head>
              <Body><MeteorologicalInfos type="観測実況"><MeteorologicalInfo><Item>
                <Kind><Property><Type>潮位の実況</Type></Property></Kind>
                <Areas><Area><Name>富山県</Name><Code>160000</Code></Area></Areas>
              </Item></MeteorologicalInfo></MeteorologicalInfos></Body>
            </Report>
            """;

        WeatherWarningEvent weather = NormalizeAxisWeather(xml);

        Assert.AreEqual(WeatherInformationType.DisasterPreventionBulletin, weather.InformationType);
        Assert.AreEqual("VPBS51", weather.Issue.RawType);
        Assert.AreEqual("富山県", weather.Items.Single().AreaName);
        StringAssert.Contains(weather.Items.Single().KindName, "潮位");
        StringAssert.Contains(weather.Headline, "顕著な海面昇降");

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
        Assert.IsTrue(program.Pages
            .SelectMany(static page => page.Blocks)
            .Any(static block => block.Badge == "気象防災速報（潮位）"));
        Assert.IsTrue(program.Pages.Any(static page =>
            page.AccessibleText.Contains("顕著な海面昇降", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Vpbs50PreservesRecordRainHeadlineForDedicatedDisplay()
    {
        const string xml = """
            <Report uuid="20260809044951_0_VPBS50_050000">
              <Control><Title>府県気象防災速報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head>
                <Title>秋田県気象防災速報（記録的短時間大雨）</Title>
                <ReportDateTime>2026-08-09T13:49:00+09:00</ReportDateTime>
                <EventID>KJPDB202608091349_202608091349</EventID><InfoType>発表</InfoType>
                <Headline><Text>１３時４０分、秋田県湯沢市で記録的短時間大雨。
            湯沢市付近で１時間に約１００ミリ。
            猛烈な雨が降っており、災害発生の危険度が急激に高まっています。</Text></Headline>
              </Head>
              <Body><MeteorologicalInfos type="観測実況"><MeteorologicalInfo><Item>
                <Kind><Property><Type>雨の実況</Type></Property></Kind>
                <Area><Name>湯沢市</Name><Code>0520700</Code><Status>付近</Status></Area>
              </Item></MeteorologicalInfo></MeteorologicalInfos></Body>
            </Report>
            """;

        WeatherWarningEvent weather = NormalizeAxisWeather(xml);

        Assert.AreEqual(WeatherInformationType.DisasterPreventionBulletin, weather.InformationType);
        Assert.AreEqual("湯沢市", weather.Items.Single().AreaName);
        StringAssert.Contains(weather.Headline, "湯沢市付近で１時間に約１００ミリ");
        StringAssert.Contains(weather.Headline, "災害発生の危険度が急激に高まっています");

        DisplayProgram program = new PageComposer().Compose(
            weather,
            AppSettings.CreateDefault().Display);
        Assert.HasCount(3, program.Pages);
        Assert.IsFalse(program.Pages.Any(static page =>
            page.AccessibleText.Contains("新たに発表", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AxisVphw51NormalizesTornadoAdvisory()
    {
        const string xml = """
            <Report uuid="20260810030200_0_VPHW51_130000">
              <Control><Title>竜巻注意情報（目撃情報付き）</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head>
                <ReportDateTime>2026-08-10T12:02:00+09:00</ReportDateTime>
                <EventID>JPTN260001</EventID><InfoType>発表</InfoType>
                <Title>東京都気象防災速報（竜巻目撃）</Title>
                <Headline><Text>東京都で竜巻などの激しい突風が発生したとみられます。</Text></Headline>
              </Head>
              <Body><MeteorologicalInfos><Item><Areas><Area><Name>東京都</Name><Code>130000</Code></Area></Areas></Item></MeteorologicalInfos></Body>
            </Report>
            """;

        WeatherWarningEvent weather = NormalizeAxisWeather(xml);

        Assert.AreEqual(WeatherInformationType.TornadoAdvisory, weather.InformationType);
        Assert.AreEqual(WeatherWarningLevel.Advisory, weather.MaximumLevel);
        Assert.AreEqual("竜巻注意情報", weather.Items.Single().KindName);
    }

    [TestMethod]
    public void Vphw50PreservesFullHeadlineAndValidDateTime()
    {
        const string xml = """
            <Report uuid="20260809075652_0_VPHW50_090000">
              <Control><Title>竜巻注意情報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head>
                <ReportDateTime>2026-08-09T16:56:00+09:00</ReportDateTime>
                <ValidDateTime>2026-08-09T18:10:00+09:00</ValidDateTime>
                <InfoType>発表</InfoType><Serial>2</Serial>
                <Headline><Text>栃木県南部、北部は、竜巻などの激しい突風が発生しやすい気象状況になっています。空の様子に注意してください。雷や急な風の変化など積乱雲が近づく兆しがある場合には、頑丈な建物内に移動するなど、安全確保に努めてください。落雷、ひょう、急な強い雨にも注意してください。</Text></Headline>
              </Head>
              <Body><Warning type="竜巻注意情報（発表細分）"><Item>
                <Kind><Name>竜巻注意情報</Name><Code>1</Code><Status>発表</Status></Kind>
                <Area><Name>栃木県</Name><Code>090000</Code></Area>
              </Item></Warning></Body>
            </Report>
            """;

        WeatherWarningEvent weather = NormalizeAxisWeather(xml);

        Assert.AreEqual(WeatherInformationType.TornadoAdvisory, weather.InformationType);
        StringAssert.Contains(weather.Headline, "頑丈な建物内に移動するなど、安全確保に努めてください");
        Assert.AreEqual(
            DateTimeOffset.Parse("2026-08-09T18:10:00+09:00", CultureInfo.InvariantCulture),
            weather.ValidUntil);
    }

    [TestMethod]
    public void Vtse41AdvisoryReleaseIsCancelledAndDoesNotRemainAsActiveArea()
    {
        const string xml = """
            <Report uuid="20260811010000_0_VTSE41_010000">
              <Control>
                <Title>津波警報・注意報・予報ａ</Title>
                <Status>通常</Status>
                <PublishingOffice>気象庁</PublishingOffice>
              </Control>
              <Head>
                <ReportDateTime>2026-08-11T01:00:00+09:00</ReportDateTime>
                <EventID>20260811010000</EventID>
                <InfoType>発表</InfoType>
                <Headline><Text>津波注意報を解除しました。</Text></Headline>
              </Head>
              <Body>
                <Tsunami>
                  <Forecast>
                    <Item>
                      <Area><Name>種子島・屋久島地方</Name><Code>771</Code></Area>
                      <Category>
                        <Kind><Name>津波注意報解除</Name><Code>60</Code></Kind>
                        <LastKind><Name>津波注意報</Name><Code>62</Code></LastKind>
                      </Category>
                    </Item>
                  </Forecast>
                </Tsunami>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            NiiJmaXmlHistoryMessageSource.ProviderName,
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2026-08-10T16:00:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.IsTrue(result.IsSuccess);
        TsunamiEvent tsunami = Assert.IsInstanceOfType<TsunamiEvent>(result.Event);
        Assert.IsTrue(tsunami.IsCancelled);
        Assert.HasCount(0, tsunami.Areas);
    }

    [TestMethod]
    public void Vtse41ReleaseWithOnlySeaLevelForecastIsStillCancelled()
    {
        const string xml = """
            <Report uuid="20260811010200_0_VTSE41_010000">
              <Control><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head>
                <ReportDateTime>2026-08-11T01:02:00+09:00</ReportDateTime>
                <EventID>20260811010000</EventID><InfoType>発表</InfoType>
                <Headline><Text>津波注意報を解除しました。</Text></Headline>
              </Head>
              <Body><Tsunami><Forecast><Item>
                <Area><Name>伊豆諸島</Name><Code>320</Code></Area>
                <Category><Kind><Name>津波予報（若干の海面変動）</Name><Code>71</Code></Kind></Category>
              </Item></Forecast></Tsunami></Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            NiiJmaXmlHistoryMessageSource.ProviderName,
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2026-08-10T16:02:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        TsunamiEvent tsunami = Assert.IsInstanceOfType<TsunamiEvent>(result.Event);
        Assert.IsTrue(tsunami.IsCancelled);
    }

    [TestMethod]
    public void Vtse41PartialReleaseKeepsRemainingAdvisoryActive()
    {
        const string xml = """
            <Report uuid="20260811010500_0_VTSE41_010000">
              <Control>
                <Title>津波警報・注意報・予報ａ</Title>
                <Status>通常</Status>
                <PublishingOffice>気象庁</PublishingOffice>
              </Control>
              <Head>
                <ReportDateTime>2026-08-11T01:05:00+09:00</ReportDateTime>
                <EventID>20260811010000</EventID>
                <InfoType>発表</InfoType>
                <Headline><Text>一部の津波警報を解除し、津波注意報を発表しています。</Text></Headline>
              </Head>
              <Body>
                <Tsunami>
                  <Forecast>
                    <Item>
                      <Area><Name>青森県日本海沿岸</Name><Code>191</Code></Area>
                      <Category>
                        <Kind><Name>津波警報解除</Name><Code>50</Code></Kind>
                        <LastKind><Name>津波警報</Name><Code>52</Code></LastKind>
                      </Category>
                    </Item>
                    <Item>
                      <Area><Name>岩手県</Name><Code>210</Code></Area>
                      <Category>
                        <Kind><Name>津波注意報</Name><Code>62</Code></Kind>
                        <LastKind><Name>津波警報</Name><Code>52</Code></LastKind>
                      </Category>
                    </Item>
                  </Forecast>
                </Tsunami>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            NiiJmaXmlHistoryMessageSource.ProviderName,
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2026-08-10T16:05:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.IsTrue(result.IsSuccess);
        TsunamiEvent tsunami = Assert.IsInstanceOfType<TsunamiEvent>(result.Event);
        Assert.IsFalse(tsunami.IsCancelled);
        Assert.HasCount(1, tsunami.Areas);
        Assert.AreEqual("岩手県", tsunami.Areas[0].Name);
        Assert.AreEqual(TsunamiGrade.Watch, tsunami.Areas[0].Grade);
        Assert.IsTrue(tsunami.WarningStateChanged);
    }

    [TestMethod]
    public void Vtse41DetectsUpgradeFromWarningToMajorWarning()
    {
        const string xml = """
            <Report>
              <Control><Title>津波警報・注意報・予報a</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head>
                <ReportDateTime>2024-01-01T16:22:00+09:00</ReportDateTime>
                <EventID>20240101161010</EventID><InfoType>発表</InfoType>
                <Headline><Text>大津波警報・津波警報に切り替えました。</Text></Headline>
              </Head>
              <Body><Tsunami><Forecast><Item>
                <Area><Name>石川県能登</Name><Code>181</Code></Area>
                <Category>
                  <Kind><Name>大津波警報：発表</Name><Code>53</Code></Kind>
                  <LastKind><Name>津波警報</Name><Code>51</Code></LastKind>
                </Category>
              </Item></Forecast></Tsunami></Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            NiiJmaXmlHistoryMessageSource.ProviderName,
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2024-01-01T07:22:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        TsunamiEvent tsunami = Assert.IsInstanceOfType<TsunamiEvent>(result.Event);
        Assert.IsTrue(tsunami.WarningStateChanged);
        DisplayProgram program = new PageComposer().Compose(
            tsunami,
            AppSettings.CreateDefault().Display);
        Assert.AreEqual("津波情報が変更されました", program.Pages[0].Blocks[0].PrimaryText);
    }

    [TestMethod]
    public void Vtse51SeparatesForecastStationPredictionAndCoastalObservation()
    {
        const string xml = """
            <Report uuid="20260811010000_0_VTSE51_010000"
                    xmlns:jmx_eb="http://xml.kishou.go.jp/jmaxml1/elementBasis1/">
              <Control><Title>津波情報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head><ReportDateTime>2026-08-11T01:10:00+09:00</ReportDateTime><EventID>20260811010000</EventID><InfoType>発表</InfoType><Headline><Text>１１日０１時０９分現在の、津波の観測値をお知らせします。</Text></Headline></Head>
              <Body><Tsunami>
                <Forecast><Item>
                  <Area><Name>静岡県</Name><Code>380</Code></Area>
                  <Category><Name>津波警報</Name></Category>
                  <FirstHeight><ArrivalTime>2026-08-11T01:30:00+09:00</ArrivalTime></FirstHeight>
                  <MaxHeight><jmx_eb:TsunamiHeight unit="m" description="３ｍ">3</jmx_eb:TsunamiHeight></MaxHeight>
                  <Station><Name>御前崎</Name><Code>38001</Code>
                    <HighTideDateTime>2026-08-11T02:15:00+09:00</HighTideDateTime>
                    <FirstHeight><ArrivalTime>2026-08-11T01:35:00+09:00</ArrivalTime></FirstHeight>
                  </Station>
                </Item></Forecast>
                <Observation><Item>
                  <Area><Name>静岡県</Name><Code>380</Code></Area>
                  <Station><Name>御前崎</Name><Code>38001</Code>
                    <FirstHeight><ArrivalTime>2026-08-11T01:32:00+09:00</ArrivalTime><Initial>押し</Initial></FirstHeight>
                    <MaxHeight>
                      <DateTime>2026-08-11T01:38:00+09:00</DateTime>
                      <jmx_eb:TsunamiHeight unit="m" condition="上昇中" description="１．２ｍ">1.2</jmx_eb:TsunamiHeight>
                    </MaxHeight>
                  </Station>
                </Item></Observation>
              </Tsunami></Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            NiiJmaXmlHistoryMessageSource.ProviderName,
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2026-08-10T16:10:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.IsTrue(result.IsSuccess);
        TsunamiEvent tsunami = Assert.IsInstanceOfType<TsunamiEvent>(result.Event);
        Assert.HasCount(3, tsunami.Areas);
        TsunamiArea forecast = tsunami.Areas.Single(static area =>
            area.Role == TsunamiInformationRole.ForecastArea);
        TsunamiArea station = tsunami.Areas.Single(static area =>
            area.Role == TsunamiInformationRole.StationForecast);
        TsunamiArea observation = tsunami.Areas.Single(static area =>
            area.Role == TsunamiInformationRole.CoastalObservation);
        Assert.AreEqual("静岡県", forecast.Name);
        Assert.AreEqual("御前崎", station.Name);
        Assert.AreEqual("静岡県", station.ParentAreaName);
        Assert.AreEqual(
            DateTimeOffset.Parse("2026-08-11T02:15:00+09:00", CultureInfo.InvariantCulture),
            station.HighTideAt);
        Assert.AreEqual("御前崎", observation.Name);
        Assert.AreEqual("押し", observation.FirstHeight?.Condition);
        Assert.AreEqual("１．２ｍ", observation.MaximumHeight?.Description);
        Assert.AreEqual(1.2, observation.MaximumHeight?.ValueMeters);
        Assert.AreEqual("上昇中", observation.MaximumHeight?.Condition);
        DisplayProgram display = new PageComposer().Compose(
            tsunami,
            AppSettings.CreateDefault().Display);
        Assert.IsTrue(display.Pages
            .SelectMany(static page => page.Blocks)
            .Any(static block => block.SecondaryText == "〔01時38分 押し １．２ｍ 上昇中〕"));
        Assert.AreEqual(
            DateTimeOffset.Parse("2026-08-11T01:09:00+09:00", CultureInfo.InvariantCulture),
            tsunami.ObservationAsOf);
    }

    [TestMethod]
    public void Vtse52NormalizesOffshoreObservationStations()
    {
        const string xml = """
            <Report uuid="20260811011000_0_VTSE52_010000"
                    xmlns:jmx_eb="http://xml.kishou.go.jp/jmaxml1/elementBasis1/">
              <Control>
                <Title>沖合の津波観測に関する情報</Title>
                <Status>通常</Status>
                <PublishingOffice>気象庁</PublishingOffice>
              </Control>
              <Head>
                <ReportDateTime>2026-08-11T01:10:00+09:00</ReportDateTime>
                <EventID>20260811010000</EventID>
                <InfoType>発表</InfoType>
              </Head>
              <Body>
                <Tsunami>
                  <Observation>
                    <Item>
                      <Area><Name></Name><Code></Code></Area>
                      <Station>
                        <Name>静岡御前崎沖</Name><Code>38090</Code><Sensor>ＧＰＳ波浪計</Sensor>
                        <FirstHeight><ArrivalTime>2026-08-11T01:02:00+09:00</ArrivalTime><Initial>押し</Initial></FirstHeight>
                        <MaxHeight>
                          <DateTime>2026-08-11T01:08:00+09:00</DateTime>
                          <Condition>重要</Condition>
                          <jmx_eb:TsunamiHeight type="これまでの最大波の高さ" unit="m" description="１．８ｍ">1.8</jmx_eb:TsunamiHeight>
                        </MaxHeight>
                      </Station>
                      <Station>
                        <Name>三重尾鷲沖</Name><Code>40090</Code><Sensor>ＧＰＳ波浪計</Sensor>
                        <FirstHeight><ArrivalTime>2026-08-11T01:03:00+09:00</ArrivalTime><Initial>引き</Initial></FirstHeight>
                        <MaxHeight><Condition>観測中</Condition></MaxHeight>
                      </Station>
                    </Item>
                  </Observation>
                </Tsunami>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            NiiJmaXmlHistoryMessageSource.ProviderName,
            xml,
            SourceMode.HistoryRehearsal,
            DateTimeOffset.Parse("2026-08-10T16:10:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.IsTrue(result.IsSuccess);
        TsunamiEvent tsunami = Assert.IsInstanceOfType<TsunamiEvent>(result.Event);
        Assert.IsFalse(tsunami.IsCancelled);
        Assert.HasCount(2, tsunami.Areas);
        Assert.AreEqual("静岡御前崎沖", tsunami.Areas[0].Name);
        Assert.AreEqual(TsunamiInformationRole.OffshoreObservation, tsunami.Areas[0].Role);
        Assert.AreEqual(TsunamiGrade.Unknown, tsunami.Areas[0].Grade);
        Assert.AreEqual("押し", tsunami.Areas[0].FirstHeight?.Condition);
        Assert.AreEqual("１．８ｍ", tsunami.Areas[0].MaximumHeight?.Description);
        Assert.AreEqual(1.8, tsunami.Areas[0].MaximumHeight?.ValueMeters);
        Assert.AreEqual(
            DateTimeOffset.Parse("2026-08-11T01:08:00+09:00", CultureInfo.InvariantCulture),
            tsunami.Areas[0].MaximumHeight?.ObservedAt);
        Assert.AreEqual("三重尾鷲沖", tsunami.Areas[1].Name);
        Assert.AreEqual("引き", tsunami.Areas[1].FirstHeight?.Condition);
        Assert.AreEqual("観測中", tsunami.Areas[1].MaximumHeight?.Description);
        Assert.IsNull(tsunami.Areas[1].MaximumHeight?.ValueMeters);
    }

    private static WeatherWarningEvent NormalizeAxisWeather(string xml)
    {
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());
        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "axis",
            xml,
            SourceMode.Production,
            DateTimeOffset.Parse("2026-08-10T03:02:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });
        return Assert.IsInstanceOfType<WeatherWarningEvent>(result.Event);
    }

    private static VolcanoEvent NormalizeVolcano(string xml)
    {
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());
        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "dmdata.jp",
            xml,
            SourceMode.Production,
            DateTimeOffset.Parse("2026-08-13T08:00:01Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });
        Assert.IsTrue(
            result.IsSuccess,
            string.Join("; ", result.Issues.Select(static issue => issue.Message)));
        return Assert.IsInstanceOfType<VolcanoEvent>(result.Event);
    }
}
