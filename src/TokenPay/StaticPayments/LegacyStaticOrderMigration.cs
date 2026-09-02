using TokenPay.Domains;

namespace TokenPay.StaticPayments;

public static class LegacyStaticOrderMigration
{
    public static int UpgradePendingOrders(IEnumerable<TokenOrders> orders, bool useDynamicAddress)
    {
        if (useDynamicAddress) return 0;
        var changed = 0;
        foreach (var order in orders.Where(x => x.Status == OrderStatus.Pending && !x.IsStaticAddress))
        {
            order.IsStaticAddress = true;
            order.ExpectedAmount = order.ExpectedAmount > 0m ? order.ExpectedAmount : order.Amount;
            order.MinimumPaidAmount = order.MinimumPaidAmount > 0m
                ? order.MinimumPaidAmount : order.ExpectedAmount;
            changed++;
        }
        return changed;
    }
}
