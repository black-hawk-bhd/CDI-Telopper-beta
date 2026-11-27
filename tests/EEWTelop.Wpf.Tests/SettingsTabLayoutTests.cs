using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class SettingsTabLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] SplitTabHeaders = ["受信", "フィルター", "表示", "出力"];
    private static readonly string[] TelegramReplayModes = ["本番情報の再掲", "過去情報として紹介"];
    private static readonly string[] TelegramReplayOptions =
        ["情報種別をバッジではなく字幕で表示", "再掲の最終ページに「これは○月○日○時○分発表の情報です」を追加"];

    [TestMethod]
    public void ReceptionFiltersDisplayAndOutputHaveSeparateScrollableTabs()
    {
        XElement[] tabs = LoadControlWindow().Descendants(Presentation + "TabItem").ToArray();
        CollectionAssert.AreEqual(SplitTabHeaders, tabs.Take(4).Select(tab => (string)tab.Attribute("Header")!).ToArray());
        Assert.IsFalse(tabs.Any(tab => (string?)tab.Attribute("Header") is "受信・フィルター" or "表示・出力"));
        Assert.IsFalse(tabs.SelectMany(tab => tab.Descendants(Presentation + "CheckBox"))
            .Any(control => (string?)control.Attribute("Content") == "ページ番号を表示"));
        foreach (string header in SplitTabHeaders)
        {
            XElement tab = tabs.Single(item => (string?)item.Attribute("Header") == header);
            XElement scroll = tab.Elements(Presentation + "ScrollViewer").Single();
            Assert.AreEqual("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
            Assert.AreEqual(1, scroll.Elements().Count());
        }

        Dictionary<string, string[]> groups = new()
        {
            ["受信"] = ["受信元", "DMDATA.JP 契約情報", "互換設定"],
            ["フィルター"] = ["表示フィルター"],
            ["表示"] = ["ページと文字", "本番情報の繰り返し表示"],
            ["出力"] = ["デスクトップ表示", "キャンバス", "字幕と一緒に送出する画像", "OBS Local View", "CDI External API v1", "訓練・リハーサル専用API", "OBS WebSocket 自動登録"],
        };
        foreach ((string header, string[] expectedGroups) in groups)
        {
            XElement tab = tabs.Single(item => (string?)item.Attribute("Header") == header);
            CollectionAssert.AreEqual(expectedGroups, tab.Descendants(Presentation + "GroupBox")
                .Select(group => (string)group.Attribute("Header")!).ToArray());
        }
    }

    [TestMethod]
    public void P2pReceptionHasNoConnectionPresetPicker()
    {
        XElement panel = LoadControlWindow().Descendants(Presentation + "TextBlock")
            .Single(item => (string?)item.Attribute("Text") == "P2P地震情報 API").Parent!;
        Assert.AreEqual("受信", ParentTab(panel));
        Assert.IsFalse(panel.Elements(Presentation + "ComboBox").Any());
    }

    [TestMethod]
    public void WolfxReceptionSettingsDescribeCurrentCategoriesAndEarthquakeDetails()
    {
        XElement panel = LoadControlWindow().Descendants(Presentation + "TextBlock")
            .Single(item => (string?)item.Attribute("Text") == "Wolfx（公開API）").Parent!;
        Assert.AreEqual("受信", ParentTab(panel));
        string help = string.Join("\n", panel.Descendants(Presentation + "TextBlock")
            .Attributes("Text").Select(attribute => attribute.Value));
        StringAssert.Contains(help, "緊急地震速報（警報・取消）、地震情報、津波情報、南海トラフ地震の情報、北海道・三陸沖後発地震注意情報");
        StringAssert.Contains(help, "地震詳細APIから震度速報・震源情報・各地の震度・長周期地震動階級");
        foreach (string product in new[] { "VXSE51", "VXSE52", "VXSE53", "VXSE61", "VXSE62" })
        {
            StringAssert.Contains(help, product);
        }
        Assert.IsFalse(help.Contains("VXSE53相当", StringComparison.Ordinal));
        Assert.DoesNotContain("EEW", help);
    }

    [TestMethod]
    public void WeatherTwoRowSettingDescribesAllStatusesAndRetainsItsBinding()
    {
        XElement control = LoadControlWindow().Descendants(Presentation + "CheckBox")
            .Single(item => (string?)item.Attribute("IsChecked") == "{Binding Settings.LimitActiveWeatherAreaRowsToTwo}");
        Assert.AreEqual("表示", ParentTab(control));
        string tooltip = (string)control.Attribute("ToolTip")!;
        StringAssert.Contains(tooltip, "新規発表・更新・継続・解除");
        StringAssert.Contains(tooltip, "OFFでは従来の3行表示");
        StringAssert.Contains(tooltip, "説明文のページは変更しません");
    }

    [TestMethod]
    public void MovedControlsKeepTheirSettingsCommandsAndPasswordHandlers()
    {
        XDocument document = LoadControlWindow();
        (string Tab, string Attribute, string Value)[] bindings =
        [
            ("受信", "SelectedValue", "{Binding Settings.EewProvider}"),
            ("受信", "SelectedValue", "{Binding Settings.DmdataAuthenticationMode}"),
            ("受信", "IsChecked", "{Binding Settings.JmaXmlAutoFallback}"),
            ("受信", "Command", "{Binding DmdataContracts.RefreshCommand}"),
            ("受信", "Command", "{Binding ResetReceptionSettingsCommand}"),
            ("受信", "Command", "{Binding ResetCompatibilityAndSafetySettingsCommand}"),
            ("フィルター", "IsChecked", "{Binding Settings.FilterEew}"),
            ("フィルター", "IsChecked", "{Binding Settings.FilterQuake}"),
            ("フィルター", "IsChecked", "{Binding Settings.FilterWeatherWarnings}"),
            ("フィルター", "IsChecked", "{Binding Settings.FilterWeatherDangerWarnings}"),
            ("フィルター", "IsChecked", "{Binding Settings.HideWeatherContinuationOnly}"),
            ("フィルター", "IsChecked", "{Binding Settings.ShowRoutineNankaiCommentary}"),
            ("フィルター", "IsChecked", "{Binding Settings.ShowNankaiTroughInformation}"),
            ("フィルター", "IsChecked", "{Binding Settings.ShowSubsequentEarthquakeAdvisory}"),
            ("フィルター", "Command", "{Binding ResetFilterSettingsCommand}"),
            ("表示", "Text", "{Binding Settings.PageDurationSeconds}"),
            ("表示", "ItemsSource", "{Binding Settings.TelegramPageDurations}"),
            ("表示", "IsChecked", "{Binding Settings.SeparateIntensityPagesByScale}"),
            ("表示", "IsChecked", "{Binding Settings.LimitActiveWeatherAreaRowsToTwo}"),
            ("表示", "IsChecked", "{Binding Settings.ShowTsunamiDowngradeGradeBadge}"),
            ("表示", "IsChecked", "{Binding Settings.ProductionReplayQuakeEnabled}"),
            ("表示", "Command", "{Binding ResetDisplaySettingsCommand}"),
            ("表示", "Command", "{Binding ResetProductionReplaySettingsCommand}"),
            ("出力", "IsChecked", "{Binding Settings.DesktopOverlayEnabled}"),
            ("出力", "IsChecked", "{Binding Settings.DesktopOverlayTopmost}"),
            ("出力", "SelectedItem", "{Binding Settings.BackgroundMode}"),
            ("出力", "Text", "{Binding Settings.Width}"),
            ("出力", "Text", "{Binding Settings.Height}"),
            ("出力", "ItemsSource", "{Binding Settings.SubtitleImages}"),
            ("出力", "Click", "OnSelectSubtitleImage"),
            ("出力", "Click", "OnRemoveSubtitleImage"),
            ("出力", "IsChecked", "{Binding Settings.ObsEnabled}"),
            ("出力", "IsChecked", "{Binding ExternalApiEnabled}"),
            ("出力", "IsChecked", "{Binding Settings.ExternalApiAutoEnableOnStartup}"),
            ("出力", "Command", "{Binding ToggleExternalApiConnectionKeySavingCommand}"),
            ("出力", "Command", "{Binding ReissueExternalApiConnectionKeyCommand}"),
            ("出力", "IsChecked", "{Binding RehearsalApiEnabled}"),
            ("出力", "Command", "{Binding ResetCanvasSettingsCommand}"),
            ("出力", "Command", "{Binding ResetObsLocalViewSettingsCommand}"),
            ("出力", "Command", "{Binding ResetObsWebSocketSettingsCommand}"),
        ];
        foreach ((string tab, string attribute, string value) in bindings)
        {
            XElement control = document.Descendants().Single(item => (string?)item.Attribute(attribute) == value);
            Assert.AreEqual(tab, ParentTab(control), value);
        }
        (string Name, string Tab, string Handler)[] passwords =
        [
            ("DmdataCredentialBox", "受信", "OnDmdataCredentialChanged"),
            ("AxisAccessTokenBox", "受信", "OnAxisAccessTokenChanged"),
            ("ObsWebSocketPasswordBox", "出力", "OnObsWebSocketPasswordChanged"),
        ];
        foreach ((string name, string tab, string handler) in passwords)
        {
            XElement control = document.Descendants(Presentation + "PasswordBox")
                .Single(item => (string?)item.Attribute(Xaml + "Name") == name);
            Assert.AreEqual(tab, ParentTab(control));
            Assert.AreEqual(handler, (string?)control.Attribute("PasswordChanged"));
        }
    }

    private static string? ParentTab(XElement element) =>
        (string?)element.Ancestors(Presentation + "TabItem").Single().Attribute("Header");

    [TestMethod]
    public void OnlyBrowserPreviewRemainsAndNewEarthquakeSettingsAreOnFilterTab()
    {
        XDocument document = LoadControlWindow();
        Assert.HasCount(1, document.Descendants(Presentation + "Button")
            .Where(button => (string?)button.Attribute("Click") == "OnOpenBrowserMonitor").ToArray());
        Assert.IsFalse(document.Descendants().Attributes().Any(attribute =>
            attribute.Value.Contains("ShowPreviewCommand", StringComparison.Ordinal) ||
            attribute.Value.Contains("Settings.HideQuakeBelowIntensity3", StringComparison.Ordinal)));
        foreach (string binding in new[] { "{Binding Settings.QuakeIntensityFilters}",
                     "{Binding Settings.QuakePrefectureMode}", "{Binding Settings.ShowLowIntensityPointsInStrongQuakes}",
                     "{Binding Settings.HideNonJmaIntensityStations}", "{Binding Settings.ShowQuakeWithEewNoStrongShakingComment}" })
        {
            XElement control = document.Descendants().Single(element => element.Attributes().Any(attribute => attribute.Value == binding));
            Assert.AreEqual("フィルター", ParentTab(control));
        }
    }

    [TestMethod]
    public void LiveTestConfirmationAndTrainingReplayControlsAreRemoved()
    {
        Assert.IsFalse(LoadControlWindow().Descendants(Presentation + "CheckBox").Any(item =>
            (string?)item.Attribute("Content") == "本番中のテストを確認"));
        string[] replayModes = LoadWindow("TelegramReviewWindow.xaml").Descendants(Presentation + "RadioButton")
            .Where(item => (string?)item.Attribute("GroupName") == "TelegramReplayMode")
            .Select(item => (string)item.Attribute("Content")!).ToArray();
        CollectionAssert.AreEqual(TelegramReplayModes, replayModes);
    }

    [TestMethod]
    public void TelegramReviewShowsOnlyTheCaptionAndIssueTimeOptions()
    {
        var settings = LoadWindow("TelegramReviewWindow.xaml").Descendants(Presentation + "Expander")
            .Single(item => (string?)item.Attribute("Header") == "取得元と再掲の表示設定");
        string[] labels = settings.Descendants(Presentation + "CheckBox")
            .Select(item => (string)item.Attribute("Content")!).ToArray();
        CollectionAssert.AreEqual(TelegramReplayOptions, labels);
    }

    private static XDocument LoadControlWindow() => LoadWindow("ControlWindow.xaml");

    private static XDocument LoadWindow(string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "src", "EEWTelop.Wpf", fileName);
            if (File.Exists(path)) return XDocument.Load(path);
        }
        throw new FileNotFoundException($"{fileName} was not found.");
    }

    [TestMethod]
    public void EarthquakeTsunamiAndWeatherAudioTimingHaveThreeChoicesOnAudioTab()
    {
        XDocument document = LoadControlWindow();
        foreach (string property in new[] { "QuakeAnnouncementMode", "TsunamiAnnouncementMode", "WeatherAnnouncementMode" })
        {
            XElement combo = document.Descendants(Presentation + "ComboBox")
                .Single(element => (string?)element.Attribute("SelectedValue") == "{Binding Settings." + property + "}");
            Assert.AreEqual("音声", ParentTab(combo));
            Assert.HasCount(3, combo.Elements(Presentation + "ComboBoxItem").ToArray());
        }
    }
}
