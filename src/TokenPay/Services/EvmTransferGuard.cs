using TokenPay.Models.EthModel;

namespace TokenPay.Services;

/// <summary>Single ingress guard shared by EVM scanners; never substitutes configured addresses for chain data.</summary>
public static class EvmTransferGuard
{
    public static async Task<bool> ObserveNativeAsync(IStaticPaymentMatcher matcher, EVMChain chain, string asset,
        string address, EthTransaction item, bool internalTransfer, long latestBlock, CancellationToken cancellationToken)
    {
        var confirmations = internalTransfer ? Math.Max(0, latestBlock - item.BlockNumber) : item.Confirmations;
        if (!string.Equals(item.To, address, StringComparison.OrdinalIgnoreCase) || item.IsError != 0
            || (!internalTransfer && item.TxreceiptStatus == 0) || confirmations < chain.Confirmations
            || !string.IsNullOrEmpty(item.ContractAddress)) return false;
        var key = internalTransfer ? $"trace:{item.TraceId ?? item.TransactionIndex}" : "native";
        await matcher.ObserveAsync(new(chain.ChainNameEN, asset, null, item.Hash, key, item.From, item.To,
            item.RealAmount(chain.Decimals), item.BlockNumber, item.DateTime, (int)confirmations), cancellationToken);
        return true;
    }

    public static async Task<bool> ObserveTokenAsync(IStaticPaymentMatcher matcher, EVMChain chain, EVMErc20 token,
        string asset, string address, ERC20Transaction item, CancellationToken cancellationToken)
    {
        if (!string.Equals(item.To, address, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(item.ContractAddress, token.ContractAddress, StringComparison.OrdinalIgnoreCase)
            || item.IsError != 0 || item.TxreceiptStatus == 0 || item.Confirmations < chain.Confirmations
            || item.TokenDecimal != token.Decimals) return false;
        await matcher.ObserveAsync(new(chain.ChainNameEN, asset, token.ContractAddress, item.Hash, $"log:{item.LogIndex}",
            item.From, item.To, item.RealAmount, long.TryParse(item.BlockNumber, out var block) ? block : 0,
            item.DateTime, (int)item.Confirmations), cancellationToken);
        return true;
    }
}
