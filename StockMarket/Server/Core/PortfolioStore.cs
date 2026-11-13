using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Server;
using TarkFX.StockMarket.Server.Models;

namespace TarkFX.StockMarket.Server.Core;

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

    private PortfolioFile _file;

    public PortfolioStore(ISptLogger<PortfolioStore> logger, ModHelper modHelper)
    {
        _logger = logger;
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
            double best = double.MaxValue;

            foreach (Lot lot in position.Lots)
            {
                if (lot.Shares <= 0) continue;
                double remaining = TPlusOneMarketSeconds - (nowMarketSeconds - lot.BuyMarketSeconds);
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

    private static List<Lot> SellableLots(Position position, double nowMarketSeconds)
    {
        return position.Lots
            .Where(lot => lot.Shares > 0 &&
                          nowMarketSeconds - lot.BuyMarketSeconds >= TPlusOneMarketSeconds)
            .OrderBy(lot => lot.BuyMarketSeconds)
            .ToList();
    }

    private static Position GetOrCreatePosition(PortfolioFile file, string profileId, string stockId)
    {
        if (!file.Profiles.TryGetValue(profileId, out Portfolio? portfolio))
        {
            portfolio = new Portfolio();
            file.Profiles[profileId] = portfolio;
        }

        if (!portfolio.Positions.TryGetValue(stockId, out Position? position))
        {
            position = new Position();
            portfolio.Positions[stockId] = position;
        }

        return position;
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
