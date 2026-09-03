using TokenPay.Domains;
using TokenPay.Models.EthModel;
using TokenPay.Services;

namespace TokenPay.Tests;

public class EvmTransferGuardTests
{
    private const string Address = "0xAABBcc";
    private readonly EVMChain _chain = new() { ChainNameEN = "ETH", BaseCoin = "ETH", Decimals = 18, Confirmations = 12 };

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Native_scanner_only_forwards_real_incoming_transfers(bool internalTransfer, bool outgoing, bool expected)
    {
        var matcher = new RecordingMatcher();
        var transaction = Native(outgoing ? "0xrecipient" : Address.ToLowerInvariant());
        var actual = await EvmTransferGuard.ObserveNativeAsync(matcher, _chain, "EVM_ETH_ETH", Address,
            transaction, internalTransfer, 120, default);
        Assert.Equal(expected, actual);
        Assert.Equal(expected ? 1 : 0, matcher.Transfers.Count);
        if (expected) Assert.Equal(transaction.To, matcher.Transfers.Single().ToAddress);
    }

    [Fact]
    public async Task Token_scanner_rejects_outgoing_wrong_contract_failed_and_unconfirmed_transactions()
    {
        var token = new EVMErc20 { Name = "USDT", ContractAddress = "0xToken", Decimals = 6 };
        foreach (var invalid in new[]
        {
            Token("0xother", "0xToken", 20, 0, 1), Token(Address, "0xwrong", 20, 0, 1),
            Token(Address, "0xToken", 20, 1, 1), Token(Address, "0xToken", 20, 0, 0),
            Token(Address, "0xToken", 11, 0, 1)
        })
        {
            var matcher = new RecordingMatcher();
            Assert.False(await EvmTransferGuard.ObserveTokenAsync(matcher, _chain, token, "EVM_ETH_USDT_ERC20", Address, invalid, default));
            Assert.Empty(matcher.Transfers);
        }
        var accepted = new RecordingMatcher();
        Assert.True(await EvmTransferGuard.ObserveTokenAsync(accepted, _chain, token, "EVM_ETH_USDT_ERC20", Address,
            Token(Address.ToLowerInvariant(), "0xtoken", 12, 0, 1), default));
        Assert.Equal("log:3", accepted.Transfers.Single().TransferKey);
    }

    private static EthTransaction Native(string to) => new() { To = to, From = "0xfrom", Hash = "hash", Value = 1_000_000_000_000_000_000m,
        BlockNumber = 100, Confirmations = 20, TxreceiptStatus = 1, MethodId = "0x", ContractAddress = "", TransactionIndex = "4", TraceId = "0_1", TimeStamp = 1_700_000_000 };
    private static ERC20Transaction Token(string to, string contract, decimal confirmations, int error, int receipt) => new()
    { To = to, From = "0xfrom", ContractAddress = contract, Confirmations = confirmations, IsError = error,
      TxreceiptStatus = receipt, TokenDecimal = 6, Value = 10_000_000, Hash = "hash", LogIndex = 3, BlockNumber = "100", TimeStamp = 1_700_000_000 };

    private sealed class RecordingMatcher : IStaticPaymentMatcher
    {
        public List<ObservedTransfer> Transfers { get; } = [];
        public Task<MatchResult> ObserveAsync(ObservedTransfer transfer, CancellationToken cancellationToken = default) { Transfers.Add(transfer); return Task.FromResult(new MatchResult(PaymentMatchStatus.Unmatched)); }
        public Task<MatchResult> ClaimByTxIdAsync(Guid orderId, string transactionHash, int? transferIndex = null, string? clientIp = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MatchResult> ReportPaymentAsync(Guid orderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RetryUnmatchedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
