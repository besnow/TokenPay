using FreeSql.DataAnnotations;

namespace TokenPay.Domains;

[Index("uk_payment_claim", "OrderId,Network,TransactionHash,TransferKey", true)]
public class PaymentClaim
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid? ChainPaymentId { get; set; }
    public required string Network { get; set; }
    public required string Asset { get; set; }
    public required string TransactionHash { get; set; }
    public required string TransferKey { get; set; }
    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;
    public string? ClientIp { get; set; }
    public PaymentClaimStatus ClaimStatus { get; set; }
    public string? RejectReason { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public enum PaymentClaimStatus { Submitted, Matched, Rejected, AlreadyUsed, InvalidTransaction, AmountMismatch }
