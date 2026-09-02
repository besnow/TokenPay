using TokenPay.Domains;
using TokenPay.StaticPayments;

namespace TokenPay.Tests;

public sealed class StaticPaymentPolicyTests
{
    private static readonly TimeZoneInfo Utc8 = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    private static readonly StaticPaymentOptions Options = new();

    private static TokenOrders Order(DateTime created, decimal minimum = 10m) => new()
    {
        Currency = "USDT_TRC20", ToAddress = "T-address", Amount = 10m,
        ExpectedAmount = 10m, MinimumPaidAmount = minimum, CreateTime = created,
        IsStaticAddress = true, Status = OrderStatus.Pending, OutOrderId = "out",
        OrderUserKey = "user"
    };

    private static ChainPayment Transfer(DateTime utc, decimal amount = 10m) => new()
    {
        Network = "TRON", Currency = "USDT_TRC20", ToAddress = "T-address",
        TransactionHash = new string('a', 64), TransferIndex = 0,
        Amount = amount, BlockTimeUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc), Succeeded = true
    };

    [Fact]
    public void UniqueOrdinaryCandidateDoesNotRequireReport()
    {
        var created = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var candidates = StaticPaymentPolicy.Candidates([Order(created)], Transfer(new DateTime(2026, 1, 1, 4, 30, 0)), Options, Utc8);
        Assert.Single(candidates);
        Assert.Equal(PaymentMatchStatus.Matched, StaticPaymentPolicy.Classify(candidates, Transfer(new DateTime(2026, 1, 1, 4, 30, 0))));
    }

    [Fact]
    public void AbandonedOldOrderDoesNotBlockNewOrder()
    {
        var old = Order(new DateTime(2026, 1, 1, 8, 0, 0));
        var recent = Order(new DateTime(2026, 1, 1, 12, 0, 0));
        var candidates = StaticPaymentPolicy.Candidates([old, recent], Transfer(new DateTime(2026, 1, 1, 4, 30, 0)), Options, Utc8);
        Assert.Same(recent, Assert.Single(candidates));
    }

    [Fact]
    public void ReportExtendsLateRecognitionOnlyWithinBounds()
    {
        var order = Order(new DateTime(2026, 1, 1, 12, 0, 0));
        order.PaymentReportedAtUtc = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc);
        Assert.Single(StaticPaymentPolicy.Candidates([order], Transfer(new DateTime(2026, 1, 1, 5, 45, 0)), Options, Utc8));
        Assert.Empty(StaticPaymentPolicy.Candidates([order], Transfer(new DateTime(2026, 1, 2, 5, 0, 1)), Options, Utc8));
    }

    [Fact]
    public void MultipleCandidatesAreAlwaysAmbiguous()
    {
        var at = new DateTime(2026, 1, 1, 12, 0, 0);
        var transfer = Transfer(new DateTime(2026, 1, 1, 4, 30, 0));
        var candidates = StaticPaymentPolicy.Candidates([Order(at), Order(at.AddMinutes(2))], transfer, Options, Utc8);
        Assert.Equal(PaymentMatchStatus.Ambiguous, StaticPaymentPolicy.Classify(candidates, transfer));
    }

    [Theory]
    [InlineData(1.47, 0.147)]
    [InlineData(10, 1)]
    [InlineData(20, 2)]
    [InlineData(100, 2)]
    public void UnderpayRuleIsCapped(decimal value, decimal expected) =>
        Assert.Equal(expected, StaticPaymentPolicy.AllowedUnderpay(value, 1m, Options));

    [Fact]
    public void BelowMinimumByOneUnitFails()
    {
        var order = Order(DateTime.UtcNow, 9.000001m);
        Assert.Equal(PaymentMatchStatus.AmountInsufficient,
            StaticPaymentPolicy.Classify([order], Transfer(DateTime.UtcNow, 9m)));
    }

    [Fact]
    public void OverpayMatchesWithoutChangingOrderAmount()
    {
        var order = Order(DateTime.UtcNow);
        Assert.Equal(PaymentMatchStatus.Matched,
            StaticPaymentPolicy.Classify([order], Transfer(DateTime.UtcNow, 11m)));
        Assert.Equal(10m, order.Amount);
        Assert.False(Options.CreditOverpay);
    }

    [Fact]
    public void SmallEthOrderKeepsFractionalTolerance()
    {
        var allowed = StaticPaymentPolicy.AllowedUnderpay(9.9m, 20_000m, Options);
        Assert.Equal(0.0000495m, allowed);
        Assert.NotEqual(0m, allowed);
    }

    [Theory]
    [InlineData("EVM_ETH_ETH", 8)]
    [InlineData("EVM_POLYGON_USDT_ERC20", 6)]
    [InlineData("USDT_TRC20", 6)]
    public void CurrencyPrecisionIsRecognized(string currency, int decimals) =>
        Assert.Equal(decimals, StaticPaymentPolicy.DisplayDecimals(currency));

    [Fact]
    public void Utc8LegacyTimeDoesNotImmediatelyExpire()
    {
        var local = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(new DateTime(2026, 1, 1, 4, 0, 0, DateTimeKind.Utc), PaymentTime.ToUtc(local, Utc8));
    }

    [Fact]
    public void MigrationOnlyTouchesLegacyPendingStaticOrders()
    {
        var pending = Order(DateTime.Now); pending.IsStaticAddress = false; pending.MinimumPaidAmount = 0;
        var paid = Order(DateTime.Now); paid.IsStaticAddress = false; paid.Status = OrderStatus.Paid;
        Assert.Equal(1, LegacyStaticOrderMigration.UpgradePendingOrders([pending, paid], false));
        Assert.True(pending.IsStaticAddress);
        Assert.Equal(pending.Amount, pending.MinimumPaidAmount);
        Assert.False(paid.IsStaticAddress);
    }

    [Fact]
    public void DynamicDeploymentDoesNotMigrateOrders()
    {
        var pending = Order(DateTime.Now); pending.IsStaticAddress = false;
        Assert.Equal(0, LegacyStaticOrderMigration.UpgradePendingOrders([pending], true));
        Assert.False(pending.IsStaticAddress);
    }
}
