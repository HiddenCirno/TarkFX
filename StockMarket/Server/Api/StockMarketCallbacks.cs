using SPTarkov.Common.Models.Logging;
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
/// 路由实现。所有返回都是 JSON 字符串，由 <see cref="HttpResponseUtil"/> 序列化。
/// </summary>
[Injectable(InjectionType.Transient)]
public sealed class StockMarketCallbacks(
    MarketStore market,
    PortfolioStore portfolios,
    TradeService tradeService,
    StockMarketConfig config,
    ProfileHelper profileHelper,
    HttpResponseUtil httpResponseUtil,
    ISptLogger<StockMarketCallbacks> logger)
{
    public ValueTask<string> GetState(MongoId sessionId)
    {
        string profileId = sessionId.ToString();
        PmcData? pmc = profileHelper.GetPmcProfile(sessionId);

        BalancesDto balances = new()
        {
            Rub = pmc is null ? 0 : TradeService.GetBalance(pmc, Money.ROUBLES),
            Usd = pmc is null ? 0 : TradeService.GetBalance(pmc, Money.DOLLARS),
            Eur = pmc is null ? 0 : TradeService.GetBalance(pmc, Money.EUROS)
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
                Halted = quote.Halted
            });

            (int shares, decimal avgCost) = portfolios.GetHolding(profileId, def.Id);
            if (shares <= 0) continue;

            holdings.Add(new HoldingDto
            {
                StockId = def.Id,
                Shares = shares,
                Sellable = portfolios.GetSellableShares(profileId, def.Id, nowSeconds),
                AvgCost = avgCost,
                Value = quote.Price * shares,
                NextUnlockIn = portfolios.GetNextUnlockIn(profileId, def.Id, nowSeconds)
            });
        }

        StateResponse response = new()
        {
            Success = true,
            Tick = market.CurrentTick(MarketClock.NowUnix()),
            MarketSeconds = nowSeconds,
            SecondsToNextTick = market.SecondsToNextTick(),
            TPlusOneSeconds = PortfolioStore.TPlusOneMarketSeconds,
            Stocks = stocks,
            Balances = balances,
            Holdings = holdings
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

    public async ValueTask<string> Buy(TradeRequest request, MongoId sessionId)
    {
        try
        {
            TradeOutcome outcome = await tradeService.BuyAsync(sessionId, request.StockId, request.Shares);
            return httpResponseUtil.NoBody(ToResponse(outcome));
        }
        catch (Exception ex)
        {
            logger.Error($"[StockMarket] 交易处理异常：{ex}");
            return httpResponseUtil.NoBody(new TradeResponse
            {
                Success = false,
                Code = "internal_error",
                MessageKey = "stockmarket/error/internal"
            });
        }
    }

    public async ValueTask<string> Sell(TradeRequest request, MongoId sessionId)
    {
        try
        {
            TradeOutcome outcome = await tradeService.SellAsync(sessionId, request.StockId, request.Shares);
            return httpResponseUtil.NoBody(ToResponse(outcome));
        }
        catch (Exception ex)
        {
            logger.Error($"[StockMarket] 交易处理异常：{ex}");
            return httpResponseUtil.NoBody(new TradeResponse
            {
                Success = false,
                Code = "internal_error",
                MessageKey = "stockmarket/error/internal"
            });
        }
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
        market.AdvanceTo(target);

        return ValueTask.FromResult(httpResponseUtil.NoBody(new SimpleResponse
        {
            Success = true,
            Tick = market.CurrentTick(MarketClock.NowUnix())
        }));
    }

    private static TradeResponse ToResponse(TradeOutcome outcome) => new()
    {
        Success = outcome.Success,
        Code = outcome.Code,
        MessageKey = outcome.MessageKey,
        Price = outcome.Price,
        Total = outcome.Total,
        Shares = outcome.Shares
    };
}
