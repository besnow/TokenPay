using FreeSql;
using TokenPay.Domains;

namespace TokenPay.Services;

public static class TransactionHashMigration
{
    public static void Run(IFreeSql db)
    {
        db.Transaction(() =>
        {
            var payments = db.Select<ChainPayment>().ToList();
            foreach (var group in payments.GroupBy(x => new
                     { x.Network, Hash = ChainTransactionHash.Normalize(x.Network, x.TransactionHash), x.TransferKey }))
            {
                var orders = group.Where(x => x.MatchedOrderId.HasValue).Select(x => x.MatchedOrderId!.Value).Distinct().ToArray();
                if (orders.Length > 1)
                    throw new InvalidOperationException($"Canonical transaction conflict for {group.Key.Network}/{group.Key.Hash}/{group.Key.TransferKey}; orders: {string.Join(",", orders)}");
                var keeper = group.OrderByDescending(x => x.MatchedOrderId.HasValue).ThenBy(x => x.FirstSeenTime).First();
                foreach (var duplicate in group.Where(x => x.Id != keeper.Id))
                {
                    db.Update<PaymentClaim>().Set(x => x.ChainPaymentId, keeper.Id).Where(x => x.ChainPaymentId == duplicate.Id).ExecuteAffrows();
                    db.Update<TokenOrders>().Set(x => x.ChainPaymentId, keeper.Id).Where(x => x.ChainPaymentId == duplicate.Id).ExecuteAffrows();
                    db.Delete<ChainPayment>(duplicate.Id).ExecuteAffrows();
                }
                if (keeper.TransactionHash != group.Key.Hash)
                    db.Update<ChainPayment>().Set(x => x.TransactionHash, group.Key.Hash).Where(x => x.Id == keeper.Id).ExecuteAffrows();
            }
            var claims = db.Select<PaymentClaim>().ToList();
            foreach (var group in claims.GroupBy(x => new { x.OrderId, x.Network,
                         Hash = ChainTransactionHash.Normalize(x.Network, x.TransactionHash), x.TransferKey }))
            {
                var keeper = group.OrderByDescending(x => x.ClaimStatus == PaymentClaimStatus.Matched).ThenBy(x => x.SubmittedAtUtc).First();
                foreach (var duplicate in group.Where(x => x.Id != keeper.Id)) db.Delete<PaymentClaim>(duplicate.Id).ExecuteAffrows();
                if (keeper.TransactionHash != group.Key.Hash)
                    db.Update<PaymentClaim>().Set(x => x.TransactionHash, group.Key.Hash).Where(x => x.Id == keeper.Id).ExecuteAffrows();
            }
            foreach (var order in db.Select<TokenOrders>().Where(x => x.BlockTransactionId != null).ToList())
            {
                var network = order.Currency.StartsWith("EVM_", StringComparison.Ordinal) ? order.Currency.Split('_')[1] : "TRON";
                var canonical = ChainTransactionHash.Normalize(network, order.BlockTransactionId!);
                if (order.BlockTransactionId != canonical)
                    db.Update<TokenOrders>().Set(x => x.BlockTransactionId, canonical).Where(x => x.Id == order.Id).ExecuteAffrows();
            }
        });
    }
}
