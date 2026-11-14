using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;

namespace TarkFX.StockMarket.Server.Api;

/// <summary>
/// 客户端路由。
///
/// <para>
/// 全部 POST（客户端统一走 RequestHandler.PostJson）。
/// 行情快照和 K 线分开：多股票时 fast state 只有报价，
/// 选中某支股票才拉它的 200 根 K 线。
/// </para>
/// </summary>
[Injectable(InjectionType.Transient, OnLoadOrder.Routers)]
public sealed class StockMarketRouter : StaticRouter
{
    public StockMarketRouter(JsonUtil jsonUtil, StockMarketCallbacks callbacks)
        : base(jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                "/stock-market/state",
                async (url, request, sessionId, output, ct) =>
                    await callbacks.GetState(sessionId)),

            new RouteAction<CandleRequest>(
                "/stock-market/candles",
                async (url, request, sessionId, output, ct) =>
                    await callbacks.GetCandles(request, sessionId)),

            new RouteAction<DevAdvanceRequest>(
                "/stock-market/dev/advance",
                async (url, request, sessionId, output, ct) =>
                    await callbacks.DevAdvance(request, sessionId))
        ])
    {
    }
}
