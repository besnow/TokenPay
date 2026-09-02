using FreeSql.DataAnnotations;

namespace TokenPay.Domains;

/// <summary>A durable, network-qualified on-chain transfer.</summary>
[Index("UX_ChainPayment_Transfer", "Network,TransactionHash,TransferIndex", true)]
public sealed class ChainPayment
{
    public Guid Id { get; set; }
    public required string Network { get; set; }
    public required string Currency { get; set; }
    public string? TokenContract { get; set; }
    public required string TransactionHash { get; set; }
    public long TransferIndex { get; set; }
    public required string ToAddress { get; set; }
    [Column(Precision = 38, Scale = 18)] public decimal Amount { get; set; }
    public DateTime BlockTimeUtc { get; set; }
    public int Confirmations { get; set; }
    public bool Succeeded { get; set; }
    public Guid? MatchedOrderId { get; set; }
    public PaymentMatchStatus MatchStatus { get; set; } = PaymentMatchStatus.Unmatched;
    public DateTime DiscoveredAtUtc { get; set; } = DateTime.UtcNow;
}
