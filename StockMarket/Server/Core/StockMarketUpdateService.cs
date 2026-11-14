using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace TarkFX.StockMarket.Server.Core;

/// <summary>
/// 服务端心跳：SPT 每 5 秒调用一次。
///
/// <para>
/// 这里只做一件事：让行情追上当前 tick。
/// <see cref="MarketStore.AdvanceTo"/> 是幂等的，没到下一个 tick 边界时是空操作，
/// 所以 5 秒一次的调用几乎没有成本。
/// </para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class StockMarketUpdateService(
    MarketRunner runner,
    ISptLogger<StockMarketUpdateService> logger) : IOnUpdate
{
    public Task<bool> OnUpdateAsync(long secondsSinceLastRun, CancellationToken cancellationToken)
    {
        try
        {
            // 逐 tick 生成 K 线，并在每个 tick 内检查强平
            runner.AdvanceToNow();
        }
        catch (Exception ex)
        {
            // 行情挂了不能把整个服务端更新循环带崩
            logger.Error($"[StockMarket] tick 推进失败：{ex}");
        }

        return Task.FromResult(true);
    }
}
