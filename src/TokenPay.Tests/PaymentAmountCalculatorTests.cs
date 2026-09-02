using TokenPay.Helper;
using TokenPay.Models;

namespace TokenPay.Tests;

public class PaymentAmountCalculatorTests
{
    private static readonly StaticPaymentMatchOptions Options = new();

    [Theory]
    [InlineData("1.47", "0.147", "1.323")]
    [InlineData("10", "1", "9")]
    [InlineData("20", "2", "18")]
    [InlineData("50", "2", "48")]
    public void Usdt_underpayment_is_ten_percent_capped_at_two(string value, string allowed, string minimum)
    {
        var result = PaymentAmountCalculator.Calculate(decimal.Parse(value), decimal.Parse(value), 1m, Options, 6);
        Assert.Equal(decimal.Parse(allowed), result.allowedUnderpayCoin);
        Assert.Equal(decimal.Parse(minimum), result.minimumPaidAmount);
    }

    [Fact]
    public void Eth_allowance_is_converted_from_usdt_value()
    {
        var result = PaymentAmountCalculator.Calculate(0.01m, 20m, 2_000m, Options, 18);
        Assert.Equal(0.001m, result.allowedUnderpayCoin);
        Assert.Equal(0.009m, result.minimumPaidAmount);
    }

    [Fact]
    public void Trx_allowance_is_converted_from_usdt_value()
    {
        var result = PaymentAmountCalculator.Calculate(200m, 20m, 0.10m, Options, 6);
        Assert.Equal(20m, result.allowedUnderpayCoin);
        Assert.Equal(180m, result.minimumPaidAmount);
    }

    [Fact]
    public void High_precision_is_not_truncated()
    {
        var result = PaymentAmountCalculator.Calculate(0.001234567890123456m, 10m, 10_000m, Options, 18);
        Assert.Equal(0.0001m, result.allowedUnderpayCoin);
        Assert.Equal(0.001134567890123456m, result.minimumPaidAmount);
    }
}
