using TarkFX.StockMarket.Server.Models;

namespace TarkFX.StockMarket.Server.Core;

public readonly record struct CandleResult(Candle Candle, int Regime);

/// <summary>
/// 价格引擎：对数空间均值回归（Ornstein–Uhlenbeck）+ 波动率状态切换 + 泊松跳变。
///
/// <code>
/// regime_t ∈ {Quiet, Active}          ← 每 tick 以概率切换
/// x[t+1]   = x[t] + κ·(ln(mean) − x[t]) + μ + σ·scale(regime)·z + jump
/// price    = exp(x)
/// </code>
///
/// <para>
/// 【为什么加状态切换】恒定 σ 会让每根 K 线长得一模一样，连着一排同尺寸实体，
/// 看起来像心电图。真实的盘面是"长期横盘磨底（小实体/十字星）→ 放量出大实体"，
/// 所以用两状态马尔可夫链控制波动率倍率。
/// </para>
///
/// <para>
/// 【确定性】所有随机数来自 <see cref="SplitMix64.SeedFor"/>，只依赖 (stockId, tick)；
/// regime 是跨 tick 的状态，因此被持久化在 StockRuntime 里。
/// 服务端离线补算时按顺序重放，结果与在线生成逐位一致。
/// </para>
///
/// <para>
/// ⚠ 任何改动随机数消费顺序 / 子步数量 / 分布形状的修改，都必须把
/// <see cref="ModelVersion"/> +1 —— MarketStore 会据此丢弃旧行情重新生成。
/// </para>
/// </summary>
public sealed class PriceEngine
{
    /// <summary>行情模型版本。改算法必须 +1。</summary>
    public const int ModelVersion = 4;

    /// <summary>
    /// 每 tick 的内部子步数。
    /// 子步越多，上下影线越自然，实体相对越短（更容易出现十字星）。
    /// </summary>
    private const int SubSteps = 16;

    public CandleResult Next(StockDefinition def, StockRuntime runtime, long tick)
    {
        decimal open = runtime.LastClose > 0m ? runtime.LastClose : def.InitialPrice;

        var rng = new SplitMix64(SplitMix64.SeedFor(def.Id, tick));

        // ① 先决定本 tick 的波动率状态（必须放在随机数消费的最前面，
        //    否则补算与在线生成的序列会错位）
        int regime = runtime.VolatilityRegime;
        double switchChance = regime == 0 ? def.RegimeActivateChance : def.RegimeCalmChance;
        if (rng.NextDouble() < switchChance)
            regime = regime == 0 ? 1 : 0;

        double volScale = regime == 0 ? def.QuietVolatilityScale : def.ActiveVolatilityScale;
        double sigma = def.Volatility * Math.Max(0.01, volScale);

        double meanX = Math.Log((double)Math.Max(def.MeanPrice, def.MinPrice));
        double x = Math.Log((double)Math.Max(open, def.MinPrice));

        double sigmaStep = sigma * Math.Sqrt(1.0 / SubSteps);
        double kappaStep = def.MeanReversion / SubSteps;
        double driftStep = def.Drift / SubSteps;

        double high = x;
        double low = x;

        // ② 子步
        for (int i = 0; i < SubSteps; i++)
        {
            x += kappaStep * (meanX - x) + driftStep + sigmaStep * rng.NextGaussian();
            if (x > high) high = x;
            if (x < low) low = x;
        }

        // ③ 跳变
        if (rng.NextDouble() < def.JumpChance)
        {
            double direction = rng.NextDouble() < 0.5 ? -1.0 : 1.0;
            double magnitude = def.JumpScale * sigma * (0.5 + rng.NextDouble());
            x += direction * magnitude;
            if (x > high) high = x;
            if (x < low) low = x;
        }

        decimal close = RoundToTick(FromLog(x), def);
        decimal candleHigh = RoundToTick(FromLog(high), def);
        decimal candleLow = RoundToTick(FromLog(low), def);

        // 取整后再兜底一次，保证 OHLC 关系合法
        candleHigh = Math.Max(candleHigh, Math.Max(open, close));
        candleLow = Math.Min(candleLow, Math.Min(open, close));

        // ④ 成交量（活跃状态放量）
        double volumeScale = regime == 0 ? 0.45 : 1.9;
        long volume = (long)Math.Round(def.BaseVolume * volumeScale * (0.5 + rng.NextDouble()));

        Candle candle = new()
        {
            Tick = tick,
            Open = open,
            High = candleHigh,
            Low = candleLow,
            Close = close,
            Volume = volume
        };

        return new CandleResult(candle, regime);
    }

    private static decimal FromLog(double x)
    {
        double value = Math.Exp(x);
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0m;
        if (value > 1e15) return 1e15m;
        if (value < 1e-6) return 1e-6m;
        return (decimal)value;
    }

    /// <summary>夹到 [MinPrice, MaxPrice] 并按 TickSize 取整。</summary>
    public static decimal RoundToTick(decimal price, StockDefinition def)
    {
        decimal tickSize = def.TickSize > 0m ? def.TickSize : 0.01m;

        if (price < def.MinPrice) price = def.MinPrice;
        if (price > def.MaxPrice) price = def.MaxPrice;

        decimal steps = Math.Round(price / tickSize, MidpointRounding.AwayFromZero);
        decimal value = steps * tickSize;

        if (value < def.MinPrice) value = def.MinPrice;
        if (value > def.MaxPrice) value = def.MaxPrice;
        return value;
    }
}
