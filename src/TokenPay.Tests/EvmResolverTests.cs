using System.Net;
using System.Text;
using Microsoft.Extensions.Http;
using Newtonsoft.Json.Linq;
using TokenPay.Domains;
using TokenPay.Models.EthModel;
using TokenPay.Services;

namespace TokenPay.Tests;

public class EvmResolverTests
{
    private const string Hash = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Address = "0x1111111111111111111111111111111111111111";
    private const string Contract = "0x2222222222222222222222222222222222222222";
    private const string Transfer = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";

    [Fact]
    public void Real_tokentx_shape_has_no_fabricated_receipt_fields()
    {
        var item = Newtonsoft.Json.JsonConvert.DeserializeObject<ERC20Transaction>(
            "{\"blockNumber\":\"100\",\"hash\":\"" + Hash + "\",\"from\":\"" + Address + "\",\"to\":\"" + Address + "\",\"contractAddress\":\"" + Contract + "\",\"value\":\"1000000\",\"tokenDecimal\":\"6\"}")!;
        Assert.Equal(0, item.TxreceiptStatus);
        Assert.Equal(0, item.LogIndex);
    }

    [Fact]
    public async Task Receipt_is_authoritative_and_real_log_indexes_are_distinct()
    {
        var resolver = Resolver("0x1", TwoLogs());
        var result = await resolver.ResolveAsync(Order(), Hash.ToUpperInvariant(), CancellationToken.None);
        Assert.Equal(new[] { "log:2", "log:10" }, result.Select(x => x.TransferKey));
        Assert.All(result, x => Assert.Equal(1m, x.Amount));
    }

    [Fact]
    public async Task Failed_receipt_cannot_resolve_a_payment()
    {
        var resolver = Resolver("0x0", TwoLogs());
        Assert.Empty(await resolver.ResolveAsync(Order(), Hash, CancellationToken.None));
    }

    private static EvmChainTransactionResolver Resolver(string status, JArray logs)
    {
        var handler = new RpcHandler(method => method switch
        {
            "eth_getTransactionByHash" => new JObject { ["from"] = Address, ["to"] = Address, ["value"] = "0x0" },
            "eth_getTransactionReceipt" => new JObject { ["status"] = status, ["blockNumber"] = "0x64", ["logs"] = logs },
            "eth_chainId" => "0x1",
            "eth_blockNumber" => "0x70",
            "eth_getBlockByNumber" => new JObject { ["timestamp"] = "0x65000000" },
            _ => null
        });
        return new EvmChainTransactionResolver(new Factory(handler), [new EVMChain
        {
            Enable = true, ChainNameEN = "ETH", ChainName = "Ethereum", BaseCoin = "ETH", ChainId = 1,
            Decimals = 18, Confirmations = 1, RpcHost = "https://rpc.invalid", ERC20Name = "ERC20",
            ERC20 = [new EVMErc20 { Name = "USDT", ContractAddress = Contract, Decimals = 6 }]
        }]);
    }
    private static TokenOrders Order() => new() { OutOrderId = "o", OrderUserKey = "u", Currency = "EVM_ETH_USDT_ERC20",
        ToAddress = Address, Status = OrderStatus.Pending, IsStaticAddress = true };
    private static JArray TwoLogs() => new(
        Log("0x2"), Log("0xa"));
    private static JObject Log(string index) => new() { ["address"] = Contract, ["logIndex"] = index, ["data"] = "0xf4240",
        ["topics"] = new JArray(Transfer, "0x" + new string('0', 24) + "3333333333333333333333333333333333333333",
            "0x" + new string('0', 24) + Address[2..]) };

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }
    private sealed class RpcHandler(Func<string, JToken?> result) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JObject.Parse(await request.Content!.ReadAsStringAsync(ct));
            var value = result(body.Value<string>("method")!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = value }.ToString(), Encoding.UTF8, "application/json") };
        }
    }
}
