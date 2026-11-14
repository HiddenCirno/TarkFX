using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Server;

namespace TarkFX.StockMarket.Server.Core;

/// <summary>
/// config/config.json。卖出侧默认收取 5% 手续费（强平不计费）。
/// </summary>
public sealed class ModConfigFile
{
    public decimal FeeRate { get; set; } = 0.05m;
    public long MailLifetimeSeconds { get; set; } = 604800;
    public bool EnableDevRoutes { get; set; } = false;

    /// <summary>每买/卖 1 倍 BaseVolume 的股票对对数价格的冲击（0 = 关闭）</summary>
    public double MarketImpactCoefficient { get; set; } = 0.02;

    /// <summary>单笔交易的最大对数冲击（0.15 ≈ ±16%）</summary>
    public double MaxImpactLog { get; set; } = 0.15;

    /// <summary>单日涨跌停幅度（0.30 = ±30%；0 = 关闭）</summary>
    public double DailyLimitPercent { get; set; } = 0.30;

    /// <summary>个人持股上限（现货 + 杠杆多 + 空头合计）</summary>
    public int MaxHoldingShares { get; set; } = 100_000;

    /// <summary>券行持有流通股本的比例（0.30 = 30%）</summary>
    public double LenderPercent { get; set; } = 0.30;

    /// <summary>涨跌停熔断时长（塔科夫小时），96 tick = 24 小时</summary>
    public double CircuitBreakerHours { get; set; } = 24.0;

    /// <summary>冲击曲线指数：0.5 = sqrt（凹曲线），1.0 = 线性</summary>
    public double MarketImpactExponent { get; set; } = 0.5;

    /// <summary>Debug 模式：T+1 变为 T+0（现货与杠杆都立即解锁）</summary>
    public bool DebugMode { get; set; } = false;

    /// <summary>卖出步进股数：每卖出这么多股重算一次价格与冲击（0 = 不分步）</summary>
    public int SellStepShares { get; set; } = 10;
}

[Injectable(InjectionType.Singleton)]
public sealed class StockMarketConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public decimal FeeRate { get; private set; } = 0.05m;
    public long MailLifetimeSeconds { get; private set; } = 604800;
    public bool EnableDevRoutes { get; private set; }
    public double MarketImpactCoefficient { get; private set; } = 0.02;
    public double MaxImpactLog { get; private set; } = 0.15;
    public double DailyLimitPercent { get; private set; } = 0.30;
    public double MarketImpactExponent { get; private set; } = 0.5;
    public bool DebugMode { get; private set; }
    public int SellStepShares { get; private set; } = 10;
    public int MaxHoldingShares { get; private set; } = 100_000;
    public double LenderPercent { get; private set; } = 0.30;
    public double CircuitBreakerHours { get; private set; } = 24.0;

    /// <summary>熔断时长换算成 tick（市场时钟 15 分钟/tick，塔科夫时间）。</summary>
    public long CircuitBreakerTicks =>
        (long)Math.Round(Math.Max(0.0, CircuitBreakerHours) * 3600.0 / MarketClock.TickSeconds);

    public StockMarketConfig(ISptLogger<StockMarketConfig> logger, ModHelper modHelper)
    {
        try
        {
            string modFolder = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
            string path = Path.Combine(modFolder, "config", "config.json");
            if (!File.Exists(path))
                return;

            ModConfigFile? config = JsonSerializer.Deserialize<ModConfigFile>(
                File.ReadAllText(path), JsonOptions);
            if (config is null)
                return;

            FeeRate = Math.Clamp(config.FeeRate, 0m, 1m);
            MailLifetimeSeconds = Math.Max(60, config.MailLifetimeSeconds);
            EnableDevRoutes = config.EnableDevRoutes;
            MarketImpactCoefficient = Math.Clamp(config.MarketImpactCoefficient, 0.0, 0.5);
            MaxImpactLog = Math.Clamp(config.MaxImpactLog, 0.0, 0.5);
            DailyLimitPercent = Math.Clamp(config.DailyLimitPercent, 0.0, 0.5);
            MarketImpactExponent = Math.Clamp(config.MarketImpactExponent, 0.2, 1.5);
            DebugMode = config.DebugMode;
            SellStepShares = Math.Clamp(config.SellStepShares, 0, 100_000);
            MaxHoldingShares = Math.Clamp(config.MaxHoldingShares, 1, 100_000_000);
            LenderPercent = Math.Clamp(config.LenderPercent, 0.0, 1.0);
            CircuitBreakerHours = Math.Clamp(config.CircuitBreakerHours, 0.0, 24.0 * 30);
        }
        catch (Exception ex)
        {
            logger.Error($"[StockMarket] config.json 读取失败，使用默认值：{ex.Message}");
        }
    }
}
