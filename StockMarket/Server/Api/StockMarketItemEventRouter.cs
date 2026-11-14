using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.DI.Routing;

namespace TarkFX.StockMarket.Server.Api;

/// <summary>
/// 把股票市场的交易/还款挂到原版 item/moving 队列。
///
/// <para>
/// 原版没有"直接扣物品"的 HTTP 路由；所有物品变更都走
/// <c>client/game/profile/items/moving</c> 的 <see cref="ItemEventRouter"/>。
/// 这里注册两个自定义 action，客户端用自定义 AbstractOperation 入队即可。
/// </para>
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers + 1)]
public sealed class StockMarketItemEventRouter(StockMarketItemEventService service)
    : ItemEventRouter(
    [
        new ItemRouteAction<StockTradeEventRequest>(
            "TarkFXStockTrade",
            async (url, pmc, body, sessionId, output, ct) =>
                await service.HandleTradeAsync(url, pmc, body, sessionId, output, ct)),

        new ItemRouteAction<StockRepayEventRequest>(
            "TarkFXStockRepay",
            async (url, pmc, body, sessionId, output, ct) =>
                await service.HandleRepayAsync(url, pmc, body, sessionId, output, ct))
    ])
{
}
