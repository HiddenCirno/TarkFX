using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Server;
using TarkFX.StockMarket.Server.Models;

namespace TarkFX.StockMarket.Server.Core;

/// <summary>分批卖出的成交结果。</summary>
public readonly record struct FillResult(decimal Gross, decimal FirstPrice, decimal LastPrice);

/// <summary>一次行情快照，供路由层转成 DTO。</summary>
public sealed record StockQuote(
    StockDefinition Definition,
    decimal Price,
    decimal PreviousClose,
    bool Halted,
    long LastTick,
    decimal UpperLimit,
    decimal LowerLimit,
    bool AtLimitUp,
    bool AtLimitDown,
    long LenderTotalShares,
    long LenderAvailableShares,
    long LiquidationFrozenUntilTick,
    long MarketCap);

/// <summary>
/// 行情总线：持有 K 线、推进时钟、落盘。
///
/// <para>
/// 【线程模型】服务端 <c>IOnUpdate</c> 每 5 秒跑一次，HTTP 请求走另一条线程。
/// 所有对 <see cref="_file"/> 的读改写都在 <see cref="_sync"/> 里，
/// 路由只读快照、交易只读当前价，不会和 tick 生成打架。
/// </para>
///
/// <para>
/// 【落盘】每生成一个新 tick 就写一次 data/market.json。
/// 文件很小（200 根 × 股票数），用 tmp + Move 保证不会写出半截 JSON。
/// </para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class MarketStore
{
    /// <summary>最多保留多少根 K 线</summary>
    public const int HistoryLimit = 200;

    /// <summary>一次启动最多补算多少 tick，超过就跳跃（约一个月现实时间）</summary>
    private const long MaxCatchUpTicks = 20_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();
    private readonly PriceEngine _engine = new();
    private readonly MarketClock _clock = new();
    private readonly List<StockDefinition> _definitions;
    private readonly Dictionary<string, StockDefinition> _byId;
    private readonly string _marketPath;
    private readonly ISptLogger<MarketStore> _logger;
    private readonly StockMarketConfig _config;

    private MarketFile _file;

    public MarketStore(
        ISptLogger<MarketStore> logger,
        ModHelper modHelper,
        StockMarketConfig config)
    {
        _logger = logger;
        _config = config;

        // ⚠ 不能用 Assembly.Location —— SPT 可能从字节数组加载 mod，
        //   那样 Location 是空串。ModHelper 是官方给 mod 用的路径解析。
        string modFolder = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        _marketPath = Path.Combine(modFolder, "data", "market.json");

        _definitions = LoadDefinitions(Path.Combine(modFolder, "config", "stocks.json"));
        _byId = _definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

        _file = LoadOrCreate();

        // 启动后的补算交给 MarketRunner：必须逐 tick 生成并在每 tick 检查强平，
        // 否则离线期间的强平卖出不会反映到 K 线路径上。
    }

    public IReadOnlyList<StockDefinition> Definitions => _definitions;

    public StockDefinition? GetDefinition(string stockId) =>
        _byId.TryGetValue(stockId, out StockDefinition? def) ? def : null;

    public long CurrentTick(long realUnix) => _clock.TickIndex(realUnix);

    public double MarketSecondsNow() => _clock.MarketSecondsNow();

    public double SecondsToNextTick() => _clock.SecondsToNextTick(MarketClock.NowUnix());

    /// <summary>最近一根 K 线的收盘价，也就是当前成交价。</summary>
    public StockQuote? GetQuote(string stockId)
    {
        lock (_sync)
        {
            if (!_byId.TryGetValue(stockId, out StockDefinition? def))
                return null;

            if (!_file.Stocks.TryGetValue(stockId, out StockRuntime? runtime))
                return null;

            long tick = _clock.TickIndexNow();
            decimal previous = PreviousDayClose(runtime, tick);
            (decimal lower, decimal upper) = DailyBand(runtime, tick);

            return new StockQuote(
                def,
                runtime.LastClose,
                previous,
                runtime.IsHaltedAt(tick),
                tick,
                upper,
                lower,
                IsLimitUp(runtime.LastClose, upper),
                IsLimitDown(runtime.LastClose, lower),
                LenderTotalShares(def),
                LenderAvailableLocked(def, runtime),
                runtime.LiquidationFrozenUntilTick,
                MarketCap(runtime.LastClose, def));
        }
    }

    public IReadOnlyList<StockQuote> GetAllQuotes()
    {
        var result = new List<StockQuote>(_definitions.Count);
        lock (_sync)
        {
            long tick = _clock.TickIndexNow();
            foreach (StockDefinition def in _definitions)
            {
                if (!def.Enabled) continue;
                if (!_file.Stocks.TryGetValue(def.Id, out StockRuntime? runtime)) continue;

                decimal previous = PreviousDayClose(runtime, tick);
                (decimal lower, decimal upper) = DailyBand(runtime, tick);

                result.Add(new StockQuote(
                    def,
                    runtime.LastClose,
                    previous,
                    runtime.IsHaltedAt(tick),
                    tick,
                    upper,
                    lower,
                    IsLimitUp(runtime.LastClose, upper),
                    IsLimitDown(runtime.LastClose, lower),
                    LenderTotalShares(def),
                    LenderAvailableLocked(def, runtime),
                    runtime.LiquidationFrozenUntilTick,
                    MarketCap(runtime.LastClose, def)));
            }
        }
        return result;
    }

    /// <summary>返回最近 N 根 K 线的副本，按 tick 升序。</summary>
    public List<Candle> GetCandles(string stockId, int count)
    {
        lock (_sync)
        {
            if (!_file.Stocks.TryGetValue(stockId, out StockRuntime? runtime))
                return [];

            int take = Math.Clamp(count, 1, HistoryLimit);
            int start = Math.Max(0, runtime.Candles.Count - take);
            return runtime.Candles.GetRange(start, runtime.Candles.Count - start);
        }
    }

    // ══════════════════ 券行 / 熔断 ══════════════════

    /// <summary>券行持有的总股数 = 总股本 × 配置比例。</summary>
    public long LenderTotalShares(StockDefinition def)
    {
        decimal total = Math.Max(0, def.TotalShares) * (decimal)Math.Clamp(_config.LenderPercent, 0.0, 1.0);
        return (long)Math.Floor(total);
    }

    private long LenderAvailableLocked(StockDefinition def, StockRuntime runtime)
    {
        long total = LenderTotalShares(def);
        long lent = Math.Clamp(runtime.LenderLentShares, 0L, total);
        return Math.Max(0L, total - lent);
    }

    /// <summary>当前可借股数。</summary>
    public long LenderAvailable(string stockId)
    {
        lock (_sync)
        {
            if (!_byId.TryGetValue(stockId, out StockDefinition? def)) return 0;
            if (!_file.Stocks.TryGetValue(stockId, out StockRuntime? runtime)) return 0;
            return LenderAvailableLocked(def, runtime);
        }
    }

    /// <summary>借入股票（开空）。券源不足返回 false，不改变任何状态。</summary>
    public bool BorrowShares(string stockId, int shares)
    {
        if (shares <= 0) return false;

        lock (_sync)
        {
            if (!_byId.TryGetValue(stockId, out StockDefinition? def)) return false;
            if (!_file.Stocks.TryGetValue(stockId, out StockRuntime? runtime)) return false;

            long total = LenderTotalShares(def);
            if (runtime.LenderLentShares + shares > total)
                return false;

            runtime.LenderLentShares += shares;
            SaveLocked();
            return true;
        }
    }

    /// <summary>归还券源（平空/强平/转生）。</summary>
    public void ReleaseBorrowedShares(string stockId, int shares)
    {
        if (shares <= 0) return;

        lock (_sync)
        {
            if (!_file.Stocks.TryGetValue(stockId, out StockRuntime? runtime))
                return;

            runtime.LenderLentShares = Math.Max(0L, runtime.LenderLentShares - shares);
            SaveLocked();
        }
    }

    /// <summary>该股票当前是否处于涨跌停熔断（暂停强平）。</summary>
    public bool IsLiquidationFrozen(string stockId)
    {
        lock (_sync)
        {
            if (!_file.Stocks.TryGetValue(stockId, out StockRuntime? runtime))
                return false;
            return runtime.IsLiquidationFrozenAt(_clock.TickIndexNow());
        }
    }

    /// <summary>涨跌停边沿检测：首次触板时开启熔断；持续封板不续期。</summary>
    private void MaybeUpdateCircuitBreaker(
        StockDefinition def, StockRuntime runtime, long tick, decimal lower, decimal upper)
    {
        if (_config.DailyLimitPercent <= 0 || lower == decimal.MinValue)
        {
            runtime.WasAtLimit = false;
            return;
        }

        bool atLimit = runtime.LastClose >= upper || runtime.LastClose <= lower;

        if (atLimit && !runtime.WasAtLimit && _config.CircuitBreakerTicks > 0)
        {
            runtime.LiquidationFrozenUntilTick = tick + _config.CircuitBreakerTicks;
            _logger.Warning(
                $"[StockMarket] {def.Id} 触及涨跌停，强平熔断至 tick {runtime.LiquidationFrozenUntilTick}");
        }

        runtime.WasAtLimit = atLimit;
    }

    private static long MarketCap(decimal price, StockDefinition def)
    {
        decimal cap = Math.Max(0m, price) * Math.Max(0L, def.TotalShares);
        return cap >= long.MaxValue ? long.MaxValue : (long)Math.Round(cap);
    }

    /// <summary>最后已生成的 tick。</summary>
    public long LastTick
    {
        get { lock (_sync) return _file.LastTick; }
    }

    /// <summary>当前现实时间对应的 tick。</summary>
    public long TargetTickNow() => _clock.TickIndexNow();

    /// <summary>
    /// 离线过久时跳档：清空 K 线并把 LastTick 直接推到 target-200，
    /// 避免几万次无意义循环。
    /// </summary>
    public void SkipIfTooFarBehind(long targetTick)
    {
        lock (_sync)
        {
            if (targetTick - _file.LastTick <= MaxCatchUpTicks)
                return;

            _logger.Warning(
                $"[StockMarket] 离线时间过长，跳过 {targetTick - _file.LastTick} 个 tick（跳跃模式）。");

            foreach (StockRuntime runtime in _file.Stocks.Values)
            {
                runtime.Candles.Clear();
            }

            _file.LastTick = Math.Max(0, targetTick - HistoryLimit);
        }
    }

    /// <summary>
    /// 只推进一个 tick（不落盘）。
    ///
    /// 调用方（<see cref="MarketRunner"/>）必须在每个 tick 之后跑一次强平检查，
    /// 因为强平卖出会反过来影响价格；最后再调 <see cref="SaveMarket"/>。
    /// </summary>
    public bool AdvanceOneTick()
    {
        lock (_sync)
        {
            long next = _file.LastTick + 1;
            StepAll(next);
            _file.LastTick = next;
            return true;
        }
    }

    public void SaveMarket()
    {
        lock (_sync)
        {
            SaveLocked();
        }
    }

    /// <summary>一天有多少个 tick（24h / 15min = 96）</summary>
    private static readonly long TicksPerDay = 24 * 3600 / MarketClock.TickSeconds;

    /// <summary>当日涨跌停区间。未启用时返回 decimal 的上下界（等价于不夹）。</summary>
    private (decimal Lower, decimal Upper) DailyBand(StockRuntime runtime, long tick)
    {
        if (_config.DailyLimitPercent <= 0)
            return (decimal.MinValue, decimal.MaxValue);

        decimal reference = PreviousDayClose(runtime, tick);
        if (reference <= 0m)
            return (decimal.MinValue, decimal.MaxValue);

        decimal limit = (decimal)_config.DailyLimitPercent;
        return (reference * (1m - limit), reference * (1m + limit));
    }

    private bool IsLimitUp(decimal price, decimal upper) =>
        _config.DailyLimitPercent > 0 && price >= upper;

    private bool IsLimitDown(decimal price, decimal lower) =>
        _config.DailyLimitPercent > 0 && price <= lower;

    private Candle ClampToBand(Candle candle, StockRuntime runtime, long tick)
    {
        if (_config.DailyLimitPercent <= 0)
            return candle;

        (decimal lower, decimal upper) = DailyBand(runtime, tick);

        decimal close = Math.Clamp(candle.Close, lower, upper);
        decimal high = Math.Clamp(candle.High, lower, upper);
        decimal low = Math.Clamp(candle.Low, lower, upper);

        high = Math.Max(high, Math.Max(candle.Open, close));
        low = Math.Min(low, Math.Min(candle.Open, close));

        return candle with { Close = close, High = high, Low = low };
    }

    /// <summary>
    /// 昨收：上一交易日的最后一根收盘价。
    /// 涨跌幅按"相对昨收"计算，而不是相对上一根 K 线 —— 后者每 2 分钟就刷新一次，
    /// 看不出当天累计的涨跌。
    /// </summary>
    private static decimal PreviousDayClose(StockRuntime runtime, long tick)
    {
        long dayStart = (tick / TicksPerDay) * TicksPerDay;

        for (int i = runtime.Candles.Count - 1; i >= 0; i--)
        {
            if (runtime.Candles[i].Tick < dayStart)
                return runtime.Candles[i].Close;
        }

        // 历史窗口里还没有昨天的数据（新档第一天）：退化为首根开盘价
        return runtime.Candles.Count > 0
            ? runtime.Candles[0].Open
            : runtime.LastClose;
    }

    /// <summary>
    /// 交易冲击：大额买入推高价格，大额卖出压低价格。
    ///
    /// <para>
    /// 冲击按"股数 / BaseVolume"的平方根衰减 —— 这是流动性模型里最常用的
    /// 近似（边际冲击递减）。系数与单笔上限都在 config.json 里。
    /// </para>
    ///
    /// <para>
    /// 冲击直接改 <see cref="StockRuntime.LastClose"/> 并同步修正当前这根 K 线
    /// （Close/High/Low），所以客户端下一次轮询就能看到价格和最后一根蜡烛变化；
    /// 下一根 K 线的 open 也会从这个新价格开始，冲击会延续下去。
    /// </para>
    /// </summary>
    /// <param name="signedShares">买入为正、卖出为负</param>
    /// <returns>冲击后的新价格（找不到股票时返回 0）</returns>
    public decimal ApplyTradeImpact(string stockId, int signedShares)
    {
        if (signedShares == 0)
            return 0m;

        lock (_sync)
        {
            if (!_byId.TryGetValue(stockId, out StockDefinition? def))
                return 0m;
            if (!_file.Stocks.TryGetValue(stockId, out StockRuntime? runtime))
                return 0m;

            if (_config.MarketImpactCoefficient <= 0 || _config.MaxImpactLog <= 0)
                return runtime.LastClose;

            ApplyImpactLocked(def, runtime, signedShares);
            SaveLocked();
            return runtime.LastClose;
        }
    }

    /// <summary>
    /// 分批卖出成交。
    ///
    /// <para>
    /// 大额卖出不能整笔按第一口价成交 —— 那样玩家永远卖在最高点。
    /// 这里按 stepShares 分批：每批用"累计卖出量对应的冲击后价格"成交，
    /// 用梯形法取该批的均价，所以越卖越低（滑点），总冲击仍然符合
    /// <c>exp(-c·sqrt(Q/L))</c> 的整体曲线。
    /// </para>
    ///
    /// <para>
    /// 全程只锁一次、只落盘一次；1 万股按 10 股步进也只是 1000 次纯算术。
    /// </para>
    /// </summary>
    public FillResult SellWithImpact(string stockId, int shares, int stepShares)
    {
        if (shares <= 0)
            return default;

        lock (_sync)
        {
            if (!_byId.TryGetValue(stockId, out StockDefinition? def))
                return default;
            if (!_file.Stocks.TryGetValue(stockId, out StockRuntime? runtime))
                return default;

            decimal basePrice = runtime.LastClose;
            decimal gross = 0m;

            bool impactEnabled = _config.MarketImpactCoefficient > 0 && _config.MaxImpactLog > 0;

            if (!impactEnabled)
            {
                gross = basePrice * shares;
            }
            else
            {
                long tick = _clock.TickIndexNow();
                (decimal lower, decimal upper) = DailyBand(runtime, tick);

                // 兜底：任何历史遗留价格都先夹回当日区间
                basePrice = Math.Clamp(basePrice, lower, upper);

                int step = stepShares > 0 ? Math.Min(stepShares, shares) : shares;
                int sold = 0;
                int remaining = shares;

                while (remaining > 0)
                {
                    int chunk = Math.Min(step, remaining);

                    decimal priceBefore = ImpactedSellPrice(
                        def, basePrice, sold, lower, upper);
                    decimal priceAfter = ImpactedSellPrice(
                        def, basePrice, sold + chunk, lower, upper);

                    // 梯形法：这一批的平均成交价
                    gross += (priceBefore + priceAfter) * 0.5m * chunk;

                    sold += chunk;
                    remaining -= chunk;
                }

                decimal finalPrice = ImpactedSellPrice(def, basePrice, sold, lower, upper);
                runtime.LastClose = finalPrice;
                UpdateLastCandle(runtime, finalPrice);
                MaybeUpdateCircuitBreaker(def, runtime, tick, lower, upper);
            }

            SaveLocked();

            _logger.Debug(
                $"[StockMarket] 卖出成交 {stockId}: {shares} 股，" +
                $"首价 {basePrice:F2} → 末价 {runtime.LastClose:F2}，总额 {gross:F2}");

            return new FillResult(gross, basePrice, runtime.LastClose);
        }
    }

    /// <summary>累计卖出 q 股后的价格（含涨跌停夹取）。</summary>
    private decimal ImpactedSellPrice(
        StockDefinition def, decimal basePrice, long soldShares,
        decimal lower, decimal upper)
    {
        if (soldShares <= 0)
            return basePrice;

        double magnitude = ImpactMagnitude(def, soldShares);

        decimal raw = basePrice * (decimal)Math.Exp(-magnitude);
        return Math.Clamp(PriceEngine.RoundToTick(raw, def), lower, upper);
    }

    /// <summary>
    /// 分批买入成交。
    ///
    /// <para>
    /// 与卖出对称：每买 stepShares 股用一次当时的冲击后价格，均价一定高于第一口价，
    /// 不会"整笔按最低价买入"。budget 是可用资金（含手续费预算），
    /// 不够时返回 null，且**不会修改任何行情状态**。
    /// </para>
    /// </summary>
    public FillResult? BuyWithImpact(
        string stockId, int shares, int stepShares, long budget, decimal feeRate)
    {
        if (shares <= 0 || budget <= 0)
            return null;

        lock (_sync)
        {
            if (!_byId.TryGetValue(stockId, out StockDefinition? def))
                return null;
            if (!_file.Stocks.TryGetValue(stockId, out StockRuntime? runtime))
                return null;

            return BuyFillLocked(def, runtime, shares, stepShares, budget, feeRate);
        }
    }

    /// <summary>
    /// 强平买回：不做预算检查，成交多少算多少。
    /// 结算时的差额由调用方记成玩家欠债（"亏损不会消失"）。
    /// </summary>
    public FillResult CoverWithImpact(string stockId, int shares, int stepShares)
    {
        if (shares <= 0)
            return default;

        lock (_sync)
        {
            if (!_byId.TryGetValue(stockId, out StockDefinition? def))
                return default;
            if (!_file.Stocks.TryGetValue(stockId, out StockRuntime? runtime))
                return default;

            return BuyFillLocked(def, runtime, shares, stepShares, null, 0m) ?? default;
        }
    }

    /// <summary>买入成交的共用实现；budget = null 表示不检查预算（强平买回）。</summary>
    private FillResult? BuyFillLocked(
        StockDefinition def, StockRuntime runtime, int shares, int stepShares,
        long? budget, decimal feeRate)
    {
        decimal basePrice = runtime.LastClose;
        bool impactEnabled = _config.MarketImpactCoefficient > 0 && _config.MaxImpactLog > 0;

        decimal lower = decimal.MinValue;
        decimal upper = decimal.MaxValue;
        long tick = _clock.TickIndexNow();

        if (impactEnabled)
        {
            (lower, upper) = DailyBand(runtime, tick);

            // 兜底：任何历史遗留价格都先夹回当日区间
            basePrice = Math.Clamp(basePrice, lower, upper);
        }

        decimal gross;

        if (!impactEnabled)
        {
            gross = basePrice * shares;
        }
        else
        {
            int step = stepShares > 0 ? Math.Min(stepShares, shares) : shares;
            int bought = 0;
            int remaining = shares;
            gross = 0m;

            while (remaining > 0)
            {
                int chunk = Math.Min(step, remaining);

                decimal priceBefore = ImpactedBuyPrice(def, basePrice, bought, lower, upper);
                decimal priceAfter = ImpactedBuyPrice(def, basePrice, bought + chunk, lower, upper);

                // 梯形法：这一批的平均成交价
                gross += (priceBefore + priceAfter) * 0.5m * chunk;

                bought += chunk;
                remaining -= chunk;
            }
        }

        long roundedGross = (long)Math.Round(gross, MidpointRounding.AwayFromZero);
        long fee = (long)Math.Round(roundedGross * feeRate, MidpointRounding.AwayFromZero);

        if (budget.HasValue && roundedGross + fee > budget.Value)
            return null;

        if (impactEnabled)
        {
            decimal finalPrice = ImpactedBuyPrice(def, basePrice, shares, lower, upper);
            runtime.LastClose = finalPrice;
            UpdateLastCandle(runtime, finalPrice);
            MaybeUpdateCircuitBreaker(def, runtime, tick, lower, upper);
            SaveLocked();
        }

        return new FillResult(gross, basePrice, runtime.LastClose);
    }

    /// <summary>累计买入 q 股后的价格（含涨跌停夹取）。</summary>
    private decimal ImpactedBuyPrice(
        StockDefinition def, decimal basePrice, long boughtShares,
        decimal lower, decimal upper)
    {
        if (boughtShares <= 0)
            return basePrice;

        double magnitude = ImpactMagnitude(def, boughtShares);

        decimal raw = basePrice * (decimal)Math.Exp(magnitude);
        return Math.Clamp(PriceEngine.RoundToTick(raw, def), lower, upper);
    }

    /// <summary>
    /// 冲击幅度 = min(c · (|股数| / BaseVolume)^exponent, MaxImpactLog)。
    /// exponent 默认 0.5（sqrt）—— 抛售规模越大，边际跌幅越小，不是线性下跌。
    /// </summary>
    private double ImpactMagnitude(StockDefinition def, long shares)
    {
        double liquidity = Math.Max(1.0, def.BaseVolume);
        double ratio = Math.Abs((double)shares) / liquidity;
        return Math.Min(
            _config.MarketImpactCoefficient * Math.Pow(ratio, _config.MarketImpactExponent),
            _config.MaxImpactLog);
    }

    /// <summary>冲击核心逻辑（调用方已持有 _sync，并自行 SaveLocked）。</summary>
    private void ApplyImpactLocked(StockDefinition def, StockRuntime runtime, int signedShares)
    {
        // 凹曲线：规模越大单股冲击越小（默认 sqrt），可用 marketImpactExponent 调节
        double magnitude = ImpactMagnitude(def, signedShares);

        double deltaLog = Math.Sign(signedShares) * magnitude;

        long tick = _clock.TickIndexNow();
        (decimal lower, decimal upper) = DailyBand(runtime, tick);

        decimal raw = Math.Clamp(
            runtime.LastClose * (decimal)Math.Exp(deltaLog), lower, upper);
        decimal newClose = Math.Clamp(PriceEngine.RoundToTick(raw, def), lower, upper);

        runtime.LastClose = newClose;
        UpdateLastCandle(runtime, newClose);
    }

    private static void UpdateLastCandle(StockRuntime runtime, decimal newClose)
    {
        if (runtime.Candles.Count == 0)
            return;

        int index = runtime.Candles.Count - 1;
        Candle last = runtime.Candles[index];
        runtime.Candles[index] = last with
        {
            Close = newClose,
            High = Math.Max(last.High, newClose),
            Low = Math.Min(last.Low, newClose)
        };
    }

    /// <summary>
    /// 预留：停牌 N 个 tick。以后接救市机制/玩家对抗时用。
    /// 停牌期间只生成平盘 K 线，交易接口会拒绝。
    /// </summary>
    public bool HaltStock(string stockId, long ticks, string reasonKey)
    {
        lock (_sync)
        {
            if (!_file.Stocks.TryGetValue(stockId, out StockRuntime? runtime))
                return false;

            long tick = _clock.TickIndexNow();
            runtime.HaltedUntilTick = tick + Math.Max(1, ticks);
            runtime.HaltReasonKey = reasonKey;
            SaveLocked();
            return true;
        }
    }

    private void StepAll(long tick)
    {
        foreach (StockDefinition def in _definitions)
        {
            if (!def.Enabled) continue;

            if (!_file.Stocks.TryGetValue(def.Id, out StockRuntime? runtime))
            {
                runtime = new StockRuntime { LastClose = def.InitialPrice };
                _file.Stocks[def.Id] = runtime;
            }

            Candle candle;
            if (runtime.IsHaltedAt(tick))
            {
                // 停牌：平盘、无量
                candle = new Candle
                {
                    Tick = tick,
                    Open = runtime.LastClose,
                    High = runtime.LastClose,
                    Low = runtime.LastClose,
                    Close = runtime.LastClose,
                    Volume = 0
                };
            }
            else
            {
                CandleResult result = _engine.Next(def, runtime, tick);
                candle = result.Candle;
                runtime.VolatilityRegime = result.Regime;
            }

            // 涨跌停：整根 K 线夹在当日区间内
            candle = ClampToBand(candle, runtime, tick);

            runtime.LastClose = candle.Close;
            runtime.Candles.Add(candle);

            (decimal dayLower, decimal dayUpper) = DailyBand(runtime, tick);
            MaybeUpdateCircuitBreaker(def, runtime, tick, dayLower, dayUpper);

            if (runtime.Candles.Count > HistoryLimit)
                runtime.Candles.RemoveRange(0, runtime.Candles.Count - HistoryLimit);
        }
    }

    private MarketFile LoadOrCreate()
    {
        MarketFile file;
        if (File.Exists(_marketPath))
        {
            try
            {
                file = JsonSerializer.Deserialize<MarketFile>(
                           File.ReadAllText(_marketPath), JsonOptions)
                       ?? new MarketFile();
            }
            catch (Exception ex)
            {
                _logger.Error($"[StockMarket] 行情文件损坏，重新生成：{ex.Message}");
                file = new MarketFile();
            }
        }
        else
        {
            file = new MarketFile();
        }

        if (file.ModelVersion != PriceEngine.ModelVersion)
        {
            if (file.ModelVersion != 0)
            {
                _logger.Warning(
                    $"[StockMarket] 行情模型版本 {file.ModelVersion} -> {PriceEngine.ModelVersion}，" +
                    "旧 K 线作废，重新生成。");
            }

            file = new MarketFile { ModelVersion = PriceEngine.ModelVersion };
        }

        if (file.AnchorRealUnix <= 0)
        {
            // 新档：把锚点往前推 200 个 tick，这样启动时 TickIndexNow ≈ 200，
            // 下面是 MarketRunner 首次推进的目标：启动后立刻生成 200 根历史 K 线。
            file.AnchorRealUnix = MarketClock.NowUnix()
                                  - (long)(HistoryLimit * MarketClock.RealSecondsPerTick);
        }

        _clock.AnchorRealUnix = file.AnchorRealUnix;

        if (file.LastTick < 0)
            file.LastTick = 0;

        foreach (StockDefinition def in _definitions)
        {
            if (!file.Stocks.ContainsKey(def.Id))
            {
                file.Stocks[def.Id] = new StockRuntime { LastClose = def.InitialPrice };
            }
        }

        return file;
    }

    private void SaveLocked()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_marketPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temp = _marketPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_file, JsonOptions));
            File.Move(temp, _marketPath, true);
        }
        catch (Exception ex)
        {
            _logger.Error($"[StockMarket] 行情落盘失败：{ex.Message}");
        }
    }

    private List<StockDefinition> LoadDefinitions(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                StocksConfigFile? config = JsonSerializer.Deserialize<StocksConfigFile>(
                    File.ReadAllText(path), JsonOptions);
                if (config?.Stocks is { Count: > 0 })
                    return config.Stocks;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[StockMarket] stocks.json 读取失败：{ex.Message}");
        }

        _logger.Warning("[StockMarket] 未找到 stocks.json，使用内置测试股。");
        return
        [
            new StockDefinition
            {
                Id = "TST-01",
                NameKey = "stockmarket/stock/tst01/name",
                Currency = "RUB",
                InitialPrice = 100m,
                MeanPrice = 100m
            }
        ];
    }
}
