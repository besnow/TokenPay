namespace TokenPay.Helper;

/// <summary>Converts legacy database wall-clock values at the payment boundary.</summary>
public static class PaymentTime
{
    public static DateTime ToUtc(DateTime value, TimeZoneInfo? legacyZone = null)
    {
        if (value.Kind == DateTimeKind.Utc) return value;
        if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
        return TimeZoneInfo.ConvertTimeToUtc(value, legacyZone ?? TimeZoneInfo.Local);
    }
}
