namespace TarkFX.StockMarket.Server.Core;

/// <summary>
/// 确定性随机数：SplitMix64。
///
/// <para>
/// 【为什么不用 Random】
/// 行情必须满足：同一个 (stockId, tick) 无论何时、算多少次，结果**逐位一致**。
/// 只有这样：
///   · 服务端离线期间的空洞补算才能和在线的结果对上；
///   · 同一 tick 被重复触发不会产生不同价格；
///   · 以后要回测/复现，只依赖种子。
/// </para>
/// </summary>
public struct SplitMix64
{
    private ulong _state;

    public SplitMix64(ulong seed)
    {
        _state = seed;
    }

    public ulong NextUInt64()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>[0, 1)</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>标准正态，Box–Muller。</summary>
    public double NextGaussian()
    {
        // 避免 log(0)
        double u1 = 1.0 - NextDouble();
        double u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>按 (stockId, tick) 生成种子。FNV-1a 64。</summary>
    public static ulong SeedFor(string stockId, long tick)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in stockId)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        hash ^= (ulong)tick;
        hash *= 1099511628211UL;
        hash ^= 0x5DEECE66DUL;
        return hash;
    }
}
