using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;

namespace TarkFX.StockMarket.Server.Core;

/// <summary>
/// 行情推进器：**逐 tick 生成 + 逐 tick 强平**。
///
/// <para>
/// 为什么必须拆出来：强平卖出会产生交易冲击、反过来影响价格。
/// 如果一次性把离线期间的 K 线全部算完再检查强平，强平的卖出冲击就丢了，
/// 爆仓点也会被推到补算结束时的价格上 —— 这是错的。
/// 所以每个 tick 都是：生成 K 线 → 检查保证金 → （必要时）强平卖出 → 下一个 tick。
/// </para>
///
/// <para>
/// 全程只在最后落盘一次；强平自身的 SellWithImpact 会按需落盘（很少发生）。
/// </para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class MarketRunner(
    MarketStore market,
    LiquidationService liquidation,
    ISptLogger<MarketRunner> logger)
{
    /// <summary>单次补算的安全上限，防止异常情况下死循环。</summary>
    private const int MaxStepsPerRun = 100_000;

    public void AdvanceToNow() => AdvanceTo(market.TargetTickNow());

    public void AdvanceTo(long targetTick)
    {
        if (targetTick <= 0)
            return;

        // 离线太久直接跳档，避免几万次循环
        market.SkipIfTooFarBehind(targetTick);

        int steps = 0;

        while (market.LastTick < targetTick)
        {
            market.AdvanceOneTick();

            // 强平会卖出并产生冲击，必须在本 tick 结算完再进入下一根 K 线
            liquidation.CheckAll();

            steps++;
            if (steps >= MaxStepsPerRun)
            {
                logger.Error(
                    $"[StockMarket] 单次补算超过 {MaxStepsPerRun} 个 tick，已中止（可能时间跳变）。");
                break;
            }
        }

        market.SaveMarket();

        if (steps > 0)
            logger.Debug($"[StockMarket] 推进 {steps} 个 tick，当前 tick {market.LastTick}");
    }
}
