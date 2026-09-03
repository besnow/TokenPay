using System.Globalization;
using System.Numerics;

namespace TokenPay.Services;

public static class ChainTransactionHash
{
    public static string Normalize(string network, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException("Transaction hash is required.");
        var raw = value.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = raw[2..];
        if (raw.Length != 64 || raw.Any(c => !Uri.IsHexDigit(c)))
            throw new FormatException($"{network} transaction hash must contain exactly 64 hexadecimal characters.");
        return network.Equals("TRON", StringComparison.OrdinalIgnoreCase)
            ? raw.ToUpperInvariant()
            : "0x" + raw.ToLowerInvariant();
    }
}

public static class EvmValueConverter
{
    public static BigInteger ParseUnsignedHex(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException("EVM quantity is empty.");
        var raw = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (raw.Length == 0 || raw.Any(c => !Uri.IsHexDigit(c))) throw new FormatException("EVM quantity is not hexadecimal.");
        return BigInteger.Parse("0" + raw, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    public static decimal ToDecimal(string value, int decimals)
    {
        var integer = ParseUnsignedHex(value);
        var divisor = Helper.PaymentAmountCalculator.DecimalPower(decimals);
        if (integer > new BigInteger(decimal.MaxValue) * new BigInteger(divisor))
            throw new OverflowException("EVM quantity exceeds the supported decimal range.");
        return (decimal)integer / divisor;
    }
}

public sealed class ChainQueryException(string message, Exception? inner = null) : Exception(message, inner);
