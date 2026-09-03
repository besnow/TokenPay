using FreeSql;
using TokenPay.Domains;
using TokenPay.Services;

namespace TokenPay.Tests;

public class ChainScanCursorStoreTests
{
    [Fact]
    public async Task Cursor_survives_restart_and_never_moves_backwards()
    {
        var file = Path.Combine(Path.GetTempPath(), $"cursor-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = NewDb(file))
            {
                var store = new ChainScanCursorStore(db);
                var cursor = await store.GetAsync("TRON", "TRX", "TAddress", 24, default);
                Assert.InRange(cursor.LastBlockTimeUtc, DateTime.UtcNow.AddHours(-24).AddMinutes(-1), DateTime.UtcNow.AddHours(-24).AddMinutes(1));
                await store.AdvanceAsync(cursor, 100, DateTime.UtcNow.AddHours(-2), null, default);
            }
            using (var db = NewDb(file))
            {
                var restored = await new ChainScanCursorStore(db).GetAsync("TRON", "TRX", "TAddress", 24, default);
                Assert.Equal(100, restored.LastBlockNumber);
                Assert.True(restored.LastBlockTimeUtc > DateTime.UtcNow.AddHours(-3));
            }
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    private static IFreeSql NewDb(string file)
    {
        var db = new FreeSqlBuilder().UseConnectionString(DataType.Sqlite, $"Data Source={file}").UseAutoSyncStructure(true).Build();
        db.CodeFirst.SyncStructure<ChainScanCursor>();
        return db;
    }
}
