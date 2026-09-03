using TokenPay.Models.EthModel;

namespace TokenPay.Services;

public static class EvmConfigurationValidator
{
    public static void ValidateRpcHosts(IEnumerable<EVMChain> chains, bool useDynamicAddress)
    {
        if (useDynamicAddress) return;
        foreach (var chain in chains.Where(x => x.Enable))
            if (!Uri.TryCreate(chain.RpcHost, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException($"Enabled EVM chain {chain.ChainNameEN} requires a valid HTTP/HTTPS RpcHost in static-address mode.");
    }
}
