using System.Collections.Generic;

namespace TarkFX.StockMarket.Client.Model
{
    // 与服务端 Api/Dtos.cs 对应。Newtonsoft 反序列化大小写不敏感，
    // 所以这里的 PascalCase 属性可以直接匹配服务端的小写 json key。

    internal sealed class StateDto
    {
        public bool Success { get; set; }
        public long Tick { get; set; }
        public double MarketSeconds { get; set; }
        public double SecondsToNextTick { get; set; }
        public double TPlusOneSeconds { get; set; }
        public List<StockDto> Stocks { get; set; } = new List<StockDto>();
        public BalancesDto Balances { get; set; } = new BalancesDto();
        public List<HoldingDto> Holdings { get; set; } = new List<HoldingDto>();
    }

    internal sealed class StockDto
    {
        public string Id { get; set; }
        public string NameKey { get; set; }
        public string Currency { get; set; }
        public decimal Price { get; set; }
        public decimal PrevClose { get; set; }
        public decimal Change { get; set; }
        public double ChangePct { get; set; }
        public bool Halted { get; set; }
    }

    internal sealed class HoldingDto
    {
        public string StockId { get; set; }
        public int Shares { get; set; }
        public int Sellable { get; set; }
        public decimal AvgCost { get; set; }
        public decimal Value { get; set; }
        public double NextUnlockIn { get; set; }
    }

    internal sealed class BalancesDto
    {
        public long RUB { get; set; }
        public long USD { get; set; }
        public long EUR { get; set; }
    }

    internal sealed class CandlesDto
    {
        public bool Success { get; set; }
        public string StockId { get; set; }
        public List<CandleDto> Candles { get; set; } = new List<CandleDto>();
    }

    internal sealed class CandleDto
    {
        public long T { get; set; }
        public decimal O { get; set; }
        public decimal H { get; set; }
        public decimal L { get; set; }
        public decimal C { get; set; }
        public long V { get; set; }
    }

    internal sealed class TradeResultDto
    {
        public bool Success { get; set; }
        public string Code { get; set; }
        public string MessageKey { get; set; }
        public decimal? Price { get; set; }
        public long? Total { get; set; }
        public int? Shares { get; set; }
    }
}
