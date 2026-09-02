using TokenPay.Services;

namespace TokenPay.BgServices;

public sealed class StaticPaymentRetryService : BaseScheduledService
{
    private readonly IStaticPaymentMatcher _matcher;
    public StaticPaymentRetryService(IStaticPaymentMatcher matcher, ILogger<StaticPaymentRetryService> logger)
        : base("静态到账重新匹配", TimeSpan.FromSeconds(30), logger) => _matcher = matcher;
    protected override Task ExecuteAsync(DateTime runTime, CancellationToken stoppingToken) => _matcher.RetryUnmatchedAsync(stoppingToken);
}
