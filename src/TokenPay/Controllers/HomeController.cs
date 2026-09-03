using FreeSql;
using HDWallet.Tron;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Org.BouncyCastle.Bcpg;
using SkiaSharp;
using SkiaSharp.QrCode.Image;
using System.Diagnostics;
using System.Reflection;
using TokenPay.Domains;
using TokenPay.Extensions;
using TokenPay.Helper;
using TokenPay.Models.EthModel;
using TokenPay.Models;
using TokenPay.Services;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.RateLimiting;

namespace TokenPay.Controllers
{
    [Route("{action}")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class HomeController : Controller
    {
        private readonly IBaseRepository<TokenOrders> _repository;
        private readonly IBaseRepository<TokenRate> _rateRepository;
        private readonly IBaseRepository<Tokens> _tokenRepository;
        private readonly List<EVMChain> _chains;
        private readonly IHostEnvironment _env;
        private readonly ILogger<HomeController> _logger;
        private readonly IConfiguration _configuration;
        private readonly IStaticPaymentMatcher _staticMatcher;
        private readonly StaticPaymentMatchOptions _staticOptions;
        private FiatCurrency BaseCurrency => Enum.Parse<FiatCurrency>(_configuration.GetValue("BaseCurrency", "CNY")!);
        public static int GetDecimals(string currency, IConfiguration configuration, IReadOnlyList<EVMChain>? chains = null)
        {
            if (currency == "TRX") return configuration.GetValue("Decimals:TRX", 6);
            if (currency == "USDT_TRC20") return 6;
            if (currency.StartsWith("EVM_", StringComparison.Ordinal) && chains != null)
            {
                var parts = currency.Split('_');
                var chain = chains.FirstOrDefault(x => string.Equals(x.ChainNameEN, parts.ElementAtOrDefault(1), StringComparison.Ordinal));
                if (chain != null)
                {
                    if (parts.Length == 3 && string.Equals(parts[2], chain.BaseCoin, StringComparison.Ordinal)) return chain.Decimals;
                    var token = chain.ERC20?.FirstOrDefault(x => parts.Contains(x.Name, StringComparer.Ordinal));
                    if (token?.Decimals != null) return token.Decimals.Value;
                }
            }
            throw new InvalidOperationException($"No configured decimals for enabled currency {currency}");
        }
        private List<string> GetErc20Name()
        {
            var list = new List<string>();
            foreach (var item in _chains)
            {
                list.Add(item.ERC20Name);
            }
            list = list.Distinct().ToList();
            return list;
        }

        private decimal GetRate(string currency)
        {
            var erc20Names = GetErc20Name();
            foreach (var item in erc20Names)
            {
                currency = currency.Replace(item, "");
            }
            var _currency = currency.Replace("TRC20", "").Split("_", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();
            var value = _currency switch
            {
                "TRX" => _configuration.GetValue("Rate:TRX", 0m),
                "ETH" => _configuration.GetValue("Rate:ETH", 0m),
                "USDT" => _configuration.GetValue("Rate:USDT", 0m),
                "USDC" => _configuration.GetValue("Rate:USDC", 0m),
                _ => _configuration.GetValue($"Rate:{_currency}", 0m)
            };
            return value;
        }
        public static List<string> GetActiveCurrency(List<EVMChain> chains)
        {
            var list = new List<string>()
            {
                "TRX","USDT_TRC20"
            };
            foreach (var chain in chains)
            {
                if (chain == null || !chain.Enable || chain.ERC20 == null) continue;
                list.Add($"EVM_{chain.ChainNameEN}_{chain.BaseCoin}");
                foreach (var erc20 in chain.ERC20)
                {
                    list.Add($"EVM_{chain.ChainNameEN}_{erc20.Name}_{chain.ERC20Name}");
                }
            }
            return list;
        }
        public HomeController(IBaseRepository<TokenOrders> repository,
            IBaseRepository<TokenRate> rateRepository,
            IBaseRepository<Tokens> tokenRepository,
            List<EVMChain> chain,
            IHostEnvironment env,
            ILogger<HomeController> logger,
            IConfiguration configuration, IStaticPaymentMatcher staticMatcher, IOptions<StaticPaymentMatchOptions> staticOptions)
        {
            this._repository = repository;
            this._rateRepository = rateRepository;
            this._tokenRepository = tokenRepository;
            this._chains = chain;
            this._env = env;
            this._logger = logger;
            this._configuration = configuration;
            this._staticMatcher = staticMatcher;
            this._staticOptions = staticOptions.Value;
        }
        [Route("/")]
        public IActionResult Index()
        {
            return View();
        }

        public async Task<IActionResult> Pay(Guid Id)
        {
            var order = await _repository.Where(x => x.Id == Id).FirstAsync();
            if (order == null)
            {
                return View(order);
            }
            ViewData["QrCode"] = Convert.ToBase64String(CreateQrCode(order.ToAddress));
            var ExpireTime = _configuration.GetValue("ExpireTime", 10 * 60);
            var effectiveExpire = order.IsStaticAddress
                ? PaymentTime.ToUtc(order.CreateTime).AddHours(_staticOptions.LatePaymentRetentionHours)
                : order.CreateTime.AddSeconds(ExpireTime);
            if (DateTime.UtcNow > effectiveExpire || order.Status == OrderStatus.Expired)
            {
                return View("OrderExpired", order);
            }
            ViewData["ExpireTime"] = effectiveExpire;
            return View(order);
        }
        [HttpGet]
        [ApiExplorerSettings(IgnoreApi = false)]
        public async Task<IActionResult> Query(Guid Id, string Signature)
        {
            if (_env.IsProduction())
            {
                if (!VerifySignature(new
                {
                    Id,
                    Signature
                }))
                {
                    return Json(new ReturnData
                    {
                        Message = "签名验证失败！"
                    });
                }
            }
            var order = await _repository.Where(x => x.Id == Id).FirstAsync();
            if (order == null)
            {
                return Json(new ReturnData
                {
                    Message = "订单不存在！"
                });
            }
            return Json(new ReturnData<TokenOrders>
            {
                Success = true,
                Message = "订单信息获取成功！",
                Data = order,
            });
        }
        [Route("/{action}/{id}")]
        public async Task<IActionResult> Check(Guid Id)
        {
            var order = await _repository.Where(x => x.Id == Id).FirstAsync();
            if (order == null)
            {
                return Content(OrderStatus.Pending.ToString());
            }
            if (order.Status == OrderStatus.Pending && order.IsStaticAddress)
            {
                return Content(order.PaymentMatchStatus == PaymentMatchStatus.Waiting
                    ? OrderStatus.Pending.ToString()
                    : order.PaymentMatchStatus.ToString());
            }
            return Content(order.Status.ToString());
        }
        private bool VerifySignature(object model)
        {
            if (model == null) return false;
            var dic = new SortedDictionary<string, string?>();
            PropertyInfo[] properties = model.GetType().GetProperties();
            if (properties.Length <= 0) { return false; }
            foreach (PropertyInfo item in properties)
            {
                string name = item.Name;
                string? value = item.GetValue(model, null)?.ToString();
                if (string.IsNullOrEmpty(value)) continue;
                dic.Add(name, value);
            }
            if (dic.TryGetValue("Signature", out var Signature))
            {
                dic.Remove("Signature");
                var SignatureStr = string.Join("&", dic.Select(x => $"{x.Key}={x.Value}"));
                var ApiToken = _configuration.GetValue<string>("ApiToken");
                SignatureStr += ApiToken;
                var md5 = SignatureStr.ToMD5();
                return Signature == md5;
            }
            return false;
        }
        /// <summary>
        /// 创建订单
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("/" + nameof(CreateOrder))]
        [ApiExplorerSettings(IgnoreApi = false)]
        public async Task<IActionResult> CreateOrder([FromBody] CreateOrderViewModel model)
        {
            if (!ModelState.IsValid)
            {
                string messages = string.Join("; ", ModelState.Values
                                        .SelectMany(x => x.Errors)
                                        .Select(x => x.ErrorMessage));

                return Json(new ReturnData
                {
                    Message = messages
                });
            }
            if (_env.IsProduction())
            {
                if (!VerifySignature(model))
                {
                    return Json(new ReturnData
                    {
                        Message = "签名验证失败！"
                    });
                }
            }
            if (!GetActiveCurrency(_chains).Contains(model.Currency))
            {
                return Json(new ReturnData
                {
                    Message = $"不支持的币种【{model.Currency}】！\n当前支持的币种参数有：{string.Join(", ", GetActiveCurrency(_chains))}"
                });
            }
            if (model.ActualAmount <= 0)
            {
                return Json(new ReturnData
                {
                    Message = "金额有误！"
                });
            }
            //订单号已存在
            var hasOrder = await _repository.Where(x => x.OutOrderId == model.OutOrderId && x.Currency == model.Currency)
                .Where(x => x.Status != OrderStatus.Expired)
                .FirstAsync();
            if (hasOrder != null)
            {
                return Json(new ReturnData<string>
                {
                    Success = true,
                    Message = "订单已存在，查询旧订单！",
                    Data = Host + Url.Action(nameof(Pay), new { Id = hasOrder.Id }),
                    Info = ToPayDic(hasOrder)
                });
            }
            var order = new TokenOrders
            {
                OutOrderId = model.OutOrderId,
                OrderUserKey = model.OrderUserKey,
                Status = OrderStatus.Pending,
                Currency = model.Currency,
                ActualAmount = model.ActualAmount,
                NotifyUrl = model.NotifyUrl,
                RedirectUrl = model.RedirectUrl,
                PassThroughInfo = model.PassThroughInfo,
            };
            var UseDynamicAddress = _configuration.GetValue("UseDynamicAddress", true);
            try
            {
                if (UseDynamicAddress)
                {
                    var (Address, Amount) = await GetUseTokenDynamicAdress(model);
                    order.ToAddress = Address;
                    order.Amount = Amount;
                }
                else
                {
                    var (Address, Amount, Rate) = await GetUseTokenStaticAdress(model);
                    order.ToAddress = Address;
                    order.Amount = Amount;
                    order.IsStaticAddress = true;
                    order.LockedCoinPrice = Rate;
                    // ActualAmount is expressed in BaseCurrency; convert it to USDT using the locked USDT rate.
                    var usdtRate = GetRate("USDT");
                    if (usdtRate <= 0) usdtRate = await _rateRepository.Where(x => x.Currency == "USDT" && x.FiatCurrency == BaseCurrency).FirstAsync(x => x.Rate);
                    if (usdtRate <= 0) throw new TokenPayException("USDT 汇率有误！");
                    order.OrderValueUsdt = model.ActualAmount / usdtRate;
                    var coinPriceUsdt = Rate / usdtRate;
                    (order.AllowedUnderpayAmount, order.MinimumPaidAmount) = PaymentAmountCalculator.Calculate(
                        Amount, order.OrderValueUsdt, coinPriceUsdt, _staticOptions, GetDecimals(model.Currency, _configuration, _chains));
                }
            }
            catch (TokenPayException e)
            {
                return Json(new ReturnData
                {
                    Message = e.Message
                });
            }
            if (order.Amount <= 0)
            {
                return Json(new ReturnData
                {
                    Message = "此订单金额过低！"
                });
            }
            await _repository.InsertAsync(order);
            return Json(new ReturnData<string>
            {
                Success = true,
                Message = "创建订单成功！",
                Data = Host + Url.Action(nameof(Pay), new { Id = order.Id }),
                Info = ToPayDic(order)
            });
        }

        private SortedDictionary<string, object?> ToPayDic(TokenOrders order)
        {
            var BaseCurrency = _configuration.GetValue<string>("BaseCurrency", "CNY");
            var ExpireTime = _configuration.GetValue("ExpireTime", 10 * 60);
            var created = order.IsStaticAddress ? PaymentTime.ToUtc(order.CreateTime) : order.CreateTime;
            var autoExpire = order.IsStaticAddress ? created.AddMinutes(_staticOptions.AutoWindowMinutes) : created.AddSeconds(ExpireTime);
            var lateExpire = order.IsStaticAddress ? created.AddHours(_staticOptions.LatePaymentRetentionHours) : autoExpire;
            var dic = new SortedDictionary<string, object?>
            {
                { nameof(order.Id), order.Id.ToString() },
                { nameof(order.OutOrderId), order.OutOrderId },
                { nameof(order.OrderUserKey), order.OrderUserKey },
                { nameof(order.Amount), order.Amount.ToString() },
                { nameof(order.ActualAmount), order.ActualAmount.ToString() },
                { nameof(order.ToAddress), order.ToAddress },
                { nameof(order.PassThroughInfo), order.PassThroughInfo },
                { "BaseCurrency", BaseCurrency },
                { "BlockChainName", order.Currency.ToBlockchainEnglishName(_chains) },
                { "CurrencyName", order.Currency.ToCurrency(_chains) },
                { "ExpireTime", autoExpire.ToString("O")},
                { "AutoPaymentExpireTime", autoExpire.ToString("O")},
                { "LatePaymentRetentionTime", lateExpire.ToString("O")},
                { "QrCodeBase64", "data:image/png;base64," + Convert.ToBase64String(CreateQrCode(order.ToAddress))},
                { "QrCodeLink", Host + Url.Action(nameof(GetQrCode), new { Id = order.Id })},
            };
            return dic;
        }
        /// <summary>
        /// 获取订单对应的二维码
        /// 尺寸 300x300
        /// </summary>
        /// <returns></returns>
        public async Task<IActionResult> GetQrCode(Guid Id, int Size = 300)
        {
            var order = await _repository.Where(x => x.Id == Id).FirstAsync();
            if (order == null)
            {
                return File(new byte[0], "image/png");
            }
            return File(CreateQrCode(order.ToAddress, Size), "image/png");
        }
        private string Host
        {
            get
            {
                var host = _configuration.GetValue<string>("WebSiteUrl");
                if (string.IsNullOrEmpty(host))
                {
                    host = $"{Request.Scheme}://{Request.Host}";
                }
                return host;
            }
        }
        /// <summary>
        /// 动态地址
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        private async Task<(string, decimal)> GetUseTokenDynamicAdress(CreateOrderViewModel model)
        {
            var (UseTokenAdress, _) = await CreateAddress(model.OrderUserKey, model.Currency);
            var rate = GetRate(model.Currency);
            if (rate <= 0)
            {
                var Currency = model.Currency.ToCurrency(_chains);
                rate = await _rateRepository.Where(x => x.Currency == Currency && x.FiatCurrency == BaseCurrency).FirstAsync(x => x.Rate);
            }
            if (rate <= 0)
            {
                throw new TokenPayException("汇率有误！");
            }
            var Amount = (model.ActualAmount / rate).ToRound(GetDecimals(model.Currency,_configuration)); //因为每个用户一个独立支付地址，所以此处金额计算逻辑与静态地址不同
            return (UseTokenAdress, Amount);
        }
        /// <summary>
        /// 根据唯一Id获取一个地址
        /// </summary>
        /// <exception cref="TokenPayException"></exception>
        private async Task<(string, string)> CreateAddress(string OrderUserKey, string currency)
        {
            if (string.IsNullOrEmpty(OrderUserKey))
            {
                throw new TokenPayException("动态地址需传递用户标识！");
            }
            var BaseCurrency = TokenCurrency.TRX;
            // 币种以EVM开头判定为EVM
            if (currency.StartsWith("EVM"))
            {
                BaseCurrency = TokenCurrency.EVM;
            }
            var TokenId = $"{BaseCurrency}_{OrderUserKey}";
            var token = await _tokenRepository.Where(x => x.Id == TokenId && x.Currency == BaseCurrency).FirstAsync();
            if (token == null)
            {
                var ecKey = Nethereum.Signer.EthECKey.GenerateKey();
                var rawPrivateKey = ecKey.GetPrivateKeyAsBytes();
                var hex = Convert.ToHexString(rawPrivateKey);
                if (BaseCurrency == TokenCurrency.EVM)
                {
                    var Address = ecKey.GetPublicAddress();
                    token = new Tokens
                    {
                        Id = TokenId,
                        Address = Address,
                        Key = hex,
                        Currency = TokenCurrency.EVM
                    };
                    await _tokenRepository.InsertAsync(token);
                }
                else
                {
                    var tronWallet = new TronWallet(hex);
                    var Address = tronWallet.Address;
                    token = new Tokens
                    {
                        Id = TokenId,
                        Address = Address,
                        Key = hex,
                        Currency = TokenCurrency.TRX
                    };
                    await _tokenRepository.InsertAsync(token);
                }
            }
            return (token.Address, token.Key);
        }
        /// <summary>
        /// 静态地址
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        /// <exception cref="TokenPayException"></exception>
        private async Task<(string, decimal, decimal)> GetUseTokenStaticAdress(CreateOrderViewModel model)
        {
            var TRON = _configuration.GetSection("Address:TRON").Get<string[]>() ?? new string[0];
            var EVM = _configuration.GetSection("Address:EVM").Get<string[]>() ?? new string[0];
            var CurrencyAddress = _configuration.GetSection($"Address:{model.Currency.Replace("EVM", "").Split("_", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).First()}").Get<string[]>() ?? new string[0];

            var CurrentAdress = CurrencyAddress;

            if (CurrentAdress.Length == 0 && (model.Currency == "TRX" || model.Currency.EndsWith("TRC20")))
            {
                CurrentAdress = TRON;
            }
            if (CurrentAdress.Length == 0 && model.Currency.StartsWith("EVM"))
            {
                CurrentAdress = EVM;
            }
            if (CurrentAdress.Length == 0)
            {
                throw new TokenPayException("未配置收款地址！");
            }
            var rate = GetRate(model.Currency);
            if (rate <= 0)
            {
                var Currency = model.Currency.ToCurrency(_chains);
                rate = await _rateRepository.Where(x => x.Currency == Currency && x.FiatCurrency == BaseCurrency).FirstAsync(x => x.Rate);
            }
            if (rate <= 0)
            {
                throw new TokenPayException("汇率有误！");
            }
            var Amount = (model.ActualAmount / rate).ToRound(GetDecimals(model.Currency, _configuration, _chains));
            // Static matching is based on address/network/time uniqueness, never an amount fingerprint.
            var UseTokenAdress = CurrentAdress[Random.Shared.Next(CurrentAdress.Length)];
            return (UseTokenAdress, Amount, rate);
        }

        [Route("/CheckTron/{address}")]
        [Route("/{action}/{address}")]
        public async Task<IActionResult> CheckTronAddress(string address)
        {
            var item = await _tokenRepository.Where(x => x.Address == address && x.Currency == TokenCurrency.TRX).FirstAsync();
            if (item == null)
            {
                _logger.LogWarning("检查的地址[{address}]不存在！", address);
                return Content("ok");
            }
            item.Value = await QueryTronAction.GetTRXAsync(address);
            item.USDT = await QueryTronAction.GetUsdtAmountAsync(address);
            await _tokenRepository.UpdateAsync(item);
            return Content("ok");
        }
        [Route("/error-development")]
        public IActionResult HandleErrorDevelopment([FromServices] IHostEnvironment hostEnvironment)
        {
            var exceptionHandlerFeature =
                HttpContext.Features.Get<IExceptionHandlerFeature>()!;
            var e = exceptionHandlerFeature.Error;

            if (!hostEnvironment.IsDevelopment())
            {
                return Json(new ReturnData
                {
                    Message = e.Message
                });
            }

            return Json(new ReturnData<object>
            {
                Message = e.Message,
                Data = new
                {
                    title = exceptionHandlerFeature.Error.Message,
                    detail = exceptionHandlerFeature.Error.StackTrace,
                }
            });
        }

        [Route("/error")]
        public IActionResult HandleError() => Problem();
        /// <summary>
        /// 创建二维码
        /// </summary>
        /// <returns></returns>
        private static byte[] CreateQrCode(string qrcode, int size = 300)
        {
            using var stream = new MemoryStream();
            var qrCode = new QrCode(qrcode, new Vector2Slim(size, size), SKEncodedImageFormat.Png);
            qrCode.GenerateImage(stream);
            return stream.ToArray();
        }

        [HttpPost]
        [Route("/payment/{id:guid}/recheck")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("payment-write")]
        [RequestSizeLimit(1024)]
        public async Task<IActionResult> Recheck(Guid id, CancellationToken cancellationToken)
        {
            await _staticMatcher.ReportPaymentAsync(id, cancellationToken);
            var order = await _repository.Where(x => x.Id == id).FirstAsync();
            return Json(new { status = order == null ? "NotFound" :
                order.Status == OrderStatus.Pending ? order.PaymentMatchStatus.ToString() : order.Status.ToString(),
                reason = order?.PaymentMatchReason });
        }

        [HttpPost]
        [Route("/payment/{id:guid}/txid")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("payment-write")]
        [RequestSizeLimit(1024)]
        public async Task<IActionResult> SubmitTxId(Guid id, [FromBody] TxIdClaim model, CancellationToken cancellationToken)
        {
            var result = await _staticMatcher.ClaimByTxIdAsync(id, model.TransactionHash, model.TransferKey,
                HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
            return Json(new { status = result.Status.ToString(), result.Reason });
        }

        public sealed record TxIdClaim(string TransactionHash, string? TransferKey);
    }
}
