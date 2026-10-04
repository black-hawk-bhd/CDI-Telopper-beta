using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class SettingsTabLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] SplitTabHeaders = ["受信", "フィルター", "表示", "出力"];

    [TestMethod]
    public void ReceptionFiltersDisplayAndOutputHaveSeparateScrollableTabs()
    {
        XElement[] tabs = LoadControlWindow().Descendants(Presentation + "TabItem").ToArray();
        CollectionAssert.AreEqual(SplitTabHeaders, tabs.Take(4).Select(tab => (string)tab.Attribute("Header")!).ToArray());
        Assert.IsFalse(tabs.Any(tab => (string?)tab.Attribute("Header") is "受信・フィルター" or "表示・出力"));
        foreach (string header in SplitTabHeaders)
        {
            XElement tab = tabs.Single(item => (string?)item.Attribute("Header") == header);
            XElement scroll = tab.Elements(Presentation + "ScrollViewer").Single();
            Assert.AreEqual("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
            Assert.AreEqual(1, scroll.Elements().Count());
        }

        Dictionary<string, string[]> groups = new()
        {
            ["受信"] = ["受信元", "DMDATA.JP 契約情報", "互換・安全"],
            ["フィルター"] = ["表示フィルター"],
            ["表示"] = ["ページと文字", "本番情報の繰り返し表示"],
            ["出力"] = ["キャンバス", "OBS Local View", "CDI External API v1", "訓練・リハーサル専用API", "OBS WebSocket 自動登録"],
        };
        foreach ((string header, string[] expectedGroups) in groups)
        {
            XElement tab = tabs.Single(item => (string?)item.Attribute("Header") == header);
            CollectionAssert.AreEqual(expectedGroups, tab.Descendants(Presentation + "GroupBox")
                .Select(group => (string)group.Attribute("Header")!).ToArray());
        }
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
            ("受信", "IsChecked", "{Binding Settings.ConfirmTestInProduction}"),
            ("受信", "Command", "{Binding DmdataContracts.RefreshCommand}"),
            ("受信", "Command", "{Binding ResetReceptionSettingsCommand}"),
            ("受信", "Command", "{Binding ResetCompatibilityAndSafetySettingsCommand}"),
            ("フィルター", "IsChecked", "{Binding Settings.FilterEew}"),
            ("フィルター", "IsChecked", "{Binding Settings.FilterQuake}"),
            ("フィルター", "IsChecked", "{Binding Settings.FilterWeatherWarnings}"),
            ("フィルター", "IsChecked", "{Binding Settings.HideWeatherContinuationOnly}"),
            ("フィルター", "Command", "{Binding ResetFilterSettingsCommand}"),
            ("表示", "Text", "{Binding Settings.PageDurationSeconds}"),
            ("表示", "IsChecked", "{Binding Settings.SeparateIntensityPagesByScale}"),
            ("表示", "IsChecked", "{Binding Settings.LimitActiveWeatherAreaRowsToTwo}"),
            ("表示", "IsChecked", "{Binding Settings.ProductionReplayQuakeEnabled}"),
            ("表示", "Command", "{Binding ResetDisplaySettingsCommand}"),
            ("表示", "Command", "{Binding ResetProductionReplaySettingsCommand}"),
            ("出力", "SelectedItem", "{Binding Settings.BackgroundMode}"),
            ("出力", "Text", "{Binding Settings.Width}"),
            ("出力", "Text", "{Binding Settings.Height}"),
            ("出力", "IsChecked", "{Binding Settings.ObsEnabled}"),
            ("出力", "IsChecked", "{Binding ExternalApiEnabled}"),
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
                     "{Binding Settings.QuakePrefectureMode}", "{Binding Settings.ShowLowIntensityPointsInStrongQuakes}" })
        {
            XElement control = document.Descendants().Single(element => element.Attributes().Any(attribute => attribute.Value == binding));
            Assert.AreEqual("フィルター", ParentTab(control));
        }
    }

    private static XDocument LoadControlWindow()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "src", "EEWTelop.Wpf", "ControlWindow.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
        }
        throw new FileNotFoundException("ControlWindow.xaml was not found.");
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
