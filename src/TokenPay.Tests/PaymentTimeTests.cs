using TokenPay.Helper;

namespace TokenPay.Tests;

public class PaymentTimeTests
{
    [Fact]
    public void Legacy_unspecified_utc_plus_eight_time_is_converted_explicitly()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        var stored = new DateTime(2026, 9, 3, 20, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc), PaymentTime.ToUtc(stored, zone));
    }
}
