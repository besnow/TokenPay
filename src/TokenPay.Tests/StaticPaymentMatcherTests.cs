using System.Threading.Channels;
using FreeSql;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TokenPay.Domains;
using TokenPay.Models;
using TokenPay.Services;

namespace TokenPay.Tests;

public class StaticPaymentMatcherTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"tokenpay-{Guid.NewGuid():N}.db");
    private readonly IFreeSql _db;
    private readonly Channel<TokenOrders> _channel = Channel.CreateUnbounded<TokenOrders>();
    private readonly StaticPaymentMatcher _matcher;

    public StaticPaymentMatcherTests()
    {
        _db = new FreeSqlBuilder().UseConnectionString(DataType.Sqlite, $"Data Source={_file}").UseAutoSyncStructure(true).Build();
        _db.CodeFirst.SyncStructure<TokenOrders>();
        _db.CodeFirst.SyncStructure<ChainPayment>();
        _matcher = new(_db, Options.Create(new StaticPaymentMatchOptions()), _channel, NullLogger<StaticPaymentMatcher>.Instance);
    }

    [Fact]
    public async Task Unique_candidate_is_paid_without_a_button()
    {
        var order = await AddOrder(DateTime.UtcNow.AddMinutes(-5));
        var result = await _matcher.ObserveAsync(Transfer(DateTime.UtcNow, "unique", 9m));
        Assert.Equal(PaymentMatchStatus.Matched, result.Status);
        Assert.Equal(OrderStatus.Paid, (await _db.Select<TokenOrders>().Where(x => x.Id == order.Id).FirstAsync()).Status);
        Assert.True(_channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Public_txid_never_claims_a_transfer_with_multiple_candidates()
    {
        var first = await AddOrder(DateTime.UtcNow.AddMinutes(-10));
        await AddOrder(DateTime.UtcNow.AddMinutes(-5));
        var observed = await _matcher.ObserveAsync(Transfer(DateTime.UtcNow, "aaaaaaaaaaaaaaaa", 10m));
        Assert.Equal(PaymentMatchStatus.Ambiguous, observed.Status);
        Assert.Empty(await _db.Select<TokenOrders>().Where(x => x.Status == OrderStatus.Paid).ToListAsync());
        var claimed = await _matcher.ClaimByTxIdAsync(first.Id, "aaaaaaaaaaaaaaaa", 7);
        Assert.Equal(PaymentMatchStatus.ManualReview, claimed.Status);
        Assert.Empty(await _db.Select<TokenOrders>().Where(x => x.Status == OrderStatus.Paid).ToListAsync());
    }

    [Fact]
    public async Task Abandoned_old_order_does_not_block_a_new_order()
    {
        await AddOrder(DateTime.UtcNow.AddHours(-2));
        var recent = await AddOrder(DateTime.UtcNow.AddMinutes(-5));
        var result = await _matcher.ObserveAsync(Transfer(DateTime.UtcNow, "bbbbbbbbbbbbbbbb", 10m));
        Assert.Equal(recent.Id, result.OrderId);
    }

    [Fact]
    public async Task Report_extends_only_the_late_order_window()
    {
        var late = await AddOrder(DateTime.UtcNow.AddHours(-2));
        await _matcher.ReportPaymentAsync(late.Id);
        var result = await _matcher.ObserveAsync(Transfer(DateTime.UtcNow, "cccccccccccccccc", 10m));
        Assert.Equal(late.Id, result.OrderId);
    }

    [Fact]
    public async Task Transfer_before_order_and_transfer_after_retention_do_not_match()
    {
        await AddOrder(DateTime.UtcNow);
        Assert.Equal(PaymentMatchStatus.Unmatched, (await _matcher.ObserveAsync(Transfer(DateTime.UtcNow.AddMinutes(-2), "before", 10m))).Status);
        await _db.Delete<TokenOrders>().Where(x => x.Status == OrderStatus.Pending).ExecuteAffrowsAsync();
        var old = await AddOrder(DateTime.UtcNow.AddHours(-25));
        Assert.Equal(PaymentMatchStatus.Expired, (await _matcher.ObserveAsync(Transfer(old.CreateTime.AddHours(25), "late", 10m) with { BlockTimeUtc = old.CreateTime.AddHours(25) })).Status);
    }

    [Fact]
    public async Task Under_minimum_fails_and_overpayment_records_actual_amount_once()
    {
        await AddOrder(DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(PaymentMatchStatus.AmountInsufficient, (await _matcher.ObserveAsync(Transfer(DateTime.UtcNow, "short", 8.999999m))).Status);
        var paid = await _matcher.ObserveAsync(Transfer(DateTime.UtcNow, "over", 12m));
        var repeated = await _matcher.ObserveAsync(Transfer(DateTime.UtcNow, "over", 12m));
        Assert.Equal(PaymentMatchStatus.Matched, paid.Status);
        Assert.Equal(PaymentMatchStatus.Matched, repeated.Status);
        Assert.Equal(12m, (await _db.Select<TokenOrders>().Where(x => x.Id == paid.OrderId).FirstAsync()).PayAmount);
        Assert.Single(await _db.Select<ChainPayment>().Where(x => x.TransactionHash == "over").ToListAsync());
    }

    [Fact]
    public async Task Transfer_index_makes_events_in_one_transaction_unique()
    {
        await AddOrder(DateTime.UtcNow.AddMinutes(-1));
        await _matcher.ObserveAsync(Transfer(DateTime.UtcNow, "multi", 8m) with { TransferIndex = 1 });
        await _matcher.ObserveAsync(Transfer(DateTime.UtcNow, "multi", 10m) with { TransferIndex = 2 });
        Assert.Equal(2, (await _db.Select<ChainPayment>().Where(x => x.TransactionHash == "multi").ToListAsync()).Count);
    }

    private async Task<TokenOrders> AddOrder(DateTime created)
    {
        var order = new TokenOrders { OutOrderId = Guid.NewGuid().ToString(), OrderUserKey = "u", Currency = "USDT_TRC20", ToAddress = "TStatic", Status = OrderStatus.Pending, IsStaticAddress = true, Amount = 10m, ActualAmount = 10m, LockedCoinPrice = 1m, OrderValueUsdt = 10m, AllowedUnderpayAmount = 1m, MinimumPaidAmount = 9m, CreateTime = created };
        await _db.Insert(order).ExecuteAffrowsAsync(); return order;
    }
    private static ObservedTransfer Transfer(DateTime time, string hash, decimal amount) => new("TRON", "USDT_TRC20", "contract", hash, 7, "from", "TStatic", amount, 1, time, 20);
    public void Dispose() { _db.Dispose(); if (File.Exists(_file)) File.Delete(_file); }
}
