using TokenPay.Domains;

namespace TokenPay.Services;

/// <summary>Network-specific, injectable lookup used when a submitted TxID was not observed by a scanner.</summary>
public interface IChainTransactionResolver
{
    bool CanResolve(TokenOrders order);
    Task<IReadOnlyList<ObservedTransfer>> ResolveAsync(TokenOrders order, string transactionHash, CancellationToken cancellationToken);
}
