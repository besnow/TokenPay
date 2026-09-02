using System.Threading.Channels;
using FreeSql;
using Microsoft.Extensions.Options;
using TokenPay.Domains;
using TokenPay.Models;

namespace TokenPay.Services;

public sealed record ObservedTransfer(
    string Network, string Asset, string? TokenContract, string TransactionHash,
    int TransferIndex, string? FromAddress, string ToAddress, decimal Amount,
    long BlockNumber, DateTime BlockTimeUtc, int Confirmations);

public sealed record MatchResult(PaymentMatchStatus Status, Guid? OrderId = null, string? Reason = null);

public interface IStaticPaymentMatcher
{
    Task<MatchResult> ObserveAsync(ObservedTransfer transfer, CancellationToken cancellationToken = default);
    Task<MatchResult> ClaimByTxIdAsync(Guid orderId, string transactionHash, int? transferIndex = null, CancellationToken cancellationToken = default);
    Task RetryUnmatchedAsync(CancellationToken cancellationToken = default);
}

/// <summary>静态地址交易的唯一候选匹配器。金额只作门槛，绝不作订单指纹。</summary>
public sealed class StaticPaymentMatcher : IStaticPaymentMatcher
{
    private readonly IFreeSql _db;
    private readonly StaticPaymentMatchOptions _options;
    private readonly Channel<TokenOrders> _paidOrders;
    private readonly ILogger<StaticPaymentMatcher> _logger;

    public StaticPaymentMatcher(IFreeSql db, IOptions<StaticPaymentMatchOptions> options,
        Channel<TokenOrders> paidOrders, ILogger<StaticPaymentMatcher> logger)
    {
        _db = db;
        _options = options.Value;
        _paidOrders = paidOrders;
        _logger = logger;
    }

    public async Task<MatchResult> ObserveAsync(ObservedTransfer transfer, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return new(PaymentMatchStatus.Unmatched, Reason: "static matching disabled");
        var payment = new ChainPayment
        {
            Network = transfer.Network, Asset = transfer.Asset, TokenContract = transfer.TokenContract,
            TransactionHash = transfer.TransactionHash, TransferIndex = transfer.TransferIndex,
            FromAddress = transfer.FromAddress, ToAddress = transfer.ToAddress, ActualAmount = transfer.Amount,
            BlockNumber = transfer.BlockNumber, BlockTime = EnsureUtc(transfer.BlockTimeUtc),
            Confirmations = transfer.Confirmations, MatchStatus = PaymentMatchStatus.Unmatched
        };
        try { await _db.Insert(payment).ExecuteAffrowsAsync(cancellationToken); }
        catch { payment = await _db.Select<ChainPayment>().Where(x => x.Network == transfer.Network && x.TransactionHash == transfer.TransactionHash && x.TransferIndex == transfer.TransferIndex).FirstAsync(cancellationToken); }
        return await MatchExistingAsync(payment, null, PaymentMatchMethod.TimeUnique, cancellationToken);
    }

    public async Task<MatchResult> ClaimByTxIdAsync(Guid orderId, string transactionHash, int? transferIndex = null, CancellationToken cancellationToken = default)
    {
        var transfers = await _db.Select<ChainPayment>().Where(x => x.TransactionHash == transactionHash)
            .WhereIf(transferIndex.HasValue, x => x.TransferIndex == transferIndex).ToListAsync(cancellationToken);
        if (transfers.Count == 0) return new(PaymentMatchStatus.Unmatched, Reason: "交易尚未由已确认的链上扫描发现");
        if (transfers.Count > 1 && !transferIndex.HasValue) return new(PaymentMatchStatus.Ambiguous, Reason: "该交易包含多笔转账，请指定日志序号");
        return await MatchExistingAsync(transfers.Single(), orderId, PaymentMatchMethod.TxIdClaim, cancellationToken);
    }

