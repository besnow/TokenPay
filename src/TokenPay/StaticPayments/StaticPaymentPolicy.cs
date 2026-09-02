using TokenPay.Domains;

namespace TokenPay.StaticPayments;

public static class StaticPaymentPolicy
{
    public static int DisplayDecimals(string currency) =>
        currency.StartsWith("EVM_", StringComparison.OrdinalIgnoreCase) &&
        currency.EndsWith("_ETH", StringComparison.OrdinalIgnoreCase) ? 8 :
        currency.Contains("USDT", StringComparison.OrdinalIgnoreCase) ? 6 : 8;

    public static decimal AllowedUnderpay(decimal orderValueUsdt, decimal lockedCoinPrice,
        StaticPaymentOptions options)
    {
        if (lockedCoinPrice <= 0m) throw new ArgumentOutOfRangeException(nameof(lockedCoinPrice));
        var allowedUsdt = Math.Min(options.MaxUnderpayUsd, orderValueUsdt * options.MaxUnderpayPercent);
        return allowedUsdt / lockedCoinPrice;
    }

    public static IReadOnlyList<TokenOrders> Candidates(IEnumerable<TokenOrders> orders,
        ChainPayment transfer, StaticPaymentOptions options, TimeZoneInfo legacyTimeZone,
        TimeSpan? clockSkew = null)
    {
        var skew = clockSkew ?? TimeSpan.FromMinutes(2);
        return orders.Where(o => o.Status == OrderStatus.Pending && o.IsStaticAddress &&
                StringComparer.OrdinalIgnoreCase.Equals(o.Currency, transfer.Currency) &&
                StringComparer.OrdinalIgnoreCase.Equals(o.ToAddress, transfer.ToAddress))
            .Where(o =>
            {
                var created = PaymentTime.ToUtc(o.CreateTime, legacyTimeZone);
                var ordinary = created <= transfer.BlockTimeUtc + skew &&
                               transfer.BlockTimeUtc <= created.AddMinutes(options.AutoWindowMinutes);
                var retained = o.PaymentReportedAtUtc.HasValue &&
                               transfer.BlockTimeUtc >= o.PaymentReportedAtUtc.Value.AddMinutes(-30) &&
                               transfer.BlockTimeUtc <= created.AddHours(options.LatePaymentRetentionHours);
                return ordinary || retained;
            }).ToArray();
    }

    public static PaymentMatchStatus Classify(IReadOnlyCollection<TokenOrders> candidates,
        ChainPayment transfer)
    {
        if (candidates.Count == 0) return PaymentMatchStatus.Unmatched;
        if (candidates.Count > 1) return PaymentMatchStatus.Ambiguous;
        return transfer.Amount >= candidates.Single().MinimumPaidAmount
            ? PaymentMatchStatus.Matched : PaymentMatchStatus.AmountInsufficient;
    }
}
