using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;
using TarkFX.StockMarket.Server.Models;

namespace TarkFX.StockMarket.Server.Api;

// ══════════════ 请求 ══════════════

public sealed class TradeRequest : IRequestData
{
    [JsonPropertyName("stockId")] public string StockId { get; set; } = "";
    [JsonPropertyName("shares")] public int Shares { get; set; }
}

public sealed class CandleRequest : IRequestData
{
    [JsonPropertyName("stockId")] public string StockId { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; } = 200;
}

public sealed class DevAdvanceRequest : IRequestData
{
    [JsonPropertyName("ticks")] public int Ticks { get; set; } = 1;
}

// ══════════════ 响应 ══════════════

public sealed class StockQuoteDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("nameKey")] public string NameKey { get; set; } = "";
    [JsonPropertyName("currency")] public string Currency { get; set; } = "RUB";
    [JsonPropertyName("price")] public decimal Price { get; set; }
    [JsonPropertyName("prevClose")] public decimal PrevClose { get; set; }
    [JsonPropertyName("change")] public decimal Change { get; set; }
    [JsonPropertyName("changePct")] public double ChangePct { get; set; }
    [JsonPropertyName("halted")] public bool Halted { get; set; }
}

public sealed class HoldingDto
{
    [JsonPropertyName("stockId")] public string StockId { get; set; } = "";
    [JsonPropertyName("shares")] public int Shares { get; set; }
    [JsonPropertyName("sellable")] public int Sellable { get; set; }
    [JsonPropertyName("avgCost")] public decimal AvgCost { get; set; }
    [JsonPropertyName("value")] public decimal Value { get; set; }
    /// <summary>下一批解锁还要多少塔科夫秒；0 = 没有锁定批次</summary>
    [JsonPropertyName("nextUnlockIn")] public double NextUnlockIn { get; set; }
}

public sealed class BalancesDto
{
    [JsonPropertyName("RUB")] public long Rub { get; set; }
    [JsonPropertyName("USD")] public long Usd { get; set; }
    [JsonPropertyName("EUR")] public long Eur { get; set; }
}

public sealed class StateResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; } = true;
    [JsonPropertyName("tick")] public long Tick { get; set; }
    [JsonPropertyName("marketSeconds")] public double MarketSeconds { get; set; }
    [JsonPropertyName("secondsToNextTick")] public double SecondsToNextTick { get; set; }
    [JsonPropertyName("tPlusOneSeconds")] public double TPlusOneSeconds { get; set; }
    [JsonPropertyName("stocks")] public List<StockQuoteDto> Stocks { get; set; } = [];
    [JsonPropertyName("balances")] public BalancesDto Balances { get; set; } = new();
    [JsonPropertyName("holdings")] public List<HoldingDto> Holdings { get; set; } = [];
}

public sealed class CandlesResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; } = true;
    [JsonPropertyName("stockId")] public string StockId { get; set; } = "";
    [JsonPropertyName("candles")] public List<Candle> Candles { get; set; } = [];
}

public sealed class TradeResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("messageKey")] public string? MessageKey { get; set; }
    [JsonPropertyName("price")] public decimal? Price { get; set; }
    [JsonPropertyName("total")] public long? Total { get; set; }
    [JsonPropertyName("shares")] public int? Shares { get; set; }
}

public sealed class SimpleResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("tick")] public long Tick { get; set; }
}
