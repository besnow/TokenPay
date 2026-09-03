using Flurl;
using Flurl.Http;
using FreeSql;
using System.Threading.Channels;
using TokenPay.Domains;
using TokenPay.Extensions;
using TokenPay.Helper;
using TokenPay.Models.TronModel;
using TokenPay.Services;

namespace TokenPay.BgServices
{
    public class OrderCheckTRXService : BaseScheduledService
    {
        private readonly IConfiguration _configuration;
        private readonly IHostEnvironment _env;
        private readonly Channel<TokenOrders> _channel;
        private readonly IFreeSql freeSql;
        private readonly IStaticPaymentMatcher matcher;
        private readonly ChainScanCursorStore cursors;
        private bool UseDynamicAddress => _configuration.GetValue("UseDynamicAddress", true);
        private bool UseDynamicAddressAmountMove => _configuration.GetValue("DynamicAddressConfig:AmountMove", false);

        public OrderCheckTRXService(ILogger<OrderCheckTRXService> logger,
            IConfiguration configuration,
            IHostEnvironment env,
            Channel<TokenOrders> channel,
            IFreeSql freeSql, IStaticPaymentMatcher matcher, ChainScanCursorStore cursors) : base("TRX订单检测", TimeSpan.FromSeconds(3), logger)
        {
            this._configuration = configuration;
            this._env = env;
            this._channel = channel;
            this.freeSql = freeSql;
            this.matcher = matcher;
            this.cursors = cursors;
        }

        protected override async Task ExecuteAsync(DateTime RunTime, CancellationToken stoppingToken)
        {
            var _repository = freeSql.GetRepository<TokenOrders>();
            var _TokensRepository = freeSql.GetRepository<Tokens>();

            var Address = await _repository
                .Where(x => x.Status == OrderStatus.Pending)
                .Where(x => x.Currency == "TRX")
                .Distinct()
                .ToListAsync(x => x.ToAddress);
            var BaseUrl = _configuration.GetValue("TronApiHost", "https://api.trongrid.io");
            if (!_env.IsProduction())
            {
                BaseUrl = "https://api.shasta.trongrid.io";
            }
            var OnlyConfirmed = _configuration.GetValue("OnlyConfirmed", true);
            foreach (var address in Address)
            {
                var cursor = await cursors.GetAsync("TRON", "TRX", address, "TRX",
                    _configuration.GetValue("StaticPaymentMatch:LatePaymentRetentionHours", 24), stoppingToken);
                //查询此地址待支付订单
                var orders = await _repository
                    .Where(x => x.Status == OrderStatus.Pending)
                    .Where(x => x.Currency == "TRX")
                    .Where(x => x.ToAddress == address)
                    .OrderBy(x => x.CreateTime)
                    .ToListAsync();
                if (!orders.Any())
                {
                    continue;
                }
                var maxTimestamp = DateTime.UtcNow.ToUnixTimeStamp();
                var data = await ScanPagination.ReadTronAsync<TrxTransaction>(async (fingerprint, ct) =>
                {
                    var query = new Dictionary<string, object> { ["only_to"] = true, ["limit"] = 50,
                        ["order_by"] = "block_timestamp,asc", ["min_timestamp"] = cursor.LastBlockTimeUtc.AddMinutes(-2).ToUnixTimeStamp(),
                        ["max_timestamp"] = maxTimestamp };
                    if (OnlyConfirmed) query["only_confirmed"] = true;
                    if (!string.IsNullOrWhiteSpace(fingerprint)) query["fingerprint"] = fingerprint;
                    var req = BaseUrl.AppendPathSegment($"v1/accounts/{address}/transactions").SetQueryParams(query).WithTimeout(15);
                    if (_env.IsProduction()) req = req.WithHeader("TRON-PRO-API-KEY", _configuration.GetValue<string>("TRON-PRO-API-KEY"));
                    var page = await req.GetJsonAsync<BaseResponse<TrxTransaction>>(cancellationToken: ct);
                    return (page.Success, (IReadOnlyList<TrxTransaction>)(page.Data ?? []), page.Meta?.Fingerprint);
                }, cursor.ContinuationToken, async (token, ct) =>
                    await cursors.AdvanceAsync(cursor, cursor.LastBlockNumber, cursor.LastBlockTimeUtc, token, ct), stoppingToken);

                if (data.Count > 0)
                {
                    foreach (var item in data)
                    {
                        //没有需要匹配的订单了
                        if (!orders.Any())
                        {
                            break;
                        }
                        var raw = item.RawData.Contract.FirstOrDefault(x => x.Type == "TransferContract")?.Parameter?.Value;
                        if (raw == null || raw.AssetName != null) continue;
                        if (!UseDynamicAddress)
                        {
                            await matcher.ObserveAsync(new("TRON", "TRX", null, item.TxID, "native",
                                raw.OwnerAddress.HexToeBase58(), raw.ToAddressBase58, raw.RealAmount, 0,
                                item.BlockTimestamp.ToDateTime(), OnlyConfirmed ? 1 : 0), stoppingToken);
                            continue;
                        }
                        //此交易已被其他订单使用
                        if (await _repository.Select.AnyAsync(x => x.BlockTransactionId == item.TxID))
                        {
                            continue;
                        }
                        var token = await _TokensRepository.Where(x => x.Currency == TokenCurrency.TRX && x.Address == raw.ToAddressBase58).FirstAsync();
                        if (token != null)
                        {
                            token.Value += raw.RealAmount;
                            await _TokensRepository.UpdateAsync(token);
                        }
                        var order = orders.Where(x => x.Amount == raw.RealAmount && x.ToAddress == raw.ToAddressBase58 && x.CreateTime < item.BlockTimestamp.ToDateTime())
                            .OrderByDescending(x => x.CreateTime)//优先付最后一单
                            .FirstOrDefault();
                    recheck:
                        if (order != null)
                        {
                            order.FromAddress = raw.OwnerAddress.HexToeBase58();
                            order.BlockTransactionId = item.TxID;
                            order.Status = OrderStatus.Paid;
                            order.PayTime = item.BlockTimestamp.ToDateTime();
                            order.PayAmount = raw.RealAmount;
                            await _repository.UpdateAsync(order);
                            orders.Remove(order);
                            await SendAdminMessage(order);
                        }
                        else
                        {
                            if (UseDynamicAddress && UseDynamicAddressAmountMove)
                            {
                                //允许非准确金额支付
                                var Move = _configuration.GetSection("DynamicAddressConfig:TRX").Get<decimal[]>() ?? [];
                                if (Move.Length == 2)
                                {
                                    var Down = Move[0]; //上浮金额
                                    var Up = Move[1]; //下浮金额
                                    order = orders.Where(x => raw.RealAmount >= x.Amount - Down && raw.RealAmount <= x.Amount + Up)
                                        .Where(x => x.ToAddress == raw.ToAddressBase58 && x.CreateTime < item.BlockTimestamp.ToDateTime())
                                        .OrderByDescending(x => x.CreateTime)//优先付最后一单
                                        .FirstOrDefault();
                                    if (order != null)
                                    {
                                        order.IsDynamicAmount = true;
                                        goto recheck;
                                    }
                                }
                            }
                        }
                    }
                }
                var completedThrough = data.Count > 0 ? data.Max(x => x.BlockTimestamp).ToDateTime() : DateTime.UtcNow;
                await cursors.AdvanceAsync(cursor, 0, completedThrough, null, stoppingToken);
            }
        }
        private async Task SendAdminMessage(TokenOrders order)
        {
            await _channel.Writer.WriteAsync(order);
        }
    }
}
