using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Services.Locales;
using SPTarkov.Server.Core.Utils;
using TarkFX.StockMarket.Server.Core;

namespace TarkFX.StockMarket.Server.Api;

/// <summary>
/// 交易 / 还款的物品事件处理。
///
/// <para>
/// 这两个 action 挂在 <c>client/game/profile/items/moving</c> 的异步队列上，
/// 和原版 Move/Remove/Split 走同一条链路。这样：
/// </para>
/// <list type="bullet">
///   <item>扣钱由 <see cref="InventoryHelper.RemoveItemByCount"/> 完成，自动写 ProfileChanges；</item>
///   <item>客户端收到响应后由原生的 ApplyProfileChanges 同步仓库；</item>
///   <item>交易与物品变更在同一个队列里串行，不会出现"服务端扣了、客户端没扣"。</item>
/// </list>
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class StockMarketItemEventService(
    TradeService tradeService,
    HttpResponseUtil httpResponseUtil,
    ServerLocalisationService localisationService,
    ISptLogger<StockMarketItemEventService> logger)
{
    public async ValueTask<ItemEventRouterResponse> HandleTradeAsync(
        string url, PmcData pmc, StockTradeEventRequest body, MongoId sessionId,
        ItemEventRouterResponse output, CancellationToken cancellationToken)
    {
        TradeOutcome outcome;

        try
        {
            string op = (body.Op ?? "").Trim().ToLowerInvariant();

            outcome = op switch
            {
                "buy" => await tradeService.BuyAsync(
                    pmc, sessionId, body.StockId, body.Shares, 1, output),

                "sell" => await tradeService.SellAsync(
                    pmc, sessionId, body.StockId, body.Shares, 1, output),

                "open_long" => await tradeService.BuyAsync(
                    pmc, sessionId, body.StockId, body.Shares, body.Leverage, output),

                "close_long" => await tradeService.SellAsync(
                    pmc, sessionId, body.StockId, body.Shares, body.Leverage, output),

                "open_short" => await tradeService.OpenShortAsync(
                    pmc, sessionId, body.StockId, body.Shares, body.Leverage, output),

                "close_short" => await tradeService.CloseShortAsync(
                    pmc, sessionId, body.StockId, body.Shares, output),

                _ => Fail("invalid_op", "stockmarket/error/invalid_op")
            };
        }
        catch (Exception ex)
        {
            logger.Error($"[StockMarket] 交易事件异常：{ex}");
            outcome = Fail("internal_error", "stockmarket/error/internal");
        }

        if (!outcome.Success)
            AppendError(output, outcome.MessageKey);

        return output;
    }

    public async ValueTask<ItemEventRouterResponse> HandleRepayAsync(
        string url, PmcData pmc, StockRepayEventRequest body, MongoId sessionId,
        ItemEventRouterResponse output, CancellationToken cancellationToken)
    {
        TradeOutcome outcome;

        try
        {
            outcome = await tradeService.RepayAsync(
                pmc, sessionId, body.Currency, body.Amount, output);
        }
        catch (Exception ex)
        {
            logger.Error($"[StockMarket] 还款事件异常：{ex}");
            outcome = Fail("internal_error", "stockmarket/error/internal");
        }

        if (!outcome.Success)
            AppendError(output, outcome.MessageKey);

        return output;
    }

    private void AppendError(ItemEventRouterResponse output, string? messageKey)
    {
        string key = messageKey ?? "stockmarket/error/internal";
        string text;

        try
        {
            text = localisationService.GetText(key);
        }
        catch
        {
            text = key;
        }

        httpResponseUtil.AppendErrorToOutput(output, string.IsNullOrEmpty(text) ? key : text);
    }

    private static TradeOutcome Fail(string code, string messageKey) =>
        new(false, code, messageKey);
}
