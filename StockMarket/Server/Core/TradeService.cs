using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.Commerce;

namespace TarkFX.StockMarket.Server.Core;

public sealed record TradeOutcome(
    bool Success,
    string? Code,
    string? MessageKey,
    decimal? Price = null,
    long? Total = null,
    int? Shares = null);

/// <summary>
/// 买入 / 卖出。
///
/// <para>
/// 【成交口径】按当前最后一根 K 线的 Close 成交。
/// 金额 = round(单价 × 股数)，四舍五入到整数货币单位 ——
/// 和原版商店一致：19.19 美元买 1 个扣 19，买 10 个扣 192（191.9 → 192）。
/// </para>
///
/// <para>
/// 【T+1】按塔科夫时间 24 小时判定，落在批次上。
/// </para>
///
/// <para>
/// 【资金】直接在 profile.Inventory 里扣/加；卖出走系统邮件（卖出的钱不直接进背包）。
/// </para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class TradeService(
    MarketStore market,
    PortfolioStore portfolios,
    StockMarketConfig config,
    ProfileHelper profileHelper,
    SaveServer saveServer,
    ItemHelper itemHelper,
    MailSendService mailSendService,
    ISptLogger<TradeService> logger)
{
    private static readonly Dictionary<string, MongoId> CurrencyTemplates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["RUB"] = Money.ROUBLES,
            ["USD"] = Money.DOLLARS,
            ["EUR"] = Money.EUROS
        };

    public async Task<TradeOutcome> BuyAsync(MongoId sessionId, string stockId, int shares)
    {
        if (shares <= 0)
            return Fail("invalid_amount", "stockmarket/error/invalid_amount");

        StockQuote? quote = market.GetQuote(stockId);
        if (quote is null)
            return Fail("unknown_stock", "stockmarket/error/unknown_stock");
        if (quote.Halted)
            return Fail("halted", "stockmarket/error/halted");

        PmcData? pmc = profileHelper.GetPmcProfile(sessionId);
        if (pmc?.Inventory?.Items is null)
            return Fail("profile_unavailable", "stockmarket/error/profile_unavailable");

        if (!CurrencyTemplates.TryGetValue(quote.Definition.Currency, out MongoId template))
            return Fail("unknown_currency", "stockmarket/error/unknown_currency");

        (long total, long fee) = CalculateCost(quote.Price, shares);

        if (!TryTakeCurrency(pmc, template, total + fee))
            return Fail("insufficient_funds", "stockmarket/error/insufficient_funds");

        long tick = market.CurrentTick(MarketClock.NowUnix());
        portfolios.AddLot(sessionId.ToString(), stockId, shares, quote.Price, tick,
                          market.MarketSecondsNow());

        await saveServer.SaveProfileAsync(sessionId);

        logger.Info(
            $"[StockMarket] {sessionId} 买入 {stockId} {shares} 股 @ {quote.Price}，合计 {total}");

        return new TradeOutcome(true, null, "stockmarket/result/bought", quote.Price, total, shares);
    }

    public async Task<TradeOutcome> SellAsync(MongoId sessionId, string stockId, int shares)
    {
        if (shares <= 0)
            return Fail("invalid_amount", "stockmarket/error/invalid_amount");

        StockQuote? quote = market.GetQuote(stockId);
        if (quote is null)
            return Fail("unknown_stock", "stockmarket/error/unknown_stock");
        if (quote.Halted)
            return Fail("halted", "stockmarket/error/halted");

        string profileId = sessionId.ToString();
        double now = market.MarketSecondsNow();
        int sellable = portfolios.GetSellableShares(profileId, stockId, now);

        if (shares > sellable)
            return Fail("insufficient_shares", "stockmarket/error/insufficient_shares");

        if (!CurrencyTemplates.TryGetValue(quote.Definition.Currency, out MongoId template))
            return Fail("unknown_currency", "stockmarket/error/unknown_currency");

        (long total, long fee) = CalculateCost(quote.Price, shares);
        long proceeds = Math.Max(0, total - fee);

        portfolios.ConsumeSellable(profileId, stockId, shares, now);

        Item? money = CreateCurrencyItem(template, proceeds);
        if (money is null)
            return Fail("currency_template_missing", "stockmarket/error/currency_template_missing");

        mailSendService.SendLocalisedSystemMessageToPlayer(
            sessionId,
            "stockmarket/mail/sell",
            [money],
            null,
            config.MailLifetimeSeconds);

        await saveServer.SaveProfileAsync(sessionId);

        logger.Info(
            $"[StockMarket] {sessionId} 卖出 {stockId} {shares} 股 @ {quote.Price}，回款 {proceeds}");

        return new TradeOutcome(true, null, "stockmarket/result/sold", quote.Price, proceeds, shares);
    }

    /// <summary>
    /// 金额计算。fee 现在是 0，但保留为独立返回值 ——
    /// 以后开手续费只改 config，不动调用点。
    /// </summary>
    private (long Total, long Fee) CalculateCost(decimal unitPrice, int shares)
    {
        // 和原版一致：按总额四舍五入，而不是先取整单价再乘
        long total = RoundCurrency(unitPrice * shares);
        long fee = RoundCurrency(total * config.FeeRate);
        return (total, fee);
    }

    /// <summary>四舍五入到整数货币单位（19.19 → 19；191.9 → 192）。</summary>
    private static long RoundCurrency(decimal value)
    {
        return (long)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    public static long GetBalance(PmcData pmc, MongoId template)
    {
        if (pmc.Inventory?.Items is null)
            return 0;

        long total = 0;
        foreach (Item item in pmc.Inventory.Items)
        {
            if (item.Template != template)
                continue;
            total += (long)(item.Upd?.StackObjectsCount ?? 0);
        }
        return total;
    }

    /// <summary>从背包里按堆扣钱。钱不够返回 false，且不做任何修改。</summary>
    private static bool TryTakeCurrency(PmcData pmc, MongoId template, long amount)
    {
        List<Item>? items = pmc.Inventory?.Items;
        if (items is null || amount <= 0)
            return amount <= 0;

        List<Item> stacks = items
            .Where(item => item.Template == template && (item.Upd?.StackObjectsCount ?? 0) > 0)
            .ToList();

        long available = stacks.Sum(item => (long)(item.Upd!.StackObjectsCount ?? 0));
        if (available < amount)
            return false;

        long remaining = amount;
        foreach (Item stack in stacks)
        {
            if (remaining <= 0)
                break;

            long have = (long)(stack.Upd?.StackObjectsCount ?? 0);
            long take = Math.Min(have, remaining);

            if (take >= have)
                items.Remove(stack);
            else
                stack.Upd!.StackObjectsCount = have - take;

            remaining -= take;
        }

        return true;
    }

    private Item? CreateCurrencyItem(MongoId template, long amount)
    {
        if (amount <= 0)
            return null;

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

    private static TradeOutcome Fail(string code, string messageKey) =>
        new(false, code, messageKey);
}
