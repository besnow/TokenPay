namespace TokenPay.Models;

public sealed class StaticPaymentMatchOptions
{
    public const string SectionName = "StaticPaymentMatch";
    public bool Enabled { get; set; } = true;
    public int AutoWindowMinutes { get; set; } = 60;
    public int LatePaymentRetentionHours { get; set; } = 24;
    public decimal MaxUnderpayUsd { get; set; } = 2m;
    public decimal MaxUnderpayPercent { get; set; } = 0.10m;
    public bool AcceptOverpay { get; set; } = true;
    public bool CreditOverpay { get; set; } = false;
    public string AmbiguousMatchAction { get; set; } = "RequireTxId";
    public int BlockTimeSkewSeconds { get; set; } = 30;
}