    public async Task RetryUnmatchedAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.AddHours(-_options.LatePaymentRetentionHours);
        var payments = await _db.Select<ChainPayment>().Where(x => x.MatchStatus != PaymentMatchStatus.Matched && x.FirstSeenTime >= cutoff).ToListAsync(cancellationToken);
        foreach (var payment in payments) await MatchExistingAsync(payment, null, PaymentMatchMethod.TimeUnique, cancellationToken);
        await _db.Update<ChainPayment>().Set(x => x.MatchStatus, PaymentMatchStatus.Expired)
            .Set(x => x.MatchReason, "retention window expired")
            .Where(x => x.MatchStatus != PaymentMatchStatus.Matched && x.FirstSeenTime < cutoff).ExecuteAffrowsAsync(cancellationToken);
    }

    private async Task<MatchResult> MatchExistingAsync(ChainPayment payment, Guid? claimedOrderId, PaymentMatchMethod method, CancellationToken ct)
    {
        if (payment.MatchStatus == PaymentMatchStatus.Matched) return new(payment.MatchStatus, payment.MatchedOrderId);
        var blockTime = EnsureUtc(payment.BlockTime);
        var earliest = blockTime.AddHours(-_options.LatePaymentRetentionHours);
        var latestCreation = blockTime.AddSeconds(_options.BlockTimeSkewSeconds);
        var query = _db.Select<TokenOrders>()
            .Where(x => x.IsStaticAddress && x.Status == OrderStatus.Pending)
            .Where(x => x.Currency == payment.Asset && x.ToAddress == payment.ToAddress)
            .Where(x => x.CreateTime >= earliest && x.CreateTime <= latestCreation);
        if (claimedOrderId.HasValue) query = query.Where(x => x.Id == claimedOrderId.Value);
        var temporal = await query.ToListAsync(ct);
        var candidates = temporal.Where(x => payment.ActualAmount >= x.MinimumPaidAmount).ToList();
        if (candidates.Count == 0)
        {
            var expiredQuery = _db.Select<TokenOrders>()
                .Where(x => x.IsStaticAddress && x.Status == OrderStatus.Pending)
                .Where(x => x.Currency == payment.Asset && x.ToAddress == payment.ToAddress && x.CreateTime < earliest);
            if (claimedOrderId.HasValue) expiredQuery = expiredQuery.Where(x => x.Id == claimedOrderId.Value);
            var hasExpiredCandidate = temporal.Count == 0 && await expiredQuery.AnyAsync(ct);
            var status = temporal.Count > 0 ? PaymentMatchStatus.AmountInsufficient :
                hasExpiredCandidate ? PaymentMatchStatus.Expired : PaymentMatchStatus.Unmatched;
            var reason = status switch
            {
                PaymentMatchStatus.AmountInsufficient => "amount below minimum_paid_amount",
                PaymentMatchStatus.Expired => "order payment window expired",
                _ => "no eligible order"
            };
            return await MarkAsync(payment.Id, status, reason, ct);
        }
        if (candidates.Count > 1 && !claimedOrderId.HasValue)
            return await MarkAsync(payment.Id, PaymentMatchStatus.Ambiguous, "multiple eligible orders; TxID required", ct);

        var order = candidates.Single();
        TokenOrders? completed = null;
        _db.Transaction(() =>
        {
            var affected = _db.Update<TokenOrders>()
                .Set(x => x.Status, OrderStatus.Paid).Set(x => x.ChainPaymentId, payment.Id)
                .Set(x => x.BlockTransactionId, payment.TransactionHash).Set(x => x.PayAmount, payment.ActualAmount)
                .Set(x => x.PayTime, blockTime).Set(x => x.FromAddress, payment.FromAddress)
                .Set(x => x.IsLatePayment, blockTime > EnsureUtc(order.CreateTime).AddMinutes(_options.AutoWindowMinutes))
                .Where(x => x.Id == order.Id && x.Status == OrderStatus.Pending && x.ChainPaymentId == null).ExecuteAffrows();
            if (affected != 1) return;
            var paymentAffected = _db.Update<ChainPayment>()
                .Set(x => x.MatchStatus, PaymentMatchStatus.Matched).Set(x => x.MatchedOrderId, order.Id)
                .Set(x => x.MatchMethod, method).Set(x => x.MatchReason, (string?)null)
                .Where(x => x.Id == payment.Id && x.MatchedOrderId == null).ExecuteAffrows();
            if (paymentAffected != 1) throw new InvalidOperationException("transfer was concurrently claimed");
            completed = _db.Select<TokenOrders>().Where(x => x.Id == order.Id).First();
        });
        if (completed == null) return new(PaymentMatchStatus.Unmatched, Reason: "concurrent match lost");
        _logger.LogInformation("静态到账 {Network}/{TransactionHash}/{TransferIndex} 唯一匹配订单 {OrderId}", payment.Network, payment.TransactionHash, payment.TransferIndex, order.Id);
        await _paidOrders.Writer.WriteAsync(completed, ct);
        return new(PaymentMatchStatus.Matched, order.Id);
    }

    private async Task<MatchResult> MarkAsync(Guid id, PaymentMatchStatus status, string reason, CancellationToken ct)
    {
        await _db.Update<ChainPayment>().Set(x => x.MatchStatus, status).Set(x => x.MatchReason, reason)
            .Where(x => x.Id == id && x.MatchedOrderId == null).ExecuteAffrowsAsync(ct);
        return new(status, Reason: reason);
    }

    private static DateTime EnsureUtc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
