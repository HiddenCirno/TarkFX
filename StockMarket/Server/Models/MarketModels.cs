using System.Text.Json.Serialization;

namespace TarkFX.StockMarket.Server.Models;

/// <summary>
/// 一支股票的静态定义。来自 config/stocks.json，运行时不修改。
///
/// <para>
/// 多股票扩展就是往配置里加一条 —— 引擎对每支股票完全独立，
/// 只需要 id 唯一即可。以后要做板块/指数，就在 <see cref="PriceEngine"/>
/// 之上再加一层“共享市场因子”，不用动这里的字段。
/// </para>
/// </summary>
public sealed record StockDefinition
{
    public string Id { get; init; } = "";
    public string NameKey { get; init; } = "";

    /// <summary>RUB / USD / EUR —— 对应 SPT 的 Money 模板</summary>
    public string Currency { get; init; } = "RUB";

    public decimal InitialPrice { get; init; } = 100m;

    /// <summary>均值回归的目标价（对数空间）</summary>
    public decimal MeanPrice { get; init; } = 100m;

    public decimal MinPrice { get; init; } = 1m;
    public decimal MaxPrice { get; init; } = 1_000_000m;

    /// <summary>最小报价单位，价格最后按它取整</summary>
    public decimal TickSize { get; init; } = 0.01m;

    /// <summary>每 tick 的基准对数收益率标准差</summary>
    public double Volatility { get; init; } = 0.006;

    /// <summary>均值回归强度（0 = 纯随机游走）</summary>
    public double MeanReversion { get; init; } = 0.02;

    public double Drift { get; init; } = 0.0;

    // ───── 波动率状态切换（制造横盘磨底 / 放量大实体） ─────

    /// <summary>安静状态下的波动率倍率</summary>
    public double QuietVolatilityScale { get; init; } = 0.35;

    /// <summary>活跃状态下的波动率倍率</summary>
    public double ActiveVolatilityScale { get; init; } = 2.0;

    /// <summary>每 tick 从安静切到活跃的概率</summary>
    public double RegimeActivateChance { get; init; } = 0.04;

    /// <summary>每 tick 从活跃切回安静的概率（比激活高，所以活跃段较短）</summary>
    public double RegimeCalmChance { get; init; } = 0.25;

    /// <summary>每 tick 发生跳变的概率</summary>
    public double JumpChance { get; init; } = 0.005;

    /// <summary>跳变幅度 = JumpScale × Volatility</summary>
    public double JumpScale { get; init; } = 3.0;

    public long BaseVolume { get; init; } = 1000;

    public bool Enabled { get; init; } = true;
}

/// <summary>一根 K 线。Tick 是绝对 tick 序号，不是时间戳。</summary>
public sealed record Candle
{
    [JsonPropertyName("t")] public long Tick { get; init; }
    [JsonPropertyName("o")] public decimal Open { get; init; }
    [JsonPropertyName("h")] public decimal High { get; init; }
    [JsonPropertyName("l")] public decimal Low { get; init; }
    [JsonPropertyName("c")] public decimal Close { get; init; }
    [JsonPropertyName("v")] public long Volume { get; init; }
}

/// <summary>
/// 单支股票的运行时状态。
///
/// <para>
/// 【停牌字段是预留的】以后接救市机制 / 玩家对抗时需要“暂停交易但保留价格”，
/// 现在的引擎已经会读 <see cref="HaltedUntilTick"/> —— 停牌期间只生成平盘 K 线，
/// 交易接口返回 halted。暂时没有任何代码会去写它。
/// </para>
/// </summary>
public sealed record StockRuntime
{
    public decimal LastClose { get; set; }

    /// <summary>
    /// 波动率状态：0 = 安静，1 = 活跃。
    /// 必须持久化 —— 离线补算要从同一个状态继续，否则补出来的 K 线和在线时不一致。
    /// </summary>
    public int VolatilityRegime { get; set; }

    /// <summary>停牌到哪个 tick 为止（含）。0 = 不停牌。</summary>
    public long HaltedUntilTick { get; set; }

    /// <summary>停牌原因（本地化 key），仅用于展示/日志。</summary>
    public string? HaltReasonKey { get; set; }

    public List<Candle> Candles { get; set; } = [];

    public bool IsHaltedAt(long tick) => HaltedUntilTick > 0 && tick < HaltedUntilTick;
}

/// <summary>落盘的全局行情文件。</summary>
public sealed record MarketFile
{
    /// <summary>
    /// 价格模型版本。算法一改这个值就 +1；
    /// 旧档读进来版本对不上会直接重置，避免"前半段旧分布、后半段新分布"的假行情。
    /// </summary>
    public int ModelVersion { get; set; }

    /// <summary>首次运行时记录的现实时间（Unix 秒），行情时钟的锚点。</summary>
    public long AnchorRealUnix { get; set; }

    /// <summary>最后已生成的 tick 序号。</summary>
    public long LastTick { get; set; }

    public Dictionary<string, StockRuntime> Stocks { get; set; } = [];
}

/// <summary>一个买入批次。T+1 按批次判定，卖出 FIFO。</summary>
public sealed class Lot
{
    public int Shares { get; set; }
    public decimal Price { get; set; }
    public long BuyTick { get; set; }
    public double BuyMarketSeconds { get; set; }
}

public sealed class Position
{
    public List<Lot> Lots { get; set; } = [];
}

/// <summary>单个玩家的持仓，key 是 sessionId。</summary>
public sealed class Portfolio
{
    public Dictionary<string, Position> Positions { get; set; } = [];
}

public sealed record PortfolioFile
{
    public Dictionary<string, Portfolio> Profiles { get; set; } = [];
}

public sealed record StocksConfigFile
{
    public List<StockDefinition> Stocks { get; init; } = [];
}
