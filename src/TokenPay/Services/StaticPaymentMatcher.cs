using System.Threading.Channels;
using FreeSql;
using Microsoft.Extensions.Options;
using TokenPay.Domains;
using TokenPay.Helper;
using TokenPay.Models;

namespace TokenPay.Services;

public sealed record ObservedTransfer(
    string Network, string Asset, string? TokenContract, string TransactionHash,
    string TransferKey, string? FromAddress, string ToAddress, decimal Amount,
    long BlockNumber, DateTime BlockTimeUtc, int Confirmations);

public sealed record MatchResult(PaymentMatchStatus Status, Guid? OrderId = null, string? Reason = null);

public interface IStaticPaymentMatcher
{
    Task<MatchResult> ObserveAsync(ObservedTransfer transfer, CancellationToken cancellationToken = default);
    Task<MatchResult> ClaimByTxIdAsync(Guid orderId, string transactionHash, int? transferIndex = null, string? clientIp = null, CancellationToken cancellationToken = default);
    Task<MatchResult> ReportPaymentAsync(Guid orderId, CancellationToken cancellationToken = default);
    Task RetryUnmatchedAsync(CancellationToken cancellationToken = default);
}

/// <summary>静态地址交易的唯一候选匹配器。金额只作门槛，绝不作订单指纹。</summary>
public sealed class StaticPaymentMatcher : IStaticPaymentMatcher
{
    private readonly IFreeSql _db;
    private readonly StaticPaymentMatchOptions _options;
    private readonly Channel<TokenOrders> _paidOrders;
    private readonly ILogger<StaticPaymentMatcher> _logger;
    private readonly IReadOnlyList<IChainTransactionResolver> _resolvers;

    public StaticPaymentMatcher(IFreeSql db, IOptions<StaticPaymentMatchOptions> options,
        Channel<TokenOrders> paidOrders, ILogger<StaticPaymentMatcher> logger,
        IEnumerable<IChainTransactionResolver> resolvers)
    {
        _db = db;
        _options = options.Value;
        _paidOrders = paidOrders;
        _logger = logger;
        _resolvers = resolvers.ToArray();
    }

    public StaticPaymentMatcher(IFreeSql db, IOptions<StaticPaymentMatchOptions> options,
        Channel<TokenOrders> paidOrders, ILogger<StaticPaymentMatcher> logger)
        : this(db, options, paidOrders, logger, Array.Empty<IChainTransactionResolver>()) { }

    public async Task<MatchResult> ObserveAsync(ObservedTransfer transfer, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return new(PaymentMatchStatus.Unmatched, Reason: "static matching disabled");
        var payment = new ChainPayment
        {
            Network = transfer.Network, Asset = transfer.Asset, TokenContract = transfer.TokenContract,
            TransactionHash = transfer.TransactionHash, TransferKey = transfer.TransferKey,
            TransferIndex = int.TryParse(transfer.TransferKey.Split(':').Last(), out var legacyIndex) ? legacyIndex : 0,
            FromAddress = transfer.FromAddress, ToAddress = transfer.ToAddress, ActualAmount = transfer.Amount,
            BlockNumber = transfer.BlockNumber, BlockTime = EnsureUtc(transfer.BlockTimeUtc),
            Confirmations = transfer.Confirmations, MatchStatus = PaymentMatchStatus.Unmatched
        };
        try { await _db.Insert(payment).ExecuteAffrowsAsync(cancellationToken); }
        catch (Exception ex) when (IsUniqueConstraintViolation(ex))
        {
            payment = await _db.Select<ChainPayment>().Where(x => x.Network == transfer.Network && x.TransactionHash == transfer.TransactionHash && x.TransferKey == transfer.TransferKey).FirstAsync(cancellationToken);
        }
        return await MatchExistingAsync(payment, null, PaymentMatchMethod.TimeUnique, cancellationToken);
    }

