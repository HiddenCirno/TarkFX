using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services.Commerce;

namespace TarkFX.StockMarket.Server.Core;

/// <summary>
/// 分红：现货批次每持满 7 个塔科夫日（672 tick，含第一次周期）支付一次。
///
/// <para>
/// 每个批次在自己的 7 天周年 tick 上判断一次：
/// <code>
/// 每股分红 = min(现价 − 发行价, 发行价 × 10%)
/// 总额     = 持股数 × 每股分红
/// </code>
/// 现价 ≤ 发行价时本次不分红。只统计现货批次（杠杆多/空头不参与），
/// 分红通过系统邮件发放，币种跟随股票。
/// </para>
///
/// <para>
/// 由 <see cref="MarketRunner"/> 在每个 tick 推进后调用，因此离线补算时
/// 也会在历史 tick 上逐次结算，而不是补算结束后一次性发。
/// </para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class DividendService(
    MarketStore market,
    PortfolioStore portfolios,
    ItemHelper itemHelper,
    MailSendService mailSendService,
    StockMarketConfig config,
    ISptLogger<DividendService> logger)
{
    /// <summary>7 个塔科夫日 = 7 × 96 tick = 672 tick。</summary>
    public const long TicksPerCycle = 7 * (24 * 3600 / MarketClock.TickSeconds);

    /// <summary>每股分红上限 = 发行价的 10%。</summary>
    private const decimal MaxDividendPerSharePercent = 0.10m;

    public void ProcessTick(long tick)
    {
        if (tick <= 0)
            return;

        List<SpotLotSnapshot> lots = portfolios.GetSpotLotsSnapshot();
        if (lots.Count == 0)
            return;

        var sharesByKey = new Dictionary<(string ProfileId, string StockId), int>();
        var quotes = new Dictionary<string, StockQuote?>(StringComparer.OrdinalIgnoreCase);
        var debtBlocked = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (SpotLotSnapshot lot in lots)
        {
            long elapsed = tick - lot.BuyTick;
            if (elapsed <= 0 || elapsed % TicksPerCycle != 0)
                continue;

            // 欠债期间持仓冻结，分红同样不发（当期作废，不顺延）
            if (!debtBlocked.TryGetValue(lot.ProfileId, out bool hasDebt))
            {
                hasDebt = portfolios.HasDebt(lot.ProfileId);
                debtBlocked[lot.ProfileId] = hasDebt;
            }

            if (hasDebt)
                continue;

            if (!quotes.TryGetValue(lot.StockId, out StockQuote? quote))
            {
                quote = market.GetQuote(lot.StockId);
                quotes[lot.StockId] = quote;
            }

            if (quote is null || quote.Price <= quote.Definition.InitialPrice)
                continue;

            var key = (lot.ProfileId, lot.StockId);
            sharesByKey.TryGetValue(key, out int shares);
            sharesByKey[key] = shares + lot.Shares;
        }

        foreach (KeyValuePair<(string ProfileId, string StockId), int> entry in sharesByKey)
        {
            StockQuote? quote = quotes[entry.Key.StockId];
            if (quote is null)
                continue;

            decimal perShare = Math.Min(
                quote.Price - quote.Definition.InitialPrice,
                quote.Definition.InitialPrice * MaxDividendPerSharePercent);

            if (perShare <= 0m)
                continue;

            long amount = (long)Math.Round(
                entry.Value * perShare, MidpointRounding.AwayFromZero);

            if (amount <= 0)
                continue;

            Item? money = CreateCurrencyItem(quote.Definition.Currency, amount);
            if (money is null)
                continue;

            try
            {
                mailSendService.SendLocalisedSystemMessageToPlayer(
                    new MongoId(entry.Key.ProfileId),
                    "stockmarket/mail/dividend",
                    [money],
                    null,
                    config.MailLifetimeSeconds);

                logger.Info(
                    $"[StockMarket] 分红 {entry.Key.ProfileId} / {entry.Key.StockId}: " +
                    $"{entry.Value} 股 × {perShare:F2} = {amount}");
            }
            catch (Exception ex)
            {
                logger.Error($"[StockMarket] 分红邮件发送失败：{ex.Message}");
            }
        }
    }

    private Item? CreateCurrencyItem(string currency, long amount)
    {
        if (amount <= 0)
            return null;

        MongoId template = currency switch
        {
            "USD" => Money.DOLLARS,
            "EUR" => Money.EUROS,
            _ => Money.ROUBLES
        };

        KeyValuePair<bool, TemplateItem?> result = itemHelper.GetItem(template);
        if (!result.Key || result.Value is null)
            return null;

        Item item = new()
        {
            Id = new MongoId(),
            Template = template,
            Upd = itemHelper.GenerateUpdForItem(result.Value)
        };
        item.Upd.StackObjectsCount = amount;
        return item;
    }
}
