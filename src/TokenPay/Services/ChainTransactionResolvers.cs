using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using TokenPay.Domains;
using TokenPay.Extensions;
using TokenPay.Models.EthModel;

namespace TokenPay.Services;

/// <summary>Authoritative TRON TxID lookup.  Account-list scan responses are never trusted as receipts.</summary>
public sealed class TronChainTransactionResolver(IHttpClientFactory clients, IConfiguration configuration,
    IHostEnvironment environment) : IChainTransactionResolver
{
    public bool CanResolve(TokenOrders order) => order.Currency is "TRX" or "USDT_TRC20";

    public async Task<IReadOnlyList<ObservedTransfer>> ResolveAsync(TokenOrders order, string transactionHash, CancellationToken ct)
    {
        transactionHash = ChainTransactionHash.Normalize("TRON", transactionHash);
        var host = environment.IsProduction() ? configuration.GetValue("TronApiHost", "https://api.trongrid.io")! : "https://api.shasta.trongrid.io";
        var client = clients.CreateClient();
        if (environment.IsProduction()) client.DefaultRequestHeaders.TryAddWithoutValidation("TRON-PRO-API-KEY", configuration["TRON-PRO-API-KEY"]);
        var tx = await Post(client, $"{host}/wallet/gettransactionbyid", transactionHash, ct);
        var info = await Post(client, $"{host}/wallet/gettransactioninfobyid", transactionHash, ct);
        if (tx == null || info == null || !ReceiptSucceeded(tx, info)) return [];
        var block = info.Value<long?>("blockNumber") ?? 0;
        var timestamp = info.Value<long?>("blockTimeStamp") ?? tx["raw_data"]?.Value<long?>("timestamp") ?? 0;
        var current = await GetCurrentBlock(client, host, ct);
        var confirmations = block > 0 && current >= block ? checked((int)Math.Min(int.MaxValue, current - block + 1)) : 0;
        var required = configuration.GetValue("TronConfirmations", configuration.GetValue("OnlyConfirmed", true) ? 1 : 0);
        if (confirmations < required) return [];

        if (order.Currency == "TRX")
        {
            var contract = tx["raw_data"]?["contract"]?.FirstOrDefault(x => x.Value<string>("type") == "TransferContract");
            var value = contract?["parameter"]?["value"];
            if (value == null) return [];
            var to = TronAddress(value.Value<string>("to_address"));
            if (!string.Equals(to, order.ToAddress, StringComparison.Ordinal)) return [];
            return [new("TRON", "TRX", null, transactionHash, "native", TronAddress(value.Value<string>("owner_address")),
                to!, (value.Value<decimal?>("amount") ?? 0) / 1_000_000m, block, UnixMilliseconds(timestamp), confirmations)];
        }

        var contractAddress = environment.IsProduction() ? "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t" : "TX8ZUpucJYgHb8wBFQYuYSJ459og32AHWW";
        using var response = await client.GetAsync($"{host}/v1/transactions/{transactionHash}/events", ct);
        if (!response.IsSuccessStatusCode) return [];
        var events = JObject.Parse(await response.Content.ReadAsStringAsync(ct))["data"] as JArray ?? [];
        var result = new List<ObservedTransfer>();
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Value<string>("event_name") != "Transfer" || !string.Equals(e.Value<string>("contract_address"), contractAddress, StringComparison.Ordinal)) continue;
            var to = e["result"]?.Value<string>("to");
            if (!AddressEquals(to, order.ToAddress)) continue;
            var amount = ParseInteger(e["result"]?.Value<string>("value")) / 1_000_000m;
            var index = e.Value<int?>("event_index");
            if (!index.HasValue) continue;
            result.Add(new("TRON", "USDT_TRC20", contractAddress, transactionHash, $"event:{index}",
                e["result"]?.Value<string>("from"), order.ToAddress, amount, block, UnixMilliseconds(timestamp), confirmations));
        }
        return result;
    }

    private static async Task<JObject?> Post(HttpClient client, string url, string txid, CancellationToken ct)
    {
        using var body = new StringContent(new JObject { ["value"] = txid }.ToString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(url, body, ct);
        return response.IsSuccessStatusCode ? JObject.Parse(await response.Content.ReadAsStringAsync(ct)) : null;
    }
    private static bool ReceiptSucceeded(JObject tx, JObject info) =>
        tx["ret"]?.FirstOrDefault()?.Value<string>("contractRet") == "SUCCESS" &&
        (info["receipt"]?.Value<string>("result") is null or "SUCCESS");
    private static async Task<long> GetCurrentBlock(HttpClient client, string host, CancellationToken ct)
    {
        using var response = await client.GetAsync($"{host}/wallet/getnowblock", ct);
        if (!response.IsSuccessStatusCode) return 0;
        return JObject.Parse(await response.Content.ReadAsStringAsync(ct))["block_header"]?["raw_data"]?.Value<long?>("number") ?? 0;
    }
    private static string? TronAddress(string? address) { if (string.IsNullOrWhiteSpace(address)) return null; try { return address.HexToeBase58(); } catch { return address; } }
    private static bool AddressEquals(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || string.Equals(TronAddress(a), b, StringComparison.Ordinal);
    private static DateTime UnixMilliseconds(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
    private static decimal ParseInteger(string? value) => decimal.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}

/// <summary>JSON-RPC resolver that validates chain id, receipt status and real receipt log indexes.</summary>
public sealed class EvmChainTransactionResolver(IHttpClientFactory clients, List<EVMChain> chains) : IChainTransactionResolver
{
    private const string TransferTopic = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";
    public bool CanResolve(TokenOrders order) => order.Currency.StartsWith("EVM_", StringComparison.Ordinal);

    public async Task<IReadOnlyList<ObservedTransfer>> ResolveAsync(TokenOrders order, string transactionHash, CancellationToken ct)
    {
        var parts = order.Currency.Split('_');
        var chain = chains.FirstOrDefault(x => x.Enable && x.ChainNameEN == parts.ElementAtOrDefault(1));
        if (chain == null) return [];
        if (string.IsNullOrWhiteSpace(chain.RpcHost)) throw new ChainQueryException($"{parts.ElementAtOrDefault(1)} chain RPC is not configured.");
        var assetIsNative = parts.Length == 3 && parts[2] == chain.BaseCoin;
        var token = assetIsNative ? null : chain.ERC20?.FirstOrDefault(x => parts.Contains(x.Name, StringComparer.Ordinal));
        return await ResolveTransfersAsync(chain, order.Currency, token, order.ToAddress, transactionHash, ct);
    }

    public async Task<IReadOnlyList<ObservedTransfer>> ResolveTransfersAsync(EVMChain chain, string asset, EVMErc20? token,
        string address, string transactionHash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chain.RpcHost)) throw new ChainQueryException($"{chain.ChainNameEN} chain RPC is not configured.");
        transactionHash = ChainTransactionHash.Normalize(chain.ChainNameEN, transactionHash);
        var client = clients.CreateClient();
        var tx = await Rpc(client, chain.RpcHost, "eth_getTransactionByHash", [transactionHash], ct);
        var receipt = await Rpc(client, chain.RpcHost, "eth_getTransactionReceipt", [transactionHash], ct);
        if (tx == null || receipt == null) return [];
        if (Hex(receipt.Value<string>("status")) != 1) return [];
        var chainId = await RpcValue(client, chain.RpcHost, "eth_chainId", [], ct);
        if (Hex(chainId) != chain.ChainId) return [];
        var blockNumber = Hex(receipt.Value<string>("blockNumber"));
        var head = Hex(await RpcValue(client, chain.RpcHost, "eth_blockNumber", [], ct));
        var confirmations = head >= blockNumber ? checked((int)Math.Min(int.MaxValue, head - blockNumber + 1)) : 0;
        if (confirmations < chain.Confirmations) return [];
        var block = await Rpc(client, chain.RpcHost, "eth_getBlockByNumber", [receipt.Value<string>("blockNumber")!, false], ct);
        var time = DateTimeOffset.FromUnixTimeSeconds(Hex(block?.Value<string>("timestamp"))).UtcDateTime;
        if (token == null)
        {
            var to = tx!.Value<string>("to");
            if (!AddressEquals(to, address)) return [];
            return [new(chain.ChainNameEN, asset, null, transactionHash, "native", tx.Value<string>("from"), to!,
                EvmValueConverter.ToDecimal(tx.Value<string>("value")!, chain.Decimals), blockNumber, time, confirmations)];
        }
        if (token?.Decimals == null) return [];
        var result = new List<ObservedTransfer>();
        foreach (var log in receipt["logs"] as JArray ?? [])
        {
            var topics = log["topics"] as JArray;
            if (!AddressEquals(log.Value<string>("address"), token.ContractAddress) || topics?.Count < 3 ||
                !string.Equals(topics![0]?.ToString(), TransferTopic, StringComparison.OrdinalIgnoreCase)) continue;
            if (topics[1]?.ToString().Length < 40 || topics[2]?.ToString().Length < 40 || string.IsNullOrWhiteSpace(log.Value<string>("logIndex")))
                continue;
            var to = "0x" + topics[2]!.ToString()[^40..];
            if (!AddressEquals(to, address)) continue;
            result.Add(new(chain.ChainNameEN, asset, token.ContractAddress, transactionHash,
                $"log:{Hex(log.Value<string>("logIndex"))}", "0x" + topics[1]!.ToString()[^40..], to,
                EvmValueConverter.ToDecimal(log.Value<string>("data")!, token.Decimals.Value), blockNumber, time, confirmations));
        }
        return result;
    }
    private static async Task<JObject?> Rpc(HttpClient client, string host, string method, object[] parameters, CancellationToken ct)
    {
        var payload = new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = JArray.FromObject(parameters) };
        using var response = await client.PostAsync(host, new StringContent(payload.ToString(), Encoding.UTF8, "application/json"), ct);
        if (!response.IsSuccessStatusCode) throw new ChainQueryException($"Chain query failed with HTTP {(int)response.StatusCode}.");
        var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
        if (json["error"] != null) throw new ChainQueryException("Chain JSON-RPC returned an error.");
        return json["result"] as JObject;
    }
    private static async Task<string?> RpcValue(HttpClient c, string h, string m, object[] p, CancellationToken ct)
    {
        var payload = new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = m, ["params"] = JArray.FromObject(p) };
        using var response = await c.PostAsync(h, new StringContent(payload.ToString(), Encoding.UTF8, "application/json"), ct);
        if (!response.IsSuccessStatusCode) throw new ChainQueryException($"Chain query failed with HTTP {(int)response.StatusCode}.");
        var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
        if (json["error"] != null) throw new ChainQueryException("Chain JSON-RPC returned an error.");
        return json["result"]?.ToString();
    }
    private static long Hex(string? value) => string.IsNullOrWhiteSpace(value) ? 0 : Convert.ToInt64(value.StartsWith("0x") ? value[2..] : value, 16);
    private static bool AddressEquals(string? a, string? b) => string.Equals(a?.TrimStart('0'), b?.TrimStart('0'), StringComparison.OrdinalIgnoreCase);
}