    public async Task<MatchResult> ClaimByTxIdAsync(Guid orderId, string transactionHash, int? transferIndex = null, string? clientIp = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transactionHash) || transactionHash.Length is < 16 or > 128 || !transactionHash.All(c => char.IsAsciiLetterOrDigit(c) || c is 'x' or 'X'))
            return new(PaymentMatchStatus.Unmatched, Reason: "invalid transaction hash");
        var order = await _db.Select<TokenOrders>().Where(x => x.Id == orderId && x.IsStaticAddress).FirstAsync(cancellationToken);
        if (order == null) return new(PaymentMatchStatus.Unmatched, Reason: "order not found");
        var expectedNetwork = order.Currency.StartsWith("EVM_", StringComparison.Ordinal)
            ? order.Currency.Split('_')[1] : "TRON";
        var transfers = await _db.Select<ChainPayment>().Where(x => x.Network == expectedNetwork && x.TransactionHash == transactionHash && x.Asset == order.Currency)
            .WhereIf(transferIndex.HasValue, x => x.TransferIndex == transferIndex).ToListAsync(cancellationToken);
        if (transfers.Count == 0)
        {
            var resolver = _resolvers.SingleOrDefault(x => x.CanResolve(order));
            if (resolver == null) return new(PaymentMatchStatus.Unmatched, Reason: "no chain resolver configured for asset");
            var resolved = await resolver.ResolveAsync(order, transactionHash, cancellationToken);
            foreach (var observed in resolved.Where(x =>
                         x.Network == expectedNetwork && x.Asset == order.Currency
                         && string.Equals(x.ToAddress, order.ToAddress, StringComparison.OrdinalIgnoreCase)))
                await ObserveAsync(observed, cancellationToken);
            transfers = await _db.Select<ChainPayment>().Where(x => x.Network == expectedNetwork && x.TransactionHash == transactionHash && x.Asset == order.Currency)
                .WhereIf(transferIndex.HasValue, x => x.TransferIndex == transferIndex).ToListAsync(cancellationToken);
        }
        if (transfers.Count == 0) return new(PaymentMatchStatus.Unmatched, Reason: "链上查询未发现符合网络、币种和收款地址的成功到账");
        if (transfers.Count > 1 && !transferIndex.HasValue) return new(PaymentMatchStatus.Ambiguous, Reason: "该交易包含多笔转账，请指定日志序号");
        var payment = transfers.Single();
        try
        {
            await _db.Insert(new PaymentClaim { OrderId = orderId, ChainPaymentId = payment.Id, Network = expectedNetwork,
                TransactionHash = transactionHash, ClientIp = clientIp, ReviewStatus = PaymentClaimReviewStatus.Submitted })
                .ExecuteAffrowsAsync(cancellationToken);
        }
        catch (Exception ex) when (IsUniqueConstraintViolation(ex)) { }
        var result = await MatchExistingAsync(payment, orderId, PaymentMatchMethod.TxIdClaim, cancellationToken);
        await _db.Update<PaymentClaim>()
            .Set(x => x.ReviewStatus, result.Status == PaymentMatchStatus.Matched ? PaymentClaimReviewStatus.AutoMatched : PaymentClaimReviewStatus.ManualReview)
            .Set(x => x.ReviewReason, result.Reason)
            .Where(x => x.OrderId == orderId && x.Network == expectedNetwork && x.TransactionHash == transactionHash)
            .ExecuteAffrowsAsync(cancellationToken);
        return result;
    }

    public async Task<MatchResult> ReportPaymentAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var affected = await _db.Update<TokenOrders>()
            .Set(x => x.PaymentReportedAtUtc, now)
            .Where(x => x.Id == orderId && x.IsStaticAddress && x.Status == OrderStatus.Pending && x.PaymentReportedAtUtc == null)
            .ExecuteAffrowsAsync(cancellationToken);
        var order = await _db.Select<TokenOrders>().Where(x => x.Id == orderId && x.IsStaticAddress).FirstAsync(cancellationToken);
        if (order == null) return new(PaymentMatchStatus.Unmatched, Reason: "order not found");
        var since = now.AddMinutes(-30);
        var payments = await _db.Select<ChainPayment>()
            .Where(x => x.Asset == order.Currency && x.ToAddress == order.ToAddress && x.MatchedOrderId == null)
            .Where(x => x.BlockTime >= since).ToListAsync(cancellationToken);
        MatchResult result = new(order.PaymentMatchStatus, order.Id);
        foreach (var payment in payments)
            result = await MatchExistingAsync(payment, null, PaymentMatchMethod.TimeUnique, cancellationToken);
        _logger.LogInformation("订单 {OrderId} 已报告付款（新记录={NewReport}），仅重查该订单地址的近期到账", orderId, affected == 1);
        return result;
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
        if (payment.MatchStatus == PaymentMatchStatus.Matched)
            return claimedOrderId.HasValue && payment.MatchedOrderId != claimedOrderId
                ? new(PaymentMatchStatus.ManualReview, Reason: "transaction already belongs to another order")
                : new(payment.MatchStatus, payment.MatchedOrderId);
        var blockTime = EnsureUtc(payment.BlockTime);
        var earliest = blockTime.AddHours(-_options.LatePaymentRetentionHours);
        var latestCreation = blockTime.AddSeconds(_options.BlockTimeSkewSeconds);
        var query = _db.Select<TokenOrders>()
            .Where(x => x.IsStaticAddress && x.Status == OrderStatus.Pending)
            .Where(x => x.Currency == payment.Asset && x.ToAddress == payment.ToAddress);
        var retained = await query.ToListAsync(ct);
        var temporal = retained.Where(x =>
        {
            var created = PaymentTime.ToUtc(x.CreateTime);
            if (created < earliest || created > latestCreation) return false;
            var normal = created <= blockTime.AddSeconds(_options.BlockTimeSkewSeconds)
                         && blockTime <= created.AddMinutes(_options.AutoWindowMinutes);
            var late = x.PaymentReportedAtUtc.HasValue
                       && blockTime <= created.AddHours(_options.LatePaymentRetentionHours);
            return normal || late;
        }).ToList();
        var candidates = temporal.Where(x => payment.ActualAmount >= x.MinimumPaidAmount).ToList();
        if (candidates.Count == 0)
        {
            var hasExpiredCandidate = temporal.Count == 0 && retained.Any(x => PaymentTime.ToUtc(x.CreateTime) < earliest);
            var status = temporal.Count > 0 ? PaymentMatchStatus.AmountInsufficient :
                hasExpiredCandidate ? PaymentMatchStatus.Expired : PaymentMatchStatus.Unmatched;
            var reason = status switch
            {
                PaymentMatchStatus.AmountInsufficient => "amount below minimum_paid_amount",
                PaymentMatchStatus.Expired => "order payment window expired",
                _ => "no eligible order"
            };
            if (temporal.Count > 0)
            {
                var temporalIds = temporal.Select(o => o.Id).ToArray();
                await _db.Update<TokenOrders>().Set(x => x.PaymentMatchStatus, status).Set(x => x.PaymentMatchReason, reason)
                    .Where(x => temporalIds.Contains(x.Id)).ExecuteAffrowsAsync(ct);
            }
            return await MarkAsync(payment.Id, status, reason, ct);
        }
        if (candidates.Count > 1)
        {
            var candidateIds = candidates.Select(o => o.Id).ToArray();
            await _db.Update<TokenOrders>().Set(x => x.PaymentMatchStatus, claimedOrderId.HasValue ? PaymentMatchStatus.ManualReview : PaymentMatchStatus.Ambiguous)
                .Set(x => x.PaymentMatchReason, "multiple eligible orders; never assigned by public TxID")
                .Where(x => candidateIds.Contains(x.Id)).ExecuteAffrowsAsync(ct);
            if (claimedOrderId.HasValue)
                await _db.Update<TokenOrders>().Set(x => x.PaymentMatchStatus, PaymentMatchStatus.ManualReview)
                    .Set(x => x.PaymentMatchReason, "public TxID has multiple eligible orders")
                    .Where(x => x.Id == claimedOrderId.Value).ExecuteAffrowsAsync(ct);
            return await MarkAsync(payment.Id, claimedOrderId.HasValue ? PaymentMatchStatus.ManualReview : PaymentMatchStatus.Ambiguous,
                "multiple eligible orders; never assigned by public TxID", ct);
        }

        var order = candidates.Single();
        if (claimedOrderId.HasValue && order.Id != claimedOrderId.Value)
            return new(PaymentMatchStatus.ManualReview, Reason: "TxID is eligible for another order");
        if (payment.ActualAmount > order.Amount && !_options.AcceptOverpay)
            return await MarkAsync(payment.Id, PaymentMatchStatus.AmountInsufficient, "overpayment disabled", ct);
        TokenOrders? completed = null;
        _db.Transaction(() =>
        {
            var affected = _db.Update<TokenOrders>()
                .Set(x => x.Status, OrderStatus.Paid).Set(x => x.ChainPaymentId, payment.Id)
                .Set(x => x.BlockTransactionId, payment.TransactionHash).Set(x => x.PayAmount, payment.ActualAmount)
                .Set(x => x.PayTime, blockTime).Set(x => x.FromAddress, payment.FromAddress)
                .Set(x => x.PaymentMatchStatus, PaymentMatchStatus.Matched).Set(x => x.MatchMethod, method)
                .Set(x => x.IsLatePayment, blockTime > PaymentTime.ToUtc(order.CreateTime).AddMinutes(_options.AutoWindowMinutes))
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

    private static DateTime EnsureUtc(DateTime value) => PaymentTime.ToUtc(value);

    private static bool IsUniqueConstraintViolation(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
            if (current.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
