using Newtonsoft.Json;
using TokenPay.Extensions;

namespace TokenPay.Models.EthModel
{
#pragma warning disable CS8618 // ���˳����캯��ʱ������Ϊ null ���ֶα�������� null ֵ���뿼������Ϊ����Ϊ null��
    public class EthTransaction
    {
        [JsonProperty("blockNumber")]
        public int BlockNumber { get; set; }

        [JsonProperty("timeStamp")]
        public long TimeStamp { get; set; }
        public DateTime DateTime => (TimeStamp * 1000).ToDateTime();

        [JsonProperty("hash")]
        public string Hash { get; set; }

        [JsonProperty("nonce")]
        public string Nonce { get; set; }

        [JsonProperty("blockHash")]
        public string BlockHash { get; set; }

        [JsonProperty("transactionIndex")]
        public string TransactionIndex { get; set; }

        [JsonProperty("from")]
        public string From { get; set; }

        [JsonProperty("to")]
        public string To { get; set; }

        [JsonProperty("value")]
        public decimal Value { get; set; }
        public decimal RealAmount(int decimals)
        {
            return Value / TokenPay.Helper.PaymentAmountCalculator.DecimalPower(decimals);
        }

        [JsonProperty("traceId")]
        public string? TraceId { get; set; }

        [JsonProperty("gas")]
        public decimal Gas { get; set; }

        [JsonProperty("gasPrice")]
        public decimal GasPrice { get; set; }

        [JsonProperty("isError")]
        public int IsError { get; set; }

        [JsonProperty("txreceipt_status")]
        public int TxreceiptStatus { get; set; }

        [JsonProperty("input")]
        public string Input { get; set; }

        [JsonProperty("contractAddress")]
        public string ContractAddress { get; set; }

        [JsonProperty("cumulativeGasUsed")]
        public string CumulativeGasUsed { get; set; }

        [JsonProperty("gasUsed")]
        public string GasUsed { get; set; }

        [JsonProperty("confirmations")]
        public int Confirmations { get; set; }

        [JsonProperty("methodId")]
        public string MethodId { get; set; }

        [JsonProperty("functionName")]
        public string FunctionName { get; set; }
    }
#pragma warning restore CS8618 // ���˳����캯��ʱ������Ϊ null ���ֶα�������� null ֵ���뿼������Ϊ����Ϊ null��
}
