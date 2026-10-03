using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Wpf.Services;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class DmdataContractsViewModelTests
{
    private static readonly DateTimeOffset RetrievedAt = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void RowDistinguishesUncontractedInvalidAndActivePlans()
    {
        var active = new DmdataContractRowViewModel(Contract("92", true));
        var inactive = new DmdataContractRowViewModel(Contract("92", false));
        var uncontracted = new DmdataContractRowViewModel(Contract(null, false));
        Assert.AreEqual("契約中（有効）", active.Status);
        Assert.AreEqual("契約あり（無効）", inactive.Status);
        Assert.AreEqual("未契約", uncontracted.Status);
        StringAssert.Contains(active.Price, "月最大");
        StringAssert.Contains(active.ConnectionCount, "増加");
        Assert.AreNotEqual(active.StatusColor, inactive.StatusColor);
    }

    [TestMethod]
    public async Task NoAutomaticRequestsAndManualRefreshShowsApiSnapshot()
    {
        var service = new StubService(_ => Task.FromResult(Snapshot()));
        await using var model = Create(service);
        Assert.AreEqual(0, service.Calls);
        Assert.AreEqual(0, model.Items.Count);
        StringAssert.Contains(model.StatusText, "未取得");
        await model.RefreshAsync();
        Assert.AreEqual(1, service.Calls);
        Assert.AreEqual(3, model.Items.Count);
        Assert.IsFalse(model.IsBusy);
        StringAssert.Contains(model.RetrievedAtText, "最終取得");
    }

    [TestMethod]
    public async Task FailureClearsPreviousSnapshotAndDoesNotExposeErrorOrSayUncontracted()
    {
        int calls = 0;
        var service = new StubService(_ => ++calls == 1 ? Task.FromResult(Snapshot())
            : throw new HttpRequestException("secret-token-and-account"));
        await using var model = Create(service);
        await model.RefreshAsync();
        Assert.AreEqual(3, model.Items.Count);
        await model.RefreshAsync();
        Assert.AreEqual(0, model.Items.Count);
        Assert.AreEqual(string.Empty, model.RetrievedAtText);
        Assert.IsFalse(model.StatusText.Contains("secret-token", StringComparison.Ordinal));
        Assert.IsFalse(model.StatusText.Contains("未契約", StringComparison.Ordinal));
        StringAssert.Contains(model.StatusText, "未確認");
    }

    [TestMethod]
    public async Task ConcurrentClicksDoNotCreateMultipleRequests()
    {
        var completion = new TaskCompletionSource<DmdataContractSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubService(_ => completion.Task);
        await using var model = Create(service);
        Task first = model.RefreshAsync();
        Assert.IsTrue(model.IsBusy);
        Assert.IsFalse(model.RefreshCommand.CanExecute(null));
        await model.RefreshAsync();
        Assert.AreEqual(1, service.Calls);
        completion.SetResult(Snapshot());
        await first;
        Assert.IsTrue(model.RefreshCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task OldAccountResponseIsDiscardedAfterSettingsChangeEvenIfCancellationIsIgnored()
    {
        var completion = new TaskCompletionSource<DmdataContractSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var model = Create(new StubService(_ => completion.Task));
        Task pending = model.RefreshAsync();
        model.Invalidate();
        completion.SetResult(Snapshot());
        await pending;
        Assert.AreEqual(0, model.Items.Count);
        Assert.AreEqual(string.Empty, model.RetrievedAtText);
        StringAssert.Contains(model.StatusText, "認証設定が変更");
        Assert.IsFalse(model.IsBusy);
    }

    [TestMethod]
    public async Task DisposingCancelsAnInFlightRequest()
    {
        var service = new StubService(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Snapshot();
        });
        var model = Create(service);
        Task pending = model.RefreshAsync();
        await model.DisposeAsync();
        await pending;
        Assert.IsTrue(pending.IsCompletedSuccessfully);
        Assert.IsFalse(model.CanRefresh);
        Assert.AreEqual(0, model.Items.Count);
    }

    [TestMethod]
    public async Task MissingServiceAndOAuthOperationsDisableRetrieval()
    {
        await using var unavailable = Create(null);
        Assert.IsFalse(unavailable.RefreshCommand.CanExecute(null));
        var service = new StubService(_ => Task.FromResult(Snapshot()));
        await using var model = Create(service);
        model.SetAuthorizationBusy(true);
        Assert.IsFalse(model.RefreshCommand.CanExecute(null));
        await model.RefreshAsync();
        Assert.AreEqual(0, service.Calls);
        model.SetAuthorizationBusy(false);
        Assert.IsTrue(model.RefreshCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task EmptyResponseIsNotExpandedIntoInventedUncontractedPlans()
    {
        await using var model = Create(new StubService(_ => Task.FromResult(new DmdataContractSnapshot([], RetrievedAt))));
        await model.RefreshAsync();
        Assert.AreEqual(0, model.Items.Count);
        StringAssert.Contains(model.StatusText, "APIから返された契約情報はありません");
        Assert.IsFalse(model.StatusText.Contains("未契約", StringComparison.Ordinal));
    }

    private static DmdataContractsViewModel Create(IDmdataContractService? service) =>
        new(service, () => AppSettings.CreateDefault().Provider, new ImmediateUiDispatcher());

    private static DmdataContractInfo Contract(string? id, bool valid) =>
        new(id, 1, "地震・津波関連", "telegram.earthquake", 15, 350, id is null ? null : RetrievedAt, valid, 1);

    private static DmdataContractSnapshot Snapshot() =>
        new([Contract("92", true), Contract("93", false), Contract(null, false)], RetrievedAt);

    private sealed class StubService(Func<CancellationToken, Task<DmdataContractSnapshot>> response) : IDmdataContractService
    {
        public int Calls { get; private set; }
        public Task<DmdataContractSnapshot> GetAsync(ProviderSettings settings, CancellationToken cancellationToken)
        {
            Calls++;
            return response(cancellationToken);
        }
    }
}
