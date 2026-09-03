using FreeSql.DataAnnotations;

namespace TokenPay.Domains;

[Index("uk_chain_scan_cursor", "Network,Asset,Address", true)]
public class ChainScanCursor
{
    public Guid Id { get; set; }
    public required string Network { get; set; }
    public required string Asset { get; set; }
    public required string Address { get; set; }
    public long LastBlockNumber { get; set; }
    public DateTime LastBlockTimeUtc { get; set; }
    public string? ContinuationToken { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
