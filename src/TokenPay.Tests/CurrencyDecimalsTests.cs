using Microsoft.Extensions.Configuration;
using TokenPay.Controllers;
using TokenPay.Models.EthModel;

namespace TokenPay.Tests;

public class CurrencyDecimalsTests
{
    [Theory]
    [InlineData("EVM_ETH_ETH", 18)]
    [InlineData("EVM_BSC_BNB", 18)]
    [InlineData("EVM_Polygon_POL", 18)]
    [InlineData("EVM_ETH_USDT_ERC20", 6)]
    [InlineData("EVM_ETH_USDC_ERC20", 6)]
    public void Decimals_come_from_chain_and_token_configuration(string currency, int expected)
    {
        var token = new[] { new EVMErc20 { Name = "USDT", ContractAddress = "u", Decimals = 6 }, new EVMErc20 { Name = "USDC", ContractAddress = "c", Decimals = 6 } };
        var chains = new[]
        {
            new EVMChain { ChainNameEN = "ETH", BaseCoin = "ETH", Decimals = 18, ERC20Name = "ERC20", ERC20 = token.ToList() },
            new EVMChain { ChainNameEN = "BSC", BaseCoin = "BNB", Decimals = 18 },
            new EVMChain { ChainNameEN = "Polygon", BaseCoin = "POL", Decimals = 18 }
        };
        Assert.Equal(expected, HomeController.GetDecimals(currency, new ConfigurationBuilder().Build(), chains));
    }
}
