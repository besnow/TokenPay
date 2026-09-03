using TokenPay.Services;
using TokenPay.Models.EthModel;

namespace TokenPay.Tests;

public class ChainSafetyTests
{
    [Fact]
    public void Hashes_are_strict_and_canonical()
    {
        Assert.Equal("0x" + new string('a', 64), ChainTransactionHash.Normalize("ETH", "0X" + new string('A', 64)));
        Assert.Equal(new string('A', 64), ChainTransactionHash.Normalize("TRON", new string('a', 64)));
        Assert.Throws<FormatException>(() => ChainTransactionHash.Normalize("ETH", "abc"));
        Assert.Throws<FormatException>(() => ChainTransactionHash.Normalize("TRON", new string('z', 64)));
    }

    [Theory]
    [InlineData("0xde0b6b3a7640000", 18, "1")]
    [InlineData("0xf00000", 6, "15.72864")]
    [InlineData("0xf4240", 6, "1")]
    [InlineData("0x0", 18, "0")]
    public void Evm_quantities_are_unsigned(string hex, int decimals, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), EvmValueConverter.ToDecimal(hex, decimals));

    [Fact]
    public void Invalid_and_overflowing_evm_quantities_fail_explicitly()
    {
        Assert.Throws<FormatException>(() => EvmValueConverter.ToDecimal("0xzz", 18));
        Assert.Throws<OverflowException>(() => EvmValueConverter.ToDecimal("0x" + new string('f', 128), 18));
    }

    [Fact]
    public void Static_enabled_chain_requires_valid_rpc_host()
    {
        var chain = new EVMChain { Enable = true, ChainNameEN = "ETH", ChainName = "Ethereum", BaseCoin = "ETH" };
        var error = Assert.Throws<InvalidOperationException>(() => EvmConfigurationValidator.ValidateRpcHosts([chain], false));
        Assert.Contains("ETH", error.Message);
        chain.RpcHost = "ftp://unsafe.invalid";
        Assert.Throws<InvalidOperationException>(() => EvmConfigurationValidator.ValidateRpcHosts([chain], false));
        chain.RpcHost = "https://rpc.invalid";
        EvmConfigurationValidator.ValidateRpcHosts([chain], false);
    }

    [Fact]
    public async Task Tron_fingerprint_pages_are_complete_and_checkpointed()
    {
        var calls = new List<string?>();
        var checkpoints = new List<string>();
        var result = await ScanPagination.ReadTronAsync<int>((token, _) =>
        {
            calls.Add(token);
            return Task.FromResult(token == null
                ? (true, (IReadOnlyList<int>)new[] { 1, 2 }, "next")
                : (true, (IReadOnlyList<int>)new[] { 3 }, (string?)null));
        }, null, (token, _) => { checkpoints.Add(token); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(new[] { 1, 2, 3 }, result);
        Assert.Equal(new string?[] { null, "next" }, calls);
        Assert.Equal(new[] { "next" }, checkpoints);
    }

    [Fact]
    public async Task Tron_second_page_failure_does_not_report_completion()
    {
        var checkpoints = 0;
        await Assert.ThrowsAsync<ChainQueryException>(() => ScanPagination.ReadTronAsync<int>((token, _) =>
            Task.FromResult(token == null ? (true, (IReadOnlyList<int>)new[] { 1 }, "next") : (false, (IReadOnlyList<int>)Array.Empty<int>(), (string?)null)),
            null, (_, _) => { checkpoints++; return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal(1, checkpoints);
    }

    [Fact]
    public async Task Evm_reads_more_than_one_hundred_and_fails_on_bad_second_page()
    {
        var complete = await ScanPagination.ReadEvmAsync<int>((page, _) => Task.FromResult(
            page == 1 ? (true, (IReadOnlyList<int>)Enumerable.Range(0, 100).ToArray(), (string?)null)
                      : (true, (IReadOnlyList<int>)new[] { 100 }, (string?)null)), CancellationToken.None);
        Assert.Equal(101, complete.Count);

        await Assert.ThrowsAsync<ChainQueryException>(() => ScanPagination.ReadEvmAsync<int>((page, _) => Task.FromResult(
            page == 1 ? (true, (IReadOnlyList<int>)Enumerable.Range(0, 100).ToArray(), (string?)null)
                      : (false, (IReadOnlyList<int>)Array.Empty<int>(), "page two failed")), CancellationToken.None));
    }
}
