using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Utils;
using TarkFX.StockMarket.Server.Core;
using TarkFX.StockMarket.Server.Models;

namespace TarkFX.StockMarket.Server.Api;

/// <summary>
/// 行情/持仓查询路由。
///
/// <para>
/// 买入、卖出、开空、平空、还款都不再走 HTTP 路由 ——
/// 它们必须经过 <c>client/game/profile/items/moving</c> 的异步队列，
/// 由 <see cref="StockMarketItemEventService"/> 处理，才能把物品变更同步回客户端。
/// </para>
/// </summary>
[Injectable(InjectionType.Transient)]
public sealed class StockMarketCallbacks(
    MarketStore market,
    PortfolioStore portfolios,
    StockMarketConfig config,
    LiquidationService liquidation,
    MarketRunner runner,
    ProfileHelper profileHelper,
    HttpResponseUtil httpResponseUtil)
{
    public ValueTask<string> GetState(MongoId sessionId)
    {
        // 拉行情前先扫一遍强平，避免 UI 显示一笔已经爆掉的仓位
        liquidation.CheckAll();

        string profileId = sessionId.ToString();
        PmcData? pmc = profileHelper.GetPmcProfile(sessionId);

        BalancesDto balances = new()
        {
            Rub = pmc is null ? 0 : TradeService.GetBalance(pmc, Money.ROUBLES),
            Usd = pmc is null ? 0 : TradeService.GetBalance(pmc, Money.DOLLARS),
            Eur = pmc is null ? 0 : TradeService.GetBalance(pmc, Money.EUROS)
        };

        Dictionary<string, decimal> debts = portfolios.GetDebts(profileId);
        BalancesDto debtDto = new()
        {
            Rub = ToLong(debts, "RUB"),
            Usd = ToLong(debts, "USD"),
            Eur = ToLong(debts, "EUR")
        };

        List<StockQuoteDto> stocks = [];
        List<HoldingDto> holdings = [];
        double nowSeconds = market.MarketSecondsNow();

        foreach (StockDefinition def in market.Definitions)
        {
            if (!def.Enabled) continue;

            StockQuote? quote = market.GetQuote(def.Id);
            if (quote is null) continue;

            decimal change = quote.Price - quote.PreviousClose;
            long breakerRemaining = Math.Max(
                0L, quote.LiquidationFrozenUntilTick - quote.LastTick);

            stocks.Add(new StockQuoteDto
            {
                Id = def.Id,
                NameKey = def.NameKey,
                Currency = def.Currency,
                Price = quote.Price,
                PrevClose = quote.PreviousClose,
                Change = change,
                ChangePct = quote.PreviousClose > 0m
                    ? (double)(change / quote.PreviousClose) * 100.0
                    : 0.0,
                Halted = quote.Halted,
                AtLimitUp = quote.AtLimitUp,
                AtLimitDown = quote.AtLimitDown,
                TotalShares = def.TotalShares,
                MarketCap = quote.MarketCap,
                LenderTotal = quote.LenderTotalShares,
                LenderAvailable = quote.LenderAvailableShares,
                CircuitBreakerActive = breakerRemaining > 0,
                CircuitBreakerRemainingTicks = breakerRemaining
            });

            (int shares, decimal avgCost) = portfolios.GetHolding(profileId, def.Id);
            LeveragedPosition? leveraged = portfolios.GetLeveraged(profileId, def.Id);
            ShortPosition? shortPosition = portfolios.GetShort(profileId, def.Id);

            if (shares <= 0 && leveraged is null && shortPosition is null)
                continue;

            decimal equity = leveraged is null
                ? 0m
                : leveraged.Shares * quote.Price - leveraged.Borrowed;
            decimal liquidationPrice = leveraged is null || leveraged.Shares <= 0
                ? 0m
                : leveraged.Borrowed / leveraged.Shares;
            double leverageUnlockIn = leveraged is null
                ? 0.0
                : Math.Max(0.0,
                    portfolios.EffectiveTPlusOneSeconds -
                    (nowSeconds - leveraged.OpenedMarketSeconds));

            decimal shortEquity = shortPosition is null
                ? 0m
                : shortPosition.Margin + shortPosition.Proceeds -
                  shortPosition.Shares * quote.Price;
            decimal shortLiquidation = shortPosition is null || shortPosition.Shares <= 0
                ? 0m
                : (shortPosition.Margin + shortPosition.Proceeds) / shortPosition.Shares;
            double shortUnlockIn = shortPosition is null
                ? 0.0
                : Math.Max(0.0,
                    portfolios.EffectiveTPlusOneSeconds -
                    (nowSeconds - shortPosition.OpenedMarketSeconds));

            holdings.Add(new HoldingDto
            {
                StockId = def.Id,
                Shares = shares,
                Sellable = portfolios.GetSellableShares(profileId, def.Id, nowSeconds),
                AvgCost = avgCost,
                Value = quote.Price * shares,
                NextUnlockIn = portfolios.GetNextUnlockIn(profileId, def.Id, nowSeconds),
                Leverage = leveraged?.Leverage ?? 0,
                LeverageShares = leveraged?.Shares ?? 0,
                LeverageMargin = leveraged?.Margin ?? 0m,
                LeverageEquity = equity,
                LiquidationPrice = liquidationPrice,
                LeverageEntry = leveraged?.EntryPrice ?? 0m,
                LeverageUnlockIn = leverageUnlockIn,
                ShortLeverage = shortPosition?.Leverage ?? 0,
                ShortShares = shortPosition?.Shares ?? 0,
                ShortEntry = shortPosition?.EntryPrice ?? 0m,
                ShortMargin = shortPosition?.Margin ?? 0m,
                ShortProceeds = shortPosition?.Proceeds ?? 0m,
                ShortEquity = shortEquity,
                ShortLiquidationPrice = shortLiquidation,
                ShortUnlockIn = shortUnlockIn
            });
        }

        StateResponse response = new()
        {
            Success = true,
            Tick = market.CurrentTick(MarketClock.NowUnix()),
            MarketSeconds = nowSeconds,
            SecondsToNextTick = market.SecondsToNextTick(),
            TPlusOneSeconds = portfolios.EffectiveTPlusOneSeconds,
            MaxHoldingShares = config.MaxHoldingShares,
            Stocks = stocks,
            Balances = balances,
            Holdings = holdings,
            Debts = debtDto,
            DebtBlocked = debts.Count > 0
        };

        return ValueTask.FromResult(httpResponseUtil.NoBody(response));
    }

    public ValueTask<string> GetCandles(CandleRequest request, MongoId sessionId)
    {
        List<Candle> candles = market.GetCandles(request.StockId, request.Count);
        return ValueTask.FromResult(httpResponseUtil.NoBody(new CandlesResponse
        {
            Success = true,
            StockId = request.StockId,
            Candles = candles
        }));
    }

    /// <summary>调试：快进 N 个 tick。仅在 config.json 里开启后可用。</summary>
    public ValueTask<string> DevAdvance(DevAdvanceRequest request, MongoId sessionId)
    {
        if (!config.EnableDevRoutes)
        {
            return ValueTask.FromResult(httpResponseUtil.NoBody(new SimpleResponse
            {
                Success = false,
                Tick = market.CurrentTick(MarketClock.NowUnix())
            }));
        }

        long target = market.CurrentTick(MarketClock.NowUnix()) + Math.Clamp(request.Ticks, 1, 10000);
        runner.AdvanceTo(target);

        return ValueTask.FromResult(httpResponseUtil.NoBody(new SimpleResponse
        {
            Success = true,
            Tick = market.CurrentTick(MarketClock.NowUnix())
        }));
    }

    private static long ToLong(Dictionary<string, decimal> source, string currency)
    {
        return source.TryGetValue(currency, out decimal value)
            ? (long)Math.Ceiling(Math.Max(0m, value))
            : 0;
    }
}
