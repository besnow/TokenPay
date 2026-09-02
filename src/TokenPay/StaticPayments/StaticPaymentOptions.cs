namespace TokenPay.StaticPayments;

public sealed class StaticPaymentOptions
{
    public bool Enabled { get; set; } = true;
    public int AutoWindowMinutes { get; set; } = 60;
    public int LatePaymentRetentionHours { get; set; } = 24;
    public decimal MaxUnderpayUsd { get; set; } = 2m;
    public decimal MaxUnderpayPercent { get; set; } = 0.10m;
    public bool AcceptOverpay { get; set; } = true;
    public bool CreditOverpay { get; set; }
    public string AmbiguousMatchAction { get; set; } = "RequireTxId";
}
