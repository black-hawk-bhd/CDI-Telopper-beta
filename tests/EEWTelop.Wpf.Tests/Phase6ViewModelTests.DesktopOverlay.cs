using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.Services;
using EEWTelop.Wpf.ViewModels;
using EEWTelop.Wpf.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

public sealed partial class Phase6ViewModelTests
{
    [STATestMethod]
    public void DesktopOverlayFollowsSavedSettingsAndTheActiveCaption()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        Assert.IsFalse(defaults.DesktopOverlay.Enabled);
        AppSettings settings = defaults with
        {
            DesktopOverlay = new DesktopOverlaySettings(Enabled: true),
            Obs = defaults.Obs with { Enabled = false },
        };
        InspectControlWindow(settings, control =>
        {
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            DesktopOverlayWindow? overlay = control.OverlayWindow;
            Assert.IsNotNull(overlay);
            Assert.IsNull(overlay.Owner);
            Assert.IsTrue(overlay.Topmost);
            Assert.IsFalse(overlay.ShowInTaskbar);
            Assert.IsFalse(overlay.ShowActivated);
            Assert.AreSame(control.ViewModel.Overlay, overlay.DataContext);
            Assert.AreEqual(SystemParameters.WorkArea.Top, overlay.Top);

            const int extendedStyleIndex = -20;
            const int topmostStyle = 0x00000008;
            const int clickThroughStyle = 0x00000020;
            const int layeredStyle = 0x00080000;
            const int noActivateStyle = 0x08000000;
            const int overlayStyles = clickThroughStyle | layeredStyle | noActivateStyle;
            IntPtr handle = new WindowInteropHelper(overlay).Handle;
            int styles = ReadWindowStyle(handle, extendedStyleIndex);
            Assert.AreEqual(overlayStyles | topmostStyle, styles & (overlayStyles | topmostStyle));

            control.ViewModel.Settings.DesktopOverlayTopmost = false;
            CompleteUiTask(control.Dispatcher, control.ViewModel.SaveSettingsAsync());
            Assert.AreSame(overlay, control.OverlayWindow);
            styles = ReadWindowStyle(handle, extendedStyleIndex);
            Assert.AreEqual(overlayStyles, styles & (overlayStyles | topmostStyle));

            var caption = (TelopView)overlay.Content;
            Assert.AreEqual(Visibility.Collapsed, caption.Visibility);
            control.ViewModel.SelectedScenario = control.ViewModel.Scenarios.Single(scenario => scenario.Id == "detail-scale");
            control.ViewModel.RunTestCommand.Execute(null);
            overlay.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
            Assert.AreEqual(Visibility.Visible, caption.Visibility);
            Assert.AreEqual("操作テスト／訓練", control.ViewModel.Overlay.RehearsalLabel);

            control.ViewModel.ClearQuakeDisplayCommand.Execute(null);
            overlay.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
            Assert.AreEqual(Visibility.Collapsed, caption.Visibility);

            control.ViewModel.Settings.DesktopOverlayEnabled = false;
            CompleteUiTask(control.Dispatcher, control.ViewModel.SaveSettingsAsync());
            Assert.IsNull(control.OverlayWindow);
        });
    }

    [STATestMethod]
    [DataRow("scale-prompt")]
    [DataRow("tsunami-13")]
    [DataRow("eew-warning")]
    public void DesktopOverlayDrawsTestCaptionsAfterStartupAndClear(string scenarioId)
    {
        AppSettings settings = AppSettings.CreateDefault() with
        {
            DesktopOverlay = new DesktopOverlaySettings(Enabled: true),
        };
        InspectControlWindow(settings, control =>
        {
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            DesktopOverlayWindow? overlay = control.OverlayWindow;
            Assert.IsNotNull(overlay);
            overlay.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

            control.ViewModel.SelectedScenario = control.ViewModel.Scenarios.Single(scenario => scenario.Id == scenarioId);
            control.ViewModel.RunTestCommand.Execute(null);
            AssertCaptionIsDrawn(overlay);

            control.ViewModel.ClearDisplayCommand.Execute(null);
            overlay.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(Visibility.Collapsed, ((TelopView)overlay.Content).Visibility);

            control.ViewModel.RunTestCommand.Execute(null);
            Assert.IsTrue(control.ViewModel.Overlay.HasProgram);
            AssertCaptionIsDrawn(overlay);
        });
    }

    [STATestMethod]
    public void DesktopOverlayClearButtonHidesTheCaptionAndReenablesForTheNextDisplay()
    {
        AppSettings settings = AppSettings.CreateDefault() with
        {
            DesktopOverlay = new DesktopOverlaySettings(Enabled: true),
        };
        InspectControlWindow(settings, control =>
        {
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            DesktopOverlayWindow? overlay = control.OverlayWindow;
            Assert.IsNotNull(overlay);
            var clearButton = Assert.IsInstanceOfType<Button>(control.FindName("ClearDesktopOverlayButton"));
            Assert.IsFalse(clearButton.IsEnabled);

            control.ViewModel.SelectedScenario = control.ViewModel.Scenarios.Single(scenario => scenario.Id == "eew-warning");
            control.ViewModel.RunTestCommand.Execute(null);
            AssertCaptionIsDrawn(overlay);
            Assert.IsTrue(clearButton.IsEnabled);

            clearButton.Command.Execute(clearButton.CommandParameter);
            overlay.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
            Assert.AreEqual(Visibility.Collapsed, ((TelopView)overlay.Content).Visibility);
            Assert.IsFalse(clearButton.IsEnabled);

            control.ViewModel.RunTestCommand.Execute(null);
            AssertCaptionIsDrawn(overlay);
            Assert.IsTrue(clearButton.IsEnabled);
        });
    }

    [TestMethod]
    public async Task DesktopOverlayClearPersistsAcrossPagesWhileObsAndReceptionContinue()
    {
        AppSettings settings = AppSettings.CreateDefault() with
        {
            DesktopOverlay = new DesktopOverlaySettings(Enabled: true),
        };
        var clock = new FakeClock();
        var services = CreateServices(ProviderConnectionState.Connected, clock, suppliedSettings: settings);
        DisplayProgram program = CreateSubtitleEditProgram("desktop-overlay-clear", EventKind.Quake,
            OverlayPriority.Quake, EndPolicy.LoopUntilReplaced, "1ページ目", clock.UtcNow);
        program = program with { Pages = [program.Pages[0], program.Pages[0] with { Index = 1 }] };
        services.DisplayCoordinator.Apply(program);
        var obs = new ObsSnapshotStore(settings.Display, clock.UtcNow);
        await using var viewModel = new ControlWindowViewModel(services, settings,
            new FakeConfirmationService(), new ImmediateUiDispatcher(), obs);
        Assert.IsTrue(viewModel.Overlay.HasProgram);
        Assert.AreEqual(program.ProgramId, obs.Read().ProgramId);

        viewModel.ClearDesktopOverlayCommand.Execute(null);
        Assert.IsFalse(viewModel.Overlay.HasProgram);
        Assert.AreEqual(program.ProgramId, obs.Read().ProgramId);

        clock.Advance(PageClock.GetPageDuration(settings.Display, program));
        await WaitUntilAsync(() => obs.Read().PageIndex == 1);
        Assert.AreEqual(1, obs.Read().PageIndex);
        Assert.AreEqual(program.ProgramId, obs.Read(ObsViewChannel.General, clock.UtcNow).ProgramId);
        Assert.IsFalse(viewModel.Overlay.HasProgram);
        Assert.AreEqual(ProviderConnectionState.Connected, viewModel.ConnectionState);

        DisplayProgram updated = program with { ProgramId = "desktop-overlay-clear-update", IssuedAt = clock.UtcNow };
        services.DisplayCoordinator.Apply(updated);
        await WaitUntilAsync(() => obs.Read().ProgramId == updated.ProgramId);
        Assert.AreEqual(updated.ProgramId, viewModel.Overlay.ProgramId);
        Assert.IsTrue(viewModel.Overlay.HasProgram);
    }

    private static void AssertCaptionIsDrawn(DesktopOverlayWindow overlay)
    {
        overlay.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
        overlay.UpdateLayout();

        var caption = (TelopView)overlay.Content;
        Assert.IsTrue(caption.IsVisible && caption.ActualWidth > 0 && caption.ActualHeight > 0);
        int width = (int)Math.Ceiling(caption.ActualWidth);
        int height = (int)Math.Ceiling(caption.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(caption);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        Assert.IsTrue(ContainsVisibleText(pixels), "テスト字幕の文字がデスクトップ表示に描画されていません。");
    }

    private static bool ContainsVisibleText(byte[] pixels)
    {
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index] > 240 && pixels[index + 1] > 240 && pixels[index + 2] > 240 && pixels[index + 3] > 240)
                return true;
        }

        return false;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ReadWindowStyle(IntPtr handle, int index);
}
