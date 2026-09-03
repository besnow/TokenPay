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
        _db.CodeFirst.SyncStructure<PaymentClaim>();
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
        var claimed = await _matcher.ClaimByTxIdAsync(first.Id, Hash("aaaaaaaaaaaaaaaa"), "event:7");
        Assert.Equal(PaymentMatchStatus.Matched, claimed.Status);
        Assert.Single(await _db.Select<TokenOrders>().Where(x => x.Status == OrderStatus.Paid).ToListAsync());
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
    public async Task Txid_resolves_chain_when_payment_is_not_local()
    {
        var order = await AddOrder(DateTime.UtcNow.AddMinutes(-2));
        var resolver = new FakeResolver(Transfer(DateTime.UtcNow, "dddddddddddddddd", 10m));
        var matcher = new StaticPaymentMatcher(_db, Options.Create(new StaticPaymentMatchOptions()), _channel,
            NullLogger<StaticPaymentMatcher>.Instance, new[] { resolver });
        var result = await matcher.ClaimByTxIdAsync(order.Id, Hash("dddddddddddddddd"), "event:7");
        Assert.True(resolver.Called);
        Assert.Equal(PaymentMatchStatus.Matched, result.Status);
        Assert.Single(await _db.Select<ChainPayment>().Where(x => x.TransactionHash == Hash("dddddddddddddddd")).ToListAsync());
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
        Assert.Single(await _db.Select<ChainPayment>().Where(x => x.TransactionHash == Hash("over")).ToListAsync());
    }

    [Fact]
    public async Task Txid_claim_uses_symmetric_locked_amount_limit()
    {
        var order = await AddOrder(DateTime.UtcNow.AddMinutes(-1));
        await _matcher.ObserveAsync(Transfer(DateTime.UtcNow, "eeeeeeeeeeeeeeee", 12m));
        // The automatic scanner accepts a unique overpayment. Use an ambiguous pair
        // so the transfer remains available to exercise direct-claim limits.
        Assert.Equal(OrderStatus.Paid, (await _db.Select<TokenOrders>().Where(x => x.Id == order.Id).FirstAsync()).Status);

        var first = await AddOrder(DateTime.UtcNow.AddMinutes(-1));
        await AddOrder(DateTime.UtcNow.AddMinutes(-1));
        await _matcher.ObserveAsync(Transfer(DateTime.UtcNow, "ffffffffffffffff", 12m));
        var result = await _matcher.ClaimByTxIdAsync(first.Id, Hash("ffffffffffffffff"), "event:7");
        Assert.Equal(PaymentMatchStatus.ClaimRejected, result.Status);
        Assert.Equal(OrderStatus.Pending, (await _db.Select<TokenOrders>().Where(x => x.Id == first.Id).FirstAsync()).Status);
    }

    [Fact]
    public async Task Hash_case_variant_cannot_pay_a_second_order_and_repeat_is_idempotent()
    {
        var first = await AddOrder(DateTime.UtcNow.AddMinutes(-2));
        var lower = new string('a', 64);
        Assert.Equal(PaymentMatchStatus.Matched, (await _matcher.ObserveAsync(
            new("TRON", "USDT_TRC20", "contract", lower, "event:7", "from", "TStatic", 10m, 1, DateTime.UtcNow, 20))).Status);
        Assert.Equal(PaymentMatchStatus.Matched,
            (await _matcher.ClaimByTxIdAsync(first.Id, lower.ToUpperInvariant(), "event:7")).Status);

        var second = await AddOrder(DateTime.UtcNow.AddMinutes(-1));
        var rejected = await _matcher.ClaimByTxIdAsync(second.Id, lower.ToLowerInvariant(), "event:7");
        Assert.Equal(PaymentMatchStatus.AlreadyUsed, rejected.Status);
        Assert.Single(await _db.Select<ChainPayment>().Where(x => x.TransactionHash == lower.ToUpperInvariant()).ToListAsync());
        Assert.Single(await _db.Select<TokenOrders>().Where(x => x.Status == OrderStatus.Paid).ToListAsync());
        Assert.True(_channel.Reader.TryRead(out _));
        Assert.False(_channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Transfer_index_makes_events_in_one_transaction_unique()
    {
        await AddOrder(DateTime.UtcNow.AddMinutes(-1));
        await _matcher.ObserveAsync(Transfer(DateTime.UtcNow, "multi", 8m) with { TransferKey = "event:1" });
        await _matcher.ObserveAsync(Transfer(DateTime.UtcNow, "multi", 10m) with { TransferKey = "event:2" });
        Assert.Equal(2, (await _db.Select<ChainPayment>().Where(x => x.TransactionHash == Hash("multi")).ToListAsync()).Count);
    }

    private async Task<TokenOrders> AddOrder(DateTime created)
    {
        var order = new TokenOrders { OutOrderId = Guid.NewGuid().ToString(), OrderUserKey = "u", Currency = "USDT_TRC20", ToAddress = "TStatic", Status = OrderStatus.Pending, IsStaticAddress = true, Amount = 10m, ActualAmount = 10m, LockedCoinPrice = 1m, OrderValueUsdt = 10m, AllowedUnderpayAmount = 1m, MinimumPaidAmount = 9m, CreateTime = created };
        await _db.Insert(order).ExecuteAffrowsAsync(); return order;
    }
    private static string Hash(string seed) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)));
    private static ObservedTransfer Transfer(DateTime time, string hash, decimal amount) => new("TRON", "USDT_TRC20", "contract", Hash(hash), "event:7", "from", "TStatic", amount, 1, time, 20);
    public void Dispose() { _db.Dispose(); if (File.Exists(_file)) File.Delete(_file); }

    private sealed class FakeResolver(ObservedTransfer transfer) : IChainTransactionResolver
    {
        public bool Called { get; private set; }
        public bool CanResolve(TokenOrders order) => true;
        public Task<IReadOnlyList<ObservedTransfer>> ResolveAsync(TokenOrders order, string transactionHash, CancellationToken cancellationToken)
        {
            Called = true;
            return Task.FromResult<IReadOnlyList<ObservedTransfer>>(new[] { transfer });
        }
    }
}
