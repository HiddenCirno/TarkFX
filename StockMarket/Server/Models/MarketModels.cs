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

    public decimal InitialPrice { get; init; } = 10_000m;

    /// <summary>均值回归的目标价（对数空间）</summary>
    public decimal MeanPrice { get; init; } = 10_000m;

    public decimal MinPrice { get; init; } = 100m;
    public decimal MaxPrice { get; init; } = 10_000_000m;

    /// <summary>最小报价单位，价格最后按它取整</summary>
    public decimal TickSize { get; init; } = 1m;

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

    /// <summary>
    /// 总股本。市值 = 现价 × TotalShares；券行持有 = TotalShares × LenderPercent。
    /// 用 long 存，10,000,000 股 × 10,000 = 1e11 也不会溢出。
    /// </summary>
    public long TotalShares { get; init; } = 10_000_000;

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

    /// <summary>券行已经借出多少股（全服合计）。平仓/强平/转生时归还。</summary>
    public long LenderLentShares { get; set; }

    /// <summary>
    /// 涨跌停熔断：在这个 tick 之前，系统强平暂停。
    /// 首次触及涨跌停时设置一次（边沿触发），不因持续封板而续期。
    /// </summary>
    public long LiquidationFrozenUntilTick { get; set; }

    /// <summary>上一 tick 是否处于涨/跌停价，用于边沿触发熔断。</summary>
    public bool WasAtLimit { get; set; }

    public List<Candle> Candles { get; set; } = [];

    public bool IsHaltedAt(long tick) => HaltedUntilTick > 0 && tick < HaltedUntilTick;

    public bool IsLiquidationFrozenAt(long tick) =>
        LiquidationFrozenUntilTick > 0 && tick < LiquidationFrozenUntilTick;
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

/// <summary>
/// 一笔杠杆持仓（每支股票最多一笔）。
///
/// <para>
/// 经济含义：玩家用 <see cref="Margin"/> 自有资金，按 <see cref="Leverage"/> 倍
/// 撬动 <see cref="Shares"/> 股；剩余部分是券商借出的 <see cref="Borrowed"/>。
/// 权益 = 市值 − 借款；权益 ≤ 0 时强平（保证金亏完）。
/// </para>
/// </summary>
public sealed class LeveragedPosition
{
    public int Leverage { get; set; }
    public int Shares { get; set; }
    public decimal EntryPrice { get; set; }

    /// <summary>玩家自有资金（从背包里扣掉的那部分）</summary>
    public decimal Margin { get; set; }

    /// <summary>借入金额 = Shares × EntryPrice − Margin</summary>
    public decimal Borrowed { get; set; }

    public long OpenedTick { get; set; }
    public double OpenedMarketSeconds { get; set; }
}

/// <summary>
/// 一笔做空仓位（每支股票最多一笔）。
///
/// <para>
/// 借入 <see cref="Shares"/> 股并立即卖出，卖出所得 <see cref="Proceeds"/> 冻结，
/// 同时从玩家现金扣 <see cref="Margin"/>（= 卖出额 / 杠杆）。
/// 权益 = Margin + Proceeds − Shares × 现价；权益 ≤ 0 时强平。
/// </para>
/// </summary>
public sealed class ShortPosition
{
    public int Leverage { get; set; }
    public int Shares { get; set; }

    /// <summary>开空时的分批卖出均价</summary>
    public decimal EntryPrice { get; set; }

    /// <summary>卖出所得（冻结，不发给玩家）</summary>
    public decimal Proceeds { get; set; }

    /// <summary>玩家自有保证金（从现金扣掉的部分）</summary>
    public decimal Margin { get; set; }

    public long OpenedTick { get; set; }
    public double OpenedMarketSeconds { get; set; }
}

/// <summary>单个玩家的持仓，key 是 sessionId。</summary>
public sealed class Portfolio
{
    public Dictionary<string, Position> Positions { get; set; } = [];

    /// <summary>stockId → 杠杆持仓。旧档没有这个字段时反序列化会自动用空字典。</summary>
    public Dictionary<string, LeveragedPosition> Leveraged { get; set; } = [];

    /// <summary>stockId → 做空仓位。</summary>
    public Dictionary<string, ShortPosition> Shorts { get; set; } = [];

    /// <summary>币种（RUB/USD/EUR）→ 欠债金额。欠债未清零前封锁全部股市操作。</summary>
    public Dictionary<string, decimal> Debts { get; set; } = [];
}

public sealed record PortfolioFile
{
    public Dictionary<string, Portfolio> Profiles { get; set; } = [];
}

public sealed record StocksConfigFile
{
    public List<StockDefinition> Stocks { get; init; } = [];
}
