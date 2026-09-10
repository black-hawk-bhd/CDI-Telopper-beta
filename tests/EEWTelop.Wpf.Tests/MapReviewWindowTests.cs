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
                items.Clear();
                window.Reload(quake);
                Assert.AreSame(quake, window.SelectedQuake);
            }
            catch (Exception ex) { error = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
