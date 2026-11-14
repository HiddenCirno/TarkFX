using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Utils;
using TarkFX.StockMarket.Server.Models;

namespace TarkFX.StockMarket.Server.Api;

// ══════════════ 请求 ══════════════

/// <summary>
/// 交易事件（走 client/game/profile/items/moving）。
/// Op: buy / sell / open_long / close_long / open_short / close_short
/// </summary>
public sealed record StockTradeEventRequest : BaseInteractionRequestData
{
    [JsonPropertyName("stockId")] public string StockId { get; set; } = "";
    [JsonPropertyName("shares")] public int Shares { get; set; }
    [JsonPropertyName("leverage")] public int Leverage { get; set; } = 1;
    [JsonPropertyName("op")] public string Op { get; set; } = "buy";
}

/// <summary>还款事件（同样走 item/moving 队列）。</summary>
public sealed record StockRepayEventRequest : BaseInteractionRequestData
{
    [JsonPropertyName("currency")] public string Currency { get; set; } = "RUB";
    [JsonPropertyName("amount")] public long Amount { get; set; }
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
    [JsonPropertyName("atLimitUp")] public bool AtLimitUp { get; set; }
    [JsonPropertyName("atLimitDown")] public bool AtLimitDown { get; set; }

    // ───── 股本 / 券行 / 熔断 ─────
    [JsonPropertyName("totalShares")] public long TotalShares { get; set; }
    [JsonPropertyName("marketCap")] public long MarketCap { get; set; }
    [JsonPropertyName("lenderTotal")] public long LenderTotal { get; set; }
    [JsonPropertyName("lenderAvailable")] public long LenderAvailable { get; set; }
    [JsonPropertyName("circuitBreakerActive")] public bool CircuitBreakerActive { get; set; }
    [JsonPropertyName("circuitBreakerRemainingTicks")] public long CircuitBreakerRemainingTicks { get; set; }
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

    // ───── 杠杆多仓（Leverage = 0 表示没有） ─────
    [JsonPropertyName("leverage")] public int Leverage { get; set; }
    [JsonPropertyName("leverageShares")] public int LeverageShares { get; set; }
    [JsonPropertyName("leverageMargin")] public decimal LeverageMargin { get; set; }
    [JsonPropertyName("leverageEquity")] public decimal LeverageEquity { get; set; }
    [JsonPropertyName("liquidationPrice")] public decimal LiquidationPrice { get; set; }
    [JsonPropertyName("leverageEntry")] public decimal LeverageEntry { get; set; }
    /// <summary>杠杆仓 T+1 解锁还剩多少塔科夫秒；0 = 已可平仓</summary>
    [JsonPropertyName("leverageUnlockIn")] public double LeverageUnlockIn { get; set; }

    // ───── 空头（ShortLeverage = 0 表示没有） ─────
    [JsonPropertyName("shortLeverage")] public int ShortLeverage { get; set; }
    [JsonPropertyName("shortShares")] public int ShortShares { get; set; }
    [JsonPropertyName("shortEntry")] public decimal ShortEntry { get; set; }
    [JsonPropertyName("shortMargin")] public decimal ShortMargin { get; set; }
    [JsonPropertyName("shortProceeds")] public decimal ShortProceeds { get; set; }
    [JsonPropertyName("shortEquity")] public decimal ShortEquity { get; set; }
    /// <summary>(保证金 + 卖出款) / 股数，即权益归零点</summary>
    [JsonPropertyName("shortLiquidationPrice")] public decimal ShortLiquidationPrice { get; set; }
    /// <summary>空头 T+1 解锁还剩多少塔科夫秒；0 = 已可平仓</summary>
    [JsonPropertyName("shortUnlockIn")] public double ShortUnlockIn { get; set; }
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
    [JsonPropertyName("maxHoldingShares")] public int MaxHoldingShares { get; set; }
    [JsonPropertyName("stocks")] public List<StockQuoteDto> Stocks { get; set; } = [];
    [JsonPropertyName("balances")] public BalancesDto Balances { get; set; } = new();
    [JsonPropertyName("holdings")] public List<HoldingDto> Holdings { get; set; } = [];
    [JsonPropertyName("debts")] public BalancesDto Debts { get; set; } = new();
    [JsonPropertyName("debtBlocked")] public bool DebtBlocked { get; set; }
}

public sealed class CandlesResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; } = true;
    [JsonPropertyName("stockId")] public string StockId { get; set; } = "";
    [JsonPropertyName("candles")] public List<Candle> Candles { get; set; } = [];
}

public sealed class SimpleResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("tick")] public long Tick { get; set; }
}
