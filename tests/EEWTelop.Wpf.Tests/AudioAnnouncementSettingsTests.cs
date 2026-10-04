using EEWTelop.Application.Configuration;
using EEWTelop.Application.Logging;
using EEWTelop.Infrastructure.Settings;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class AudioAnnouncementSettingsTests
{
    [TestMethod]
    public void DefaultsAndAudioResetKeepLegacyTiming()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        var editor = new SettingsEditorViewModel(defaults);
        Assert.AreEqual(AudioAnnouncementMode.Legacy, editor.QuakeAnnouncementMode);
        Assert.AreEqual(AudioAnnouncementMode.Legacy, editor.TsunamiAnnouncementMode);
        Assert.AreEqual(AudioAnnouncementMode.Legacy, editor.WeatherAnnouncementMode);
        editor.QuakeAnnouncementMode = AudioAnnouncementMode.FirstAnnouncementOnly;
        editor.TsunamiAnnouncementMode = AudioAnnouncementMode.NewAnnouncementOrEscalation;
        editor.WeatherAnnouncementMode = AudioAnnouncementMode.FirstAnnouncementOnly;
        AppSettings selected = editor.ToSettings(defaults);
        var restored = new SettingsEditorViewModel(selected);
        Assert.AreEqual(editor.QuakeAnnouncementMode, restored.QuakeAnnouncementMode);
        Assert.AreEqual(editor.TsunamiAnnouncementMode, restored.TsunamiAnnouncementMode);
        Assert.AreEqual(editor.WeatherAnnouncementMode, restored.WeatherAnnouncementMode);
        restored.ResetAudioSettings();
        AudioSettings reset = restored.ToSettings(defaults).Audio;
        Assert.AreEqual(AudioAnnouncementMode.Legacy, reset.QuakeAnnouncementMode);
        Assert.AreEqual(AudioAnnouncementMode.Legacy, reset.TsunamiAnnouncementMode);
        Assert.AreEqual(AudioAnnouncementMode.Legacy, reset.WeatherAnnouncementMode);
    }

    [TestMethod]
    public void InvalidTimingValuesNormalizeWithoutDiscardingOtherSettings()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings invalid = defaults with
        {
            Audio = defaults.Audio with
            {
                QuakeAnnouncementMode = (AudioAnnouncementMode)999,
                TsunamiAnnouncementMode = (AudioAnnouncementMode)999,
                WeatherAnnouncementMode = (AudioAnnouncementMode)999,
                WeatherCoalescingSeconds = 2,
            },
        };
        AudioSettings normalized = JsonSettingsStore.NormalizeDocument(invalid).Audio;
        Assert.AreEqual(AudioAnnouncementMode.Legacy, normalized.QuakeAnnouncementMode);
        Assert.AreEqual(AudioAnnouncementMode.Legacy, normalized.TsunamiAnnouncementMode);
        Assert.AreEqual(AudioAnnouncementMode.Legacy, normalized.WeatherAnnouncementMode);
        Assert.AreEqual(2, normalized.WeatherCoalescingSeconds);
    }

    [TestMethod]
    [DataRow(AudioAnnouncementMode.Legacy)]
    [DataRow(AudioAnnouncementMode.FirstAnnouncementOnly)]
    [DataRow(AudioAnnouncementMode.NewAnnouncementOrEscalation)]
    public async Task TimingChoicesSurviveJsonSaveAndReload(AudioAnnouncementMode mode)
    {
        string directory = Path.Combine(Path.GetTempPath(), "cdi-audio-timing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            AppSettings defaults = AppSettings.CreateDefault();
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"), new UiLogBuffer());
            await store.SaveAsync(defaults with
            {
                Audio = defaults.Audio with
                {
                    QuakeAnnouncementMode = mode, TsunamiAnnouncementMode = mode, WeatherAnnouncementMode = mode,
                },
            });
            AppSettings reloaded = await store.LoadAsync();
            Assert.AreEqual(mode, reloaded.Audio.QuakeAnnouncementMode);
            Assert.AreEqual(mode, reloaded.Audio.TsunamiAnnouncementMode);
            Assert.AreEqual(mode, reloaded.Audio.WeatherAnnouncementMode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
