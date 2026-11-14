using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Server;
using TarkFX.StockMarket.Server.Models;

namespace TarkFX.StockMarket.Server.Core;

/// <summary>强平扫描用的只读快照。</summary>
public sealed record LeveragedSnapshot(
    string ProfileId,
    string StockId,
    int Leverage,
    int Shares,
    decimal EntryPrice,
    decimal Margin,
    decimal Borrowed);

/// <summary>空头强平扫描用的只读快照。</summary>
public sealed record ShortSnapshot(
    string ProfileId,
    string StockId,
    int Leverage,
    int Shares,
    decimal EntryPrice,
    decimal Proceeds,
    decimal Margin,
    double OpenedMarketSeconds);

/// <summary>转生时归还券行的股数。</summary>
public sealed record ReleasedBorrow(string StockId, int Shares);

/// <summary>分红扫描用的现货批次快照。</summary>
public sealed record SpotLotSnapshot(
    string ProfileId,
    string StockId,
    int Shares,
    long BuyTick);

/// <summary>
/// 持仓存储（按 sessionId）。
///
/// <para>
/// 【为什么按批次记】T+1 必须知道“每一股是什么时候买的”。
/// 只存总量和均价的话，卖出一部分就没法判断哪部分已解锁。
/// 买入生成一个 <see cref="Lot"/>，卖出按 FIFO 只消耗已满 24 塔科夫小时的批次。
/// </para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class PortfolioStore
{
    /// <summary>T+1：24 小时塔科夫时间（现实约 3.43 小时）</summary>
    public const double TPlusOneMarketSeconds = 24 * 3600;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly ISptLogger<PortfolioStore> _logger;
    private readonly StockMarketConfig _config;

    private PortfolioFile _file;

    /// <summary>Debug 模式下 T+1 直接归零。</summary>
    public double EffectiveTPlusOneSeconds =>
        _config.DebugMode ? 0.0 : TPlusOneMarketSeconds;

    public PortfolioStore(
        ISptLogger<PortfolioStore> logger,
        ModHelper modHelper,
        StockMarketConfig config)
    {
        _logger = logger;
        _config = config;
        string modFolder = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        _path = Path.Combine(modFolder, "data", "portfolios.json");
        _file = Load();
    }

    public Position GetPosition(string profileId, string stockId)
    {
        lock (_sync)
        {
            return GetOrCreatePosition(_file, profileId, stockId);
        }
    }

    /// <summary>该股当前持仓总股数。</summary>
    public int GetShares(string profileId, string stockId)
    {
        lock (_sync)
        {
            Position position = GetOrCreatePosition(_file, profileId, stockId);
            return position.Lots.Sum(lot => lot.Shares);
        }
    }

    /// <summary>已满 T+1、现在可以卖的股数。</summary>
    public int GetSellableShares(string profileId, string stockId, double nowMarketSeconds)
    {
        lock (_sync)
        {
            return SellableLots(GetOrCreatePosition(_file, profileId, stockId), nowMarketSeconds)
                .Sum(lot => lot.Shares);
        }
    }

    public Lot? NextSellableLot(string profileId, string stockId, double nowMarketSeconds)
    {
        lock (_sync)
        {
            return SellableLots(GetOrCreatePosition(_file, profileId, stockId), nowMarketSeconds)
                .FirstOrDefault();
        }
    }

    /// <summary>总股数与加权平均成本。</summary>
    public (int Shares, decimal AvgCost) GetHolding(string profileId, string stockId)
    {
        lock (_sync)
        {
            Position position = GetOrCreatePosition(_file, profileId, stockId);
            int shares = position.Lots.Sum(lot => lot.Shares);
            if (shares <= 0)
                return (0, 0m);

            decimal cost = position.Lots.Sum(lot => lot.Price * lot.Shares);
            return (shares, cost / shares);
        }
    }

    /// <summary>下一批解锁还要多少塔科夫秒；0 表示没有锁定批次。</summary>
    public double GetNextUnlockIn(string profileId, string stockId, double nowMarketSeconds)
    {
        lock (_sync)
        {
            Position position = GetOrCreatePosition(_file, profileId, stockId);
            double threshold = EffectiveTPlusOneSeconds;
            double best = double.MaxValue;

            foreach (Lot lot in position.Lots)
            {
                if (lot.Shares <= 0) continue;
                double remaining = threshold - (nowMarketSeconds - lot.BuyMarketSeconds);
                if (remaining > 0 && remaining < best)
                    best = remaining;
            }

            return best == double.MaxValue ? 0 : best;
        }
    }

    public void AddLot(string profileId, string stockId, int shares, decimal price,
                       long tick, double nowMarketSeconds)
    {
        lock (_sync)
        {
            Position position = GetOrCreatePosition(_file, profileId, stockId);
            position.Lots.Add(new Lot
            {
                Shares = shares,
                Price = price,
                BuyTick = tick,
                BuyMarketSeconds = nowMarketSeconds
            });
            SaveLocked();
        }
    }

    /// <summary>
    /// 按 FIFO 消耗已解锁的批次。调用方必须先用
    /// <see cref="GetSellableShares"/> 校验数量。
    /// </summary>
    public void ConsumeSellable(string profileId, string stockId, int shares, double nowMarketSeconds)
    {
        lock (_sync)
        {
            Position position = GetOrCreatePosition(_file, profileId, stockId);
            int remaining = shares;

            foreach (Lot lot in SellableLots(position, nowMarketSeconds).ToList())
            {
                if (remaining <= 0) break;
                int take = Math.Min(lot.Shares, remaining);
                lot.Shares -= take;
                remaining -= take;
            }

            position.Lots.RemoveAll(lot => lot.Shares <= 0);
            SaveLocked();
        }
    }

    private List<Lot> SellableLots(Position position, double nowMarketSeconds)
    {
        double threshold = EffectiveTPlusOneSeconds;

        return position.Lots
            .Where(lot => lot.Shares > 0 &&
                          nowMarketSeconds - lot.BuyMarketSeconds >= threshold)
            .OrderBy(lot => lot.BuyMarketSeconds)
            .ToList();
    }

    private static Position GetOrCreatePosition(PortfolioFile file, string profileId, string stockId)
    {
        Portfolio portfolio = GetOrCreatePortfolio(file, profileId);

        if (!portfolio.Positions.TryGetValue(stockId, out Position? position))
        {
            position = new Position();
            portfolio.Positions[stockId] = position;
        }

        return position;
    }

    private static Portfolio GetOrCreatePortfolio(PortfolioFile file, string profileId)
    {
        if (!file.Profiles.TryGetValue(profileId, out Portfolio? portfolio))
        {
            portfolio = new Portfolio();
            file.Profiles[profileId] = portfolio;
        }

        return portfolio;
    }

    // ══════════════════ 杠杆持仓 ══════════════════

    public LeveragedPosition? GetLeveraged(string profileId, string stockId)
    {
        lock (_sync)
        {
            if (!_file.Profiles.TryGetValue(profileId, out Portfolio? portfolio))
                return null;

            if (!portfolio.Leveraged.TryGetValue(stockId, out LeveragedPosition? position))
                return null;

            return new LeveragedPosition
            {
                Leverage = position.Leverage,
                Shares = position.Shares,
                EntryPrice = position.EntryPrice,
                Margin = position.Margin,
                Borrowed = position.Borrowed,
                OpenedTick = position.OpenedTick,
                OpenedMarketSeconds = position.OpenedMarketSeconds
            };
        }
    }

    public bool HasLeveraged(string profileId, string stockId)
    {
        lock (_sync)
        {
            return _file.Profiles.TryGetValue(profileId, out Portfolio? portfolio)
                   && portfolio.Leveraged.ContainsKey(stockId);
        }
    }

    public void OpenLeveraged(
        string profileId, string stockId, int leverage, int shares,
        decimal entryPrice, decimal margin, decimal borrowed,
        long tick, double nowMarketSeconds)
    {
        lock (_sync)
        {
            Portfolio portfolio = GetOrCreatePortfolio(_file, profileId);
            portfolio.Leveraged[stockId] = new LeveragedPosition
            {
                Leverage = leverage,
                Shares = shares,
                EntryPrice = entryPrice,
                Margin = margin,
                Borrowed = borrowed,
                OpenedTick = tick,
                OpenedMarketSeconds = nowMarketSeconds
            };
            SaveLocked();
        }
    }

    public bool RemoveLeveraged(string profileId, string stockId)
    {
        lock (_sync)
        {
            if (!_file.Profiles.TryGetValue(profileId, out Portfolio? portfolio))
                return false;

            if (!portfolio.Leveraged.Remove(stockId))
                return false;

            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// 按比例减持杠杆多仓（部分平仓）。调用方负责算好本次对应的
    /// margin / borrowed 份额；剩余股数为 0 时应改用 <see cref="RemoveLeveraged"/>。
    /// </summary>
    public bool ReduceLeveraged(
        string profileId, string stockId, int shares,
        decimal marginPart, decimal borrowedPart)
    {
        lock (_sync)
        {
            if (!_file.Profiles.TryGetValue(profileId, out Portfolio? portfolio))
                return false;

            if (!portfolio.Leveraged.TryGetValue(stockId, out LeveragedPosition? position))
                return false;

            if (shares <= 0 || shares >= position.Shares)
                return false;

            position.Shares -= shares;
            position.Margin = Math.Max(0m, position.Margin - marginPart);
            position.Borrowed = Math.Max(0m, position.Borrowed - borrowedPart);

            SaveLocked();
            return true;
        }
    }

    /// <summary>所有玩家的杠杆持仓快照（用于强平扫描，调用时不在锁内）。</summary>
    public List<LeveragedSnapshot> GetOpenLeveragedSnapshot()
    {
        lock (_sync)
        {
            var result = new List<LeveragedSnapshot>();

            foreach (KeyValuePair<string, Portfolio> profile in _file.Profiles)
            {
                foreach (KeyValuePair<string, LeveragedPosition> position in profile.Value.Leveraged)
                {
                    result.Add(new LeveragedSnapshot(
                        profile.Key,
                        position.Key,
                        position.Value.Leverage,
                        position.Value.Shares,
                        position.Value.EntryPrice,
                        position.Value.Margin,
                        position.Value.Borrowed));
                }
            }

            return result;
        }
    }

    /// <summary>所有玩家的现货批次快照（分红扫描用，调用时不在锁内）。</summary>
    public List<SpotLotSnapshot> GetSpotLotsSnapshot()
    {
        lock (_sync)
        {
            var result = new List<SpotLotSnapshot>();

            foreach (KeyValuePair<string, Portfolio> profile in _file.Profiles)
            {
                foreach (KeyValuePair<string, Position> position in profile.Value.Positions)
                {
                    foreach (Lot lot in position.Value.Lots)
                    {
                        if (lot.Shares > 0)
                        {
                            result.Add(new SpotLotSnapshot(
                                profile.Key, position.Key, lot.Shares, lot.BuyTick));
                        }
                    }
                }
            }

            return result;
        }
    }

    // ══════════════════ 做空 ══════════════════

    public ShortPosition? GetShort(string profileId, string stockId)
    {
        lock (_sync)
        {
            if (!_file.Profiles.TryGetValue(profileId, out Portfolio? portfolio))
                return null;

            if (!portfolio.Shorts.TryGetValue(stockId, out ShortPosition? position))
                return null;

            return new ShortPosition
            {
                Leverage = position.Leverage,
                Shares = position.Shares,
                EntryPrice = position.EntryPrice,
                Proceeds = position.Proceeds,
                Margin = position.Margin,
                OpenedTick = position.OpenedTick,
                OpenedMarketSeconds = position.OpenedMarketSeconds
            };
        }
    }

    public bool HasShort(string profileId, string stockId)
    {
        lock (_sync)
        {
            return _file.Profiles.TryGetValue(profileId, out Portfolio? portfolio)
                   && portfolio.Shorts.ContainsKey(stockId);
        }
    }

    public void OpenShort(
        string profileId, string stockId, int leverage, int shares,
        decimal entryPrice, decimal proceeds, decimal margin,
        long tick, double nowMarketSeconds)
    {
        lock (_sync)
        {
            Portfolio portfolio = GetOrCreatePortfolio(_file, profileId);
            portfolio.Shorts[stockId] = new ShortPosition
            {
                Leverage = leverage,
                Shares = shares,
                EntryPrice = entryPrice,
                Proceeds = proceeds,
                Margin = margin,
                OpenedTick = tick,
                OpenedMarketSeconds = nowMarketSeconds
            };
            SaveLocked();
        }
    }

    public bool RemoveShort(string profileId, string stockId)
    {
        lock (_sync)
        {
            if (!_file.Profiles.TryGetValue(profileId, out Portfolio? portfolio))
                return false;

            if (!portfolio.Shorts.Remove(stockId))
                return false;

            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// 按比例减持空头（部分平空）。调用方负责算好本次对应的
    /// margin / proceeds 份额；剩余股数为 0 时应改用 <see cref="RemoveShort"/>。
    /// </summary>
    public bool ReduceShort(
        string profileId, string stockId, int shares,
        decimal marginPart, decimal proceedsPart)
    {
        lock (_sync)
        {
            if (!_file.Profiles.TryGetValue(profileId, out Portfolio? portfolio))
                return false;

            if (!portfolio.Shorts.TryGetValue(stockId, out ShortPosition? position))
                return false;

            if (shares <= 0 || shares >= position.Shares)
                return false;

            position.Shares -= shares;
            position.Margin = Math.Max(0m, position.Margin - marginPart);
            position.Proceeds = Math.Max(0m, position.Proceeds - proceedsPart);

            SaveLocked();
            return true;
        }
    }

    /// <summary>所有玩家的空头快照（用于强平扫描，调用时不在锁内）。</summary>
    public List<ShortSnapshot> GetOpenShortsSnapshot()
    {
        lock (_sync)
        {
            var result = new List<ShortSnapshot>();

            foreach (KeyValuePair<string, Portfolio> profile in _file.Profiles)
            {
                foreach (KeyValuePair<string, ShortPosition> position in profile.Value.Shorts)
                {
                    result.Add(new ShortSnapshot(
                        profile.Key,
                        position.Key,
                        position.Value.Leverage,
                        position.Value.Shares,
                        position.Value.EntryPrice,
                        position.Value.Proceeds,
                        position.Value.Margin,
                        position.Value.OpenedMarketSeconds));
                }
            }

            return result;
        }
    }

    // ══════════════════ 欠债 ══════════════════

    public decimal GetDebt(string profileId, string currency)
    {
        lock (_sync)
        {
            if (!_file.Profiles.TryGetValue(profileId, out Portfolio? portfolio))
                return 0m;

            return portfolio.Debts.TryGetValue(currency, out decimal debt)
                ? Math.Max(0m, debt)
                : 0m;
        }
    }

    public Dictionary<string, decimal> GetDebts(string profileId)
    {
        lock (_sync)
        {
            var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            if (!_file.Profiles.TryGetValue(profileId, out Portfolio? portfolio))
                return result;

            foreach (KeyValuePair<string, decimal> entry in portfolio.Debts)
            {
                if (entry.Value > 0m)
                    result[entry.Key] = entry.Value;
            }

            return result;
        }
    }

    public bool HasDebt(string profileId)
    {
        lock (_sync)
        {
            if (!_file.Profiles.TryGetValue(profileId, out Portfolio? portfolio))
                return false;

            return portfolio.Debts.Values.Any(value => value > 0m);
        }
    }

    public void AddDebt(string profileId, string currency, decimal amount)
    {
        if (amount <= 0m)
            return;

        lock (_sync)
        {
            Portfolio portfolio = GetOrCreatePortfolio(_file, profileId);
            portfolio.Debts.TryGetValue(currency, out decimal current);
            portfolio.Debts[currency] = current + amount;
            SaveLocked();
        }
    }

    /// <summary>还款；返回剩余欠债。负数会被夹成 0。</summary>
    public decimal ReduceDebt(string profileId, string currency, decimal amount)
    {
        lock (_sync)
        {
            if (!_file.Profiles.TryGetValue(profileId, out Portfolio? portfolio))
                return 0m;

            if (!portfolio.Debts.TryGetValue(currency, out decimal current))
                return 0m;

            decimal left = Math.Max(0m, current - Math.Max(0m, amount));
            if (left <= 0m)
                portfolio.Debts.Remove(currency);
            else
                portfolio.Debts[currency] = left;

            SaveLocked();
            return left;
        }
    }

    // ══════════════════ 持股合计 / 转生 ══════════════════

    /// <summary>现货 + 杠杆多 + 空头合计股数（个人持仓上限用）。</summary>
    public int GetTotalShares(string profileId, string stockId)
    {
        lock (_sync)
        {
            if (!_file.Profiles.TryGetValue(profileId, out Portfolio? portfolio))
                return 0;

            int total = 0;

            if (portfolio.Positions.TryGetValue(stockId, out Position? position))
                total += position.Lots.Sum(lot => lot.Shares);

            if (portfolio.Leveraged.TryGetValue(stockId, out LeveragedPosition? leveraged))
                total += leveraged.Shares;

            if (portfolio.Shorts.TryGetValue(stockId, out ShortPosition? shortPosition))
                total += shortPosition.Shares;

            return Math.Max(0, total);
        }
    }

    /// <summary>
    /// 转生平账：清空全部持仓、杠杆、空头与欠债，不做任何行情/邮件结算。
    /// 返回需要归还给券行的股数。
    /// </summary>
    public List<ReleasedBorrow> ResetProfile(string profileId)
    {
        lock (_sync)
        {
            var released = new List<ReleasedBorrow>();

            if (!_file.Profiles.TryGetValue(profileId, out Portfolio? portfolio))
                return released;

            foreach (KeyValuePair<string, ShortPosition> entry in portfolio.Shorts)
            {
                if (entry.Value.Shares > 0)
                    released.Add(new ReleasedBorrow(entry.Key, entry.Value.Shares));
            }

            portfolio.Positions.Clear();
            portfolio.Leveraged.Clear();
            portfolio.Shorts.Clear();
            portfolio.Debts.Clear();

            SaveLocked();
            return released;
        }
    }

    private PortfolioFile Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<PortfolioFile>(
                           File.ReadAllText(_path), JsonOptions)
                       ?? new PortfolioFile();
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[StockMarket] 持仓文件损坏，重新生成：{ex.Message}");
        }
        return new PortfolioFile();
    }

    private void SaveLocked()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_file, JsonOptions));
            File.Move(temp, _path, true);
        }
        catch (Exception ex)
        {
            _logger.Error($"[StockMarket] 持仓落盘失败：{ex.Message}");
        }
    }
}
