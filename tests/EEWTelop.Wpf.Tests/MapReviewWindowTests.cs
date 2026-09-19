using System.Runtime.ExceptionServices;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class MapReviewWindowTests
{
    [TestMethod]
    public void IndependentWindowKeepsSnapshotUntilExplicitSelection()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            MapReviewWindow? window = null;
            try
            {
                var items = new List<ReceivedTelegramViewModel>();
                window = new MapReviewWindow(() => items);
                Assert.IsNull(window.Owner);
                Assert.IsNull(window.SelectedQuake);
                var quake = (QuakeEvent)TestScenarioCatalog.Create(DateTimeOffset.UtcNow).Single(s => s.Id == "detail-scale").Event;
                items.Add(new ReceivedTelegramViewModel(quake, new PageComposer().Compose(quake, AppSettings.CreateDefault().Display)));
                Assert.IsNull(window.SelectedQuake);
                window.Reload();
                Assert.AreSame(quake, window.SelectedQuake);
                string? output = Environment.GetEnvironmentVariable("EEWTELOP_RENDER_OUTPUT");
                if (output is not null)
                {
                    var content = (System.Windows.FrameworkElement)window.Content;
                    content.Measure(new System.Windows.Size(1400, 850));
                    content.Arrange(new System.Windows.Rect(0, 0, 1400, 850));
                    content.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1400, 850, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(output);
                    using var file = File.Create(Path.Combine(output, "map-review-list.png"));
                    encoder.Save(file);
                }
                items.Clear();
                window.Reload(quake);
                Assert.AreSame(quake, window.SelectedQuake);
                window.IncludeTestTelegrams();
                Assert.IsNotNull(window.SelectedQuake);
                Assert.AreEqual(SourceMode.ManualTest, window.SelectedQuake.SourceMode);
                Assert.IsTrue(MapReviewWindow.CreateTestTelegrams().All(t => t.Event is QuakeEvent && t.SourceText == "試験電文・訓練"));
                window.ShowExternalXml(quake);
                Assert.AreSame(quake, window.SelectedQuake);
                window.Reload();
                Assert.AreSame(quake, window.SelectedQuake);
                Assert.IsEmpty(items, "External map review must not add reception history.");
            }
            catch (Exception ex) { error = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
