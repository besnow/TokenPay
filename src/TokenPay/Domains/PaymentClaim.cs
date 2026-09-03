using FreeSql.DataAnnotations;

namespace TokenPay.Domains;

[Index("uk_payment_claim", "OrderId,Network,TransactionHash", true)]
public class PaymentClaim
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid? ChainPaymentId { get; set; }
    public required string Network { get; set; }
    public required string TransactionHash { get; set; }
    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;
    public string? ClientIp { get; set; }
    public PaymentClaimReviewStatus ReviewStatus { get; set; }
    public string? ReviewReason { get; set; }
    public string? EligibleOrderIds { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public string? ReviewedBy { get; set; }
}

public enum PaymentClaimReviewStatus { Submitted, AutoMatched, ManualReview, Approved, Rejected }
