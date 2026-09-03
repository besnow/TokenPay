using FreeSql;
using TokenPay.Domains;

namespace TokenPay.Services;

public sealed class ChainScanCursorStore(IFreeSql db)
{
    public async Task<ChainScanCursor> GetAsync(string network, string asset, string address, int retentionHours, CancellationToken ct)
        => await db.Select<ChainScanCursor>().Where(x => x.Network == network && x.Asset == asset && x.Address == address).FirstAsync(ct)
           ?? new ChainScanCursor { Network = network, Asset = asset, Address = address,
               LastBlockTimeUtc = DateTime.UtcNow.AddHours(-retentionHours) };

    public async Task AdvanceAsync(ChainScanCursor cursor, long block, DateTime blockTimeUtc, string? continuation, CancellationToken ct)
    {
        cursor.LastBlockNumber = Math.Max(cursor.LastBlockNumber, block);
        cursor.LastBlockTimeUtc = blockTimeUtc > cursor.LastBlockTimeUtc ? blockTimeUtc : cursor.LastBlockTimeUtc;
        cursor.ContinuationToken = continuation;
        cursor.UpdatedAtUtc = DateTime.UtcNow;
        await db.InsertOrUpdate<ChainScanCursor>().SetSource(cursor).IfExistsDoNothing().ExecuteAffrowsAsync(ct);
        await db.Update<ChainScanCursor>().SetSource(cursor)
            .Where(x => x.Network == cursor.Network && x.Asset == cursor.Asset && x.Address == cursor.Address).ExecuteAffrowsAsync(ct);
    }
}
