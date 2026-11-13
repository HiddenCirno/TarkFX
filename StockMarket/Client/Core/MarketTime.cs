using System;
using System.Globalization;

namespace TarkFX.StockMarket.Client.Core
{
    /// <summary>
    /// tick → 塔科夫时间。与服务端 MarketClock 同一套定义：
    /// 历元 2016-01-01T00:00:00Z，每 tick 15 分钟。
    /// </summary>
    internal static class MarketTime
    {
        private static readonly DateTime Epoch =
            new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        internal static DateTime FromTick(long tick) =>
            Epoch.AddSeconds(tick * 900.0);

        internal static string Short(long tick) =>
            FromTick(tick).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
