namespace TarkFX.StockMarket.Server.Core;

/// <summary>
/// 行情时钟：现实时间 → 塔科夫时间 → tick 序号。
///
/// <para>
/// 历元固定 2016-01-01T00:00:00Z，流速 7×。
/// 首次运行时把当时的现实时间记为锚点，之后：
/// <code>marketSeconds = (realNow - anchorReal) × 7</code>
/// </para>
///
/// <para>
/// 【为什么用绝对 tick 而不是累加计数器】
/// 1. 服务端卡顿/重启不漂移；
/// 2. 同一个 tick 重复计算是幂等的，补算安全；
/// 3. 调试快进只需要挪锚点。
/// </para>
///
/// <para>
/// 服务端**关闭期间行情照走**：锚点是持久化的，重新启动后
/// <see cref="TickIndex"/> 会直接跳到当前值，由 MarketStore 补齐中间的空洞。
/// </para>
/// </summary>
public sealed class MarketClock
{
    public static readonly DateTime Epoch = new(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>塔科夫时间流速（现实 1 秒 = 游戏 7 秒）</summary>
    public const double Speed = 7.0;

    /// <summary>一根 K 线 = 15 分钟塔科夫时间</summary>
    public const long TickSeconds = 15 * 60;

    /// <summary>一根 K 线的现实时长（秒）≈ 128.571</summary>
    public const double RealSecondsPerTick = TickSeconds / Speed;

    public long AnchorRealUnix { get; set; }

    public static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>从锚点开始经过的塔科夫秒数。</summary>
    public double MarketSeconds(long realUnix) => (realUnix - AnchorRealUnix) * Speed;

    public double MarketSecondsNow() => MarketSeconds(NowUnix());

    public long TickIndex(long realUnix) => (long)Math.Floor(MarketSeconds(realUnix) / TickSeconds);

    public long TickIndexNow() => TickIndex(NowUnix());

    /// <summary>某个 tick 对应的塔科夫日期（展示用）。</summary>
    public static DateTime TickToDate(long tick) => Epoch.AddSeconds((double)tick * TickSeconds);

    /// <summary>距离下一个 tick 边界还有多少现实秒（给客户端倒计时用）。</summary>
    public double SecondsToNextTick(long realUnix)
    {
        double seconds = MarketSeconds(realUnix);
        double remainder = seconds % TickSeconds;
        return (TickSeconds - remainder) / Speed;
    }
}
