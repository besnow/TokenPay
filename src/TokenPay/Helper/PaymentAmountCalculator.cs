using TokenPay.Models;

namespace TokenPay.Helper;

public static class PaymentAmountCalculator
{
    public static (decimal allowedUnderpayCoin, decimal minimumPaidAmount) Calculate(
        decimal expectedAmount, decimal orderValueUsdt, decimal coinPriceUsdt,
        StaticPaymentMatchOptions options, int coinDecimals)
    {
        if (expectedAmount <= 0 || orderValueUsdt <= 0 || coinPriceUsdt <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedAmount));
        var allowedUsd = Math.Min(options.MaxUnderpayUsd, orderValueUsdt * options.MaxUnderpayPercent);
        var factor = DecimalPower(coinDecimals);
        var allowedCoin = Math.Floor(allowedUsd / coinPriceUsdt * factor) / factor;
        return (allowedCoin, Math.Max(0, expectedAmount - allowedCoin));
    }

    public static decimal DecimalPower(int decimals)
    {
        decimal value = 1;
        for (var i = 0; i < Math.Clamp(decimals, 0, 18); i++) value *= 10;
        return value;
    }
}
