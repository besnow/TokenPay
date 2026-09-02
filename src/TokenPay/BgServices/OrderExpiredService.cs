using FreeSql;
using TokenPay.Domains;

namespace TokenPay.BgServices
{
    public class OrderExpiredService : BaseScheduledService
    {
        private readonly IConfiguration _configuration;
        private readonly IFreeSql freeSql;

        public OrderExpiredService(ILogger<OrderExpiredService> logger,
            IConfiguration configuration,
            IFreeSql freeSql) : base("订单过期", TimeSpan.FromSeconds(10), logger)
        {
            this._configuration = configuration;
            this.freeSql = freeSql;
        }

        protected override async Task ExecuteAsync(DateTime RunTime, CancellationToken stoppingToken)
        {
            var _repository = freeSql.GetRepository<TokenOrders>();

            var ExpireTime = _configuration.GetValue("ExpireTime", 10 * 60);
            var ExpireDateTime = DateTime.Now.AddSeconds(-1 * ExpireTime);
            var staticRetention = _configuration.GetValue("StaticPaymentMatch:LatePaymentRetentionHours", 24);
            var staticExpireDateTime = DateTime.UtcNow.AddHours(-staticRetention);
            var ExpiredOrders = await _repository.Where(x => x.Status == OrderStatus.Pending)
                .Where(x => (!x.IsStaticAddress && x.CreateTime < ExpireDateTime) || (x.IsStaticAddress && x.CreateTime < staticExpireDateTime))
                .ToListAsync();
            foreach (var order in ExpiredOrders)
            {
                _logger.LogInformation("订单[{c}]过期了！", order.Id);
                order.Status = OrderStatus.Expired;
                await _repository.UpdateAsync(order);
            }
        }
    }
}
