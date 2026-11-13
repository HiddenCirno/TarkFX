using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Server;
using TarkFX.StockMarket.Server.Models;

namespace TarkFX.StockMarket.Server.Core;

/// <summary>一次行情快照，供路由层转成 DTO。</summary>
public sealed record StockQuote(
    StockDefinition Definition,
    decimal Price,
    decimal PreviousClose,
    bool Halted,
    long LastTick);

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

    private MarketFile _file;

    public MarketStore(ISptLogger<MarketStore> logger, ModHelper modHelper)
    {
        _logger = logger;

        // ⚠ 不能用 Assembly.Location —— SPT 可能从字节数组加载 mod，
        //   那样 Location 是空串。ModHelper 是官方给 mod 用的路径解析。
        string modFolder = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        _marketPath = Path.Combine(modFolder, "data", "market.json");

        _definitions = LoadDefinitions(Path.Combine(modFolder, "config", "stocks.json"));
        _byId = _definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

        _file = LoadOrCreate();

        // 启动时立刻补齐（服务端关闭期间行情照走）
        AdvanceToNow();
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

            return new StockQuote(
                def,
                runtime.LastClose,
                previous,
                runtime.IsHaltedAt(tick),
                tick);
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

                result.Add(new StockQuote(def, runtime.LastClose, previous, runtime.IsHaltedAt(tick), tick));
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

    public bool AdvanceToNow()
    {
        long target = _clock.TickIndexNow();
        return AdvanceTo(target);
    }

    /// <summary>
    /// 推进到目标 tick。返回是否真的生成了新 K 线。
    /// 幂等：target 小于等于当前 LastTick 时什么都不做。
    /// </summary>
    public bool AdvanceTo(long targetTick, bool persist = true)
    {
        if (targetTick <= 0)
            return false;

        lock (_sync)
        {
            if (targetTick <= _file.LastTick)
                return false;

            long from = _file.LastTick + 1;

            // 离线太久：走跳跃模式，避免几万次无意义循环
            if (targetTick - from > MaxCatchUpTicks)
            {
                _logger.Warning(
                    $"[StockMarket] 离线时间过长，跳过 {targetTick - from} 个 tick（跳跃模式）。");
                foreach (StockRuntime runtime in _file.Stocks.Values)
                {
                    runtime.Candles.Clear();
                }
                from = targetTick - HistoryLimit;
                if (from < 1) from = 1;
            }

            for (long tick = from; tick <= targetTick; tick++)
            {
                StepAll(tick);
            }

            _file.LastTick = targetTick;

            if (persist)
                SaveLocked();

            return true;
        }
    }

    /// <summary>一天有多少个 tick（24h / 15min = 96）</summary>
    private static readonly long TicksPerDay = 24 * 3600 / MarketClock.TickSeconds;

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

            runtime.LastClose = candle.Close;
            runtime.Candles.Add(candle);

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
            // 下面的 AdvanceToNow 会直接生成 200 根历史 K 线 —— 打开界面就有图看。
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
