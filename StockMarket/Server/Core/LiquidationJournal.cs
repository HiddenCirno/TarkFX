using System.Reflection;
using System.Text;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Server;

namespace TarkFX.StockMarket.Server.Core;

/// <summary>
/// 强平复盘日志（append-only）：<c>data/liquidation.log</c>。
///
/// <para>
/// 每次系统强平写一行，包含现实时间、塔科夫时间、tick、方向、股数、
/// 触发价、实际成交均价、结算和欠债。离线补算时 tick 是历史 tick，
/// UTC 时间是补算发生的现实时间，两者都记下来方便对照。
/// </para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class LiquidationJournal
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly object _sync = new();
    private readonly string _path;
    private readonly ISptLogger<LiquidationJournal> _logger;

    public LiquidationJournal(
        ISptLogger<LiquidationJournal> logger,
        ModHelper modHelper)
    {
        _logger = logger;

        string modFolder = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        _path = Path.Combine(modFolder, "data", "liquidation.log");
    }

    public void WriteLong(
        long tick, string profileId, string stockId, int shares,
        decimal triggerPrice, decimal averageFill, decimal gross,
        decimal borrowed, decimal margin, decimal settlement, decimal debt)
    {
        Write(
            "long", tick, profileId, stockId, shares,
            triggerPrice, averageFill, gross, borrowed, margin,
            0m, settlement, debt);
    }

    public void WriteShort(
        long tick, string profileId, string stockId, int shares,
        decimal triggerPrice, decimal averageCover, decimal cover,
        decimal margin, decimal proceeds, decimal settlement, decimal debt)
    {
        Write(
            "short", tick, profileId, stockId, shares,
            triggerPrice, averageCover, cover, 0m, margin,
            proceeds, settlement, debt);
    }

    private void Write(
        string side, long tick, string profileId, string stockId, int shares,
        decimal triggerPrice, decimal averageFill, decimal gross,
        decimal borrowed, decimal margin, decimal proceeds,
        decimal settlement, decimal debt)
    {
        try
        {
            DateTime tarkov = MarketClock.TickToDate(tick);
            DateTime utc = DateTime.UtcNow;

            string line = string.Join(" | ",
                utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                $"tarkov={tarkov:yyyy-MM-dd HH:mm}",
                $"tick={tick}",
                side,
                $"profile={profileId}",
                $"stock={stockId}",
                $"shares={shares}",
                $"trigger={triggerPrice:0.00}",
                $"fill_avg={averageFill:0.00}",
                $"gross={gross:0.00}",
                $"borrowed={borrowed:0.00}",
                $"margin={margin:0.00}",
                $"proceeds={proceeds:0.00}",
                $"settlement={settlement:0.00}",
                $"debt={debt:0.00}");

            lock (_sync)
            {
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.AppendAllText(_path, line + Environment.NewLine, Utf8NoBom);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[StockMarket] 写入强平日志失败：{ex.Message}");
        }
    }
}
