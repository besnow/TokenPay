namespace TokenPay.StaticPayments;

public static class PaymentTime
{
    // Historical TokenPay timestamps were local, Kind=Unspecified. Convert them
    // with the configured deployment zone instead of incorrectly assuming UTC.
    public static DateTime ToUtc(DateTime value, TimeZoneInfo legacyTimeZone) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), legacyTimeZone)
    };

    public static DateTime UnixMillisecondsToUtc(long value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
}
