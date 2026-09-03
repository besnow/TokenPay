using FreeSql.DataAnnotations;

namespace TokenPay.Domains;

[Index("uk_chain_payment_key", "Network,TransactionHash,TransferKey", true)]
public class ChainPayment
{
    public Guid Id { get; set; }
    public required string Network { get; set; }
    public required string Asset { get; set; }
    public string? TokenContract { get; set; }
    public required string TransactionHash { get; set; }
    public int TransferIndex { get; set; }
    public required string TransferKey { get; set; }
    public string? FromAddress { get; set; }
    public required string ToAddress { get; set; }
    [Column(Precision = 38, Scale = 18)]
    public decimal ActualAmount { get; set; }
    public long BlockNumber { get; set; }
    public DateTime BlockTime { get; set; }
    public int Confirmations { get; set; }
    public DateTime FirstSeenTime { get; set; } = DateTime.UtcNow;
    public PaymentMatchStatus MatchStatus { get; set; }
    public Guid? MatchedOrderId { get; set; }
    public PaymentMatchMethod? MatchMethod { get; set; }
    [Column(StringLength = -1)]
    public string? MatchReason { get; set; }
}

public enum PaymentMatchStatus { Waiting, Unmatched, Matched, Ambiguous, AmountInsufficient, TxIdSubmitted, ManualReview, Expired }
public enum PaymentMatchMethod { TimeUnique, TxIdClaim, Manual }
