using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.Commerce;
using TarkFX.StockMarket.Server.Models;

namespace TarkFX.StockMarket.Server.Core;

public sealed record TradeOutcome(
    bool Success,
    string? Code,
    string? MessageKey,
    decimal? Price = null,
    long? Total = null,
    int? Shares = null);

/// <summary>
/// 买入 / 卖出 / 做空 / 还款。
///
/// <para>
/// 【资金口径】所有"从玩家身上扣钱"的操作都通过 <see cref="InventoryHelper"/> 完成，
/// 并且把变化写进 <see cref="ItemEventRouterResponse.ProfileChanges"/> ——
/// 这条响应会经由游戏原生的 item/moving 队列回到客户端，仓库 UI 与服务器保持一致。
/// 不能再直接改 <c>pmc.Inventory.Items</c> 后只返回自定义 JSON。
/// </para>
///
/// <para>
/// 【价格口径】按当前 K 线收盘价分批成交，金额四舍五入到整数货币单位。
/// 卖出/平仓回款走系统邮件；做空的卖出款冻结在仓位里，平仓后才结算。
/// </para>
///
/// <para>
/// 【T+1】现货、杠杆多、空头都按 24 小时塔科夫时间锁定；强平可绕过。
/// </para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class TradeService(
    MarketStore market,
    PortfolioStore portfolios,
    StockMarketConfig config,
    RaidStateTracker raidStates,
    SaveServer saveServer,
    ItemHelper itemHelper,
    InventoryHelper inventoryHelper,
    MailSendService mailSendService,
    LiquidationService liquidation,
    ISptLogger<TradeService> logger)
{
    public const int MinLeverage = 1;
    public const int MaxLeverage = 100;

    private static readonly Dictionary<string, MongoId> CurrencyTemplates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["RUB"] = Money.ROUBLES,
            ["USD"] = Money.DOLLARS,
            ["EUR"] = Money.EUROS
        };

    // ══════════════════ 入口 ══════════════════

    public async Task<TradeOutcome> BuyAsync(
        PmcData pmc, MongoId sessionId, string stockId, int shares, int leverage,
        ItemEventRouterResponse output)
    {
        if (leverage > 1)
            return await OpenLeveragedAsync(pmc, sessionId, stockId, shares, leverage, output);

        return await SpotBuyAsync(pmc, sessionId, stockId, shares, output);
    }

    public async Task<TradeOutcome> SellAsync(
        PmcData pmc, MongoId sessionId, string stockId, int shares, int leverage,
        ItemEventRouterResponse output)
    {
        if (leverage > 1)
            return await CloseLeveragedAsync(pmc, sessionId, stockId, output);

        return await SpotSellAsync(pmc, sessionId, stockId, shares, output);
    }

    // ══════════════════ 现货 ══════════════════

    private async Task<TradeOutcome> SpotBuyAsync(
        PmcData pmc, MongoId sessionId, string stockId, int shares,
        ItemEventRouterResponse output)
    {
        if (shares <= 0)
            return Fail("invalid_amount", "stockmarket/error/invalid_amount");

        StockQuote? quote = market.GetQuote(stockId);
        if (quote is null)
            return Fail("unknown_stock", "stockmarket/error/unknown_stock");
        if (quote.Halted)
            return Fail("halted", "stockmarket/error/halted");

        TradeOutcome? blocked = CheckTradable(pmc, sessionId, out string profileId);
        if (blocked is not null) return blocked;

        if (portfolios.GetTotalShares(profileId, stockId) + shares > config.MaxHoldingShares)
            return Fail("max_holding", "stockmarket/error/max_holding");

        if (!CurrencyTemplates.TryGetValue(quote.Definition.Currency, out MongoId template))
            return Fail("unknown_currency", "stockmarket/error/unknown_currency");

        long available = GetBalance(pmc, template);

        // 买入不收手续费（手续费只对卖出收取）
        FillResult? fill = market.BuyWithImpact(
            stockId, shares, config.SellStepShares, available, 0m);

        if (fill is null)
            return Fail("insufficient_funds", "stockmarket/error/insufficient_funds");

        long total = RoundCurrency(fill.Value.Gross);

        if (!TryRemoveMoney(pmc, sessionId, template, total, output))
            return Fail("insufficient_funds", "stockmarket/error/insufficient_funds");

        decimal averagePrice = shares > 0 ? fill.Value.Gross / shares : quote.Price;

        long tick = market.CurrentTick(MarketClock.NowUnix());
        portfolios.AddLot(profileId, stockId, shares, averagePrice, tick,
                          market.MarketSecondsNow());

        liquidation.CheckAll();
        await saveServer.SaveProfileAsync(sessionId);

        logger.Info(
            $"[StockMarket] {sessionId} 买入 {stockId} {shares} 股 @ 均价 {averagePrice:F2}，合计 {total}");

        return new TradeOutcome(true, null, "stockmarket/result/bought", averagePrice, total, shares);
    }

    private async Task<TradeOutcome> SpotSellAsync(
        PmcData pmc, MongoId sessionId, string stockId, int shares,
        ItemEventRouterResponse output)
    {
        if (shares <= 0)
            return Fail("invalid_amount", "stockmarket/error/invalid_amount");

        StockQuote? quote = market.GetQuote(stockId);
        if (quote is null)
            return Fail("unknown_stock", "stockmarket/error/unknown_stock");
        if (quote.Halted)
            return Fail("halted", "stockmarket/error/halted");

        TradeOutcome? blocked = CheckTradable(pmc, sessionId, out string profileId);
        if (blocked is not null) return blocked;

        double now = market.MarketSecondsNow();
        int sellable = portfolios.GetSellableShares(profileId, stockId, now);

        if (shares > sellable)
            return Fail("insufficient_shares", "stockmarket/error/insufficient_shares");

        if (!CurrencyTemplates.TryGetValue(quote.Definition.Currency, out MongoId template))
            return Fail("unknown_currency", "stockmarket/error/unknown_currency");

        portfolios.ConsumeSellable(profileId, stockId, shares, now);

        FillResult fill = market.SellWithImpact(stockId, shares, config.SellStepShares);

        long gross = RoundCurrency(fill.Gross);
        long fee = RoundCurrency(gross * config.FeeRate);
        long proceeds = Math.Max(0, gross - fee);

        liquidation.CheckAll();

        Item? money = CreateCurrencyItem(template, proceeds);
        if (money is null)
            return Fail("currency_template_missing", "stockmarket/error/currency_template_missing");

        mailSendService.SendLocalisedSystemMessageToPlayer(
            sessionId, "stockmarket/mail/sell", [money], null, config.MailLifetimeSeconds);

        await saveServer.SaveProfileAsync(sessionId);

        logger.Info(
            $"[StockMarket] {sessionId} 卖出 {stockId} {shares} 股 @ {quote.Price}，回款 {proceeds}");

        decimal averagePrice = shares > 0 ? fill.Gross / shares : quote.Price;
        return new TradeOutcome(true, null, "stockmarket/result/sold", averagePrice, proceeds, shares);
    }

    // ══════════════════ 杠杆做多 ══════════════════

    private async Task<TradeOutcome> OpenLeveragedAsync(
        PmcData pmc, MongoId sessionId, string stockId, int shares, int leverage,
        ItemEventRouterResponse output)
    {
        if (leverage < 2 || leverage > MaxLeverage)
            return Fail("invalid_leverage", "stockmarket/error/invalid_leverage");

        if (shares <= 0)
            return Fail("invalid_amount", "stockmarket/error/invalid_amount");

        StockQuote? quote = market.GetQuote(stockId);
        if (quote is null)
            return Fail("unknown_stock", "stockmarket/error/unknown_stock");
        if (quote.Halted)
            return Fail("halted", "stockmarket/error/halted");

        TradeOutcome? blocked = CheckTradable(pmc, sessionId, out string profileId);
        if (blocked is not null) return blocked;

        if (portfolios.HasLeveraged(profileId, stockId))
            return Fail("leveraged_exists", "stockmarket/error/leveraged_exists");

        if (portfolios.GetTotalShares(profileId, stockId) + shares > config.MaxHoldingShares)
            return Fail("max_holding", "stockmarket/error/max_holding");

        if (!CurrencyTemplates.TryGetValue(quote.Definition.Currency, out MongoId template))
            return Fail("unknown_currency", "stockmarket/error/unknown_currency");

        long available = GetBalance(pmc, template);
        long budget = available > long.MaxValue / leverage
            ? long.MaxValue
            : available * leverage;

        FillResult? fill = market.BuyWithImpact(
            stockId, shares, config.SellStepShares, budget, 0m);

        if (fill is null)
            return Fail("insufficient_funds", "stockmarket/error/insufficient_funds");

        decimal notional = fill.Value.Gross;
        long margin = (long)Math.Ceiling(notional / leverage);
        if (margin <= 0)
            return Fail("invalid_amount", "stockmarket/error/invalid_amount");

        if (!TryRemoveMoney(pmc, sessionId, template, margin, output))
            return Fail("insufficient_funds", "stockmarket/error/insufficient_funds");

        decimal borrowed = notional - margin;
        decimal entryPrice = shares > 0 ? notional / shares : quote.Price;
        long tick = market.CurrentTick(MarketClock.NowUnix());

        portfolios.OpenLeveraged(
            profileId, stockId, leverage, shares, entryPrice, margin, borrowed,
            tick, market.MarketSecondsNow());

        liquidation.CheckAll();
        await saveServer.SaveProfileAsync(sessionId);

        logger.Info(
            $"[StockMarket] {sessionId} 开 {leverage}x {stockId} {shares} 股 " +
            $"@ 均价 {entryPrice:F2}，保证金 {margin}，借入 {borrowed:F2}");

        return new TradeOutcome(
            true, null, "stockmarket/result/opened", entryPrice, margin, shares);
    }

    private async Task<TradeOutcome> CloseLeveragedAsync(
        PmcData pmc, MongoId sessionId, string stockId, ItemEventRouterResponse output)
    {
        StockQuote? quote = market.GetQuote(stockId);
        if (quote is null)
            return Fail("unknown_stock", "stockmarket/error/unknown_stock");
        if (quote.Halted)
            return Fail("halted", "stockmarket/error/halted");

        TradeOutcome? blocked = CheckTradable(pmc, sessionId, out string profileId);
        if (blocked is not null) return blocked;

        LeveragedPosition? position = portfolios.GetLeveraged(profileId, stockId);
        if (position is null)
            return Fail("no_leveraged_position", "stockmarket/error/no_leveraged_position");

        if (!CurrencyTemplates.TryGetValue(quote.Definition.Currency, out MongoId template))
            return Fail("unknown_currency", "stockmarket/error/unknown_currency");

        double now = market.MarketSecondsNow();
        if (now - position.OpenedMarketSeconds < portfolios.EffectiveTPlusOneSeconds)
            return Fail("t_plus_one", "stockmarket/error/t_plus_one");

        if (!portfolios.RemoveLeveraged(profileId, stockId))
            return Fail("no_leveraged_position", "stockmarket/error/no_leveraged_position");

        FillResult fill = market.SellWithImpact(
            stockId, position.Shares, config.SellStepShares);

        // 平多头 = 卖出，按成交总额收手续费
        long fee = RoundCurrency(fill.Gross * config.FeeRate);
        decimal net = fill.Gross - position.Borrowed - fee;
        long rounded = RoundCurrency(net);

        liquidation.CheckAll();

        if (rounded > 0)
            SendMoneyMail(sessionId, template, rounded, "stockmarket/mail/sell");
        else if (net < 0m)
            portfolios.AddDebt(profileId, quote.Definition.Currency, -net);

        await saveServer.SaveProfileAsync(sessionId);

        logger.Info(
            $"[StockMarket] {sessionId} 平 {position.Leverage}x {stockId} " +
            $"{position.Shares} 股 @ {quote.Price}，手续费 {fee}，结算 {net:F2}");

        decimal averagePrice = position.Shares > 0
            ? fill.Gross / position.Shares
            : quote.Price;

        return new TradeOutcome(
            true, null, "stockmarket/result/closed", averagePrice,
            Math.Max(0, rounded), position.Shares);
    }

    // ══════════════════ 做空 ══════════════════

    public async Task<TradeOutcome> OpenShortAsync(
        PmcData pmc, MongoId sessionId, string stockId, int shares, int leverage,
        ItemEventRouterResponse output)
    {
        if (leverage < MinLeverage || leverage > MaxLeverage)
            return Fail("invalid_leverage", "stockmarket/error/invalid_leverage");

        if (shares <= 0)
            return Fail("invalid_amount", "stockmarket/error/invalid_amount");

        StockQuote? quote = market.GetQuote(stockId);
        if (quote is null)
            return Fail("unknown_stock", "stockmarket/error/unknown_stock");
        if (quote.Halted)
            return Fail("halted", "stockmarket/error/halted");

        TradeOutcome? blocked = CheckTradable(pmc, sessionId, out string profileId);
        if (blocked is not null) return blocked;

        if (portfolios.HasShort(profileId, stockId))
            return Fail("short_exists", "stockmarket/error/short_exists");

        if (portfolios.GetTotalShares(profileId, stockId) + shares > config.MaxHoldingShares)
            return Fail("max_holding", "stockmarket/error/max_holding");

        if (market.LenderAvailable(stockId) < shares)
            return Fail("lender_exhausted", "stockmarket/error/lender_exhausted");

        if (!CurrencyTemplates.TryGetValue(quote.Definition.Currency, out MongoId template))
            return Fail("unknown_currency", "stockmarket/error/unknown_currency");

        // 卖出会让价格往下走，实际保证金不会高于这个保守估计
        long reserve = (long)Math.Ceiling(quote.Price * shares / leverage);
        if (GetBalance(pmc, template) < reserve)
            return Fail("insufficient_funds", "stockmarket/error/insufficient_funds");

        FillResult fill = market.SellWithImpact(stockId, shares, config.SellStepShares);

        // 开空 = 卖出借入的股票，同样收卖出侧手续费；费用从冻结卖出款里扣
        long fee = RoundCurrency(fill.Gross * config.FeeRate);
        decimal proceeds = fill.Gross - fee;
        long margin = (long)Math.Ceiling(fill.Gross / leverage);
        if (margin <= 0)
            return Fail("invalid_amount", "stockmarket/error/invalid_amount");

        if (!market.BorrowShares(stockId, shares))
            return Fail("lender_exhausted", "stockmarket/error/lender_exhausted");

        if (!TryRemoveMoney(pmc, sessionId, template, margin, output))
        {
            market.ReleaseBorrowedShares(stockId, shares);
            return Fail("insufficient_funds", "stockmarket/error/insufficient_funds");
        }

        decimal entryPrice = shares > 0 ? fill.Gross / shares : quote.Price;
        long tick = market.CurrentTick(MarketClock.NowUnix());

        portfolios.OpenShort(
            profileId, stockId, leverage, shares, entryPrice, proceeds, margin,
            tick, market.MarketSecondsNow());

        liquidation.CheckAll();
        await saveServer.SaveProfileAsync(sessionId);

        logger.Info(
            $"[StockMarket] {sessionId} 开空 {leverage}x {stockId} {shares} 股 " +
            $"@ 均价 {entryPrice:F2}，保证金 {margin}，手续费 {fee}，冻结卖出款 {proceeds:F2}");

        return new TradeOutcome(
            true, null, "stockmarket/result/opened_short", entryPrice, margin, shares);
    }

    public async Task<TradeOutcome> CloseShortAsync(
        PmcData pmc, MongoId sessionId, string stockId, ItemEventRouterResponse output)
    {
        StockQuote? quote = market.GetQuote(stockId);
        if (quote is null)
            return Fail("unknown_stock", "stockmarket/error/unknown_stock");
        if (quote.Halted)
            return Fail("halted", "stockmarket/error/halted");

        TradeOutcome? blocked = CheckTradable(pmc, sessionId, out string profileId);
        if (blocked is not null) return blocked;

        ShortPosition? position = portfolios.GetShort(profileId, stockId);
        if (position is null)
            return Fail("no_short_position", "stockmarket/error/no_short_position");

        if (!CurrencyTemplates.TryGetValue(quote.Definition.Currency, out MongoId template))
            return Fail("unknown_currency", "stockmarket/error/unknown_currency");

        double now = market.MarketSecondsNow();
        if (now - position.OpenedMarketSeconds < portfolios.EffectiveTPlusOneSeconds)
            return Fail("t_plus_one", "stockmarket/error/t_plus_one");

        FillResult fill = market.CoverWithImpact(
            stockId, position.Shares, config.SellStepShares);

        decimal equity = position.Margin + position.Proceeds - fill.Gross;

        if (!portfolios.RemoveShort(profileId, stockId))
            return Fail("no_short_position", "stockmarket/error/no_short_position");

        market.ReleaseBorrowedShares(stockId, position.Shares);

        // 平空 = 买回，不收卖出侧手续费（开空时已经收过）
        long payout = RoundCurrency(equity);

        liquidation.CheckAll();

        if (payout > 0)
            SendMoneyMail(sessionId, template, payout, "stockmarket/mail/sell");
        else if (equity < 0m)
            portfolios.AddDebt(profileId, quote.Definition.Currency, -equity);

        await saveServer.SaveProfileAsync(sessionId);

        logger.Info(
            $"[StockMarket] {sessionId} 平空 {position.Leverage}x {stockId} " +
            $"{position.Shares} 股 @ {quote.Price}，结算 {equity:F2}");

        decimal averagePrice = position.Shares > 0
            ? fill.Gross / position.Shares
            : quote.Price;

        return new TradeOutcome(
            true, null, "stockmarket/result/closed_short", averagePrice,
            Math.Max(0, payout), position.Shares);
    }

    // ══════════════════ 还款 ══════════════════

    public async Task<TradeOutcome> RepayAsync(
        PmcData pmc, MongoId sessionId, string currency, long amount,
        ItemEventRouterResponse output)
    {
        if (amount <= 0)
            return Fail("invalid_amount", "stockmarket/error/invalid_amount");

        TradeOutcome? blocked = CheckTradable(pmc, sessionId, out string profileId,
            allowDebt: true);
        if (blocked is not null) return blocked;

        if (!CurrencyTemplates.TryGetValue(currency, out MongoId template))
            return Fail("unknown_currency", "stockmarket/error/unknown_currency");

        decimal debt = portfolios.GetDebt(profileId, currency);
        if (debt <= 0m)
            return Fail("no_debt", "stockmarket/error/no_debt");

        long owed = (long)Math.Ceiling(debt);
        long available = GetBalance(pmc, template);
        long pay = Math.Min(Math.Min(amount, owed), available);

        if (pay <= 0)
            return Fail("insufficient_funds", "stockmarket/error/insufficient_funds");

        if (!TryRemoveMoney(pmc, sessionId, template, pay, output))
            return Fail("insufficient_funds", "stockmarket/error/insufficient_funds");

        decimal left = portfolios.ReduceDebt(profileId, currency, pay);

        await saveServer.SaveProfileAsync(sessionId);

        logger.Info(
            $"[StockMarket] {sessionId} 偿还 {currency} 欠债 {pay}，剩余 {left:F2}");

        return new TradeOutcome(
            true, null,
            left > 0m ? "stockmarket/result/repaid" : "stockmarket/result/debt_cleared",
            null, pay, null);
    }

    // ══════════════════ 校验 / 工具 ══════════════════

    /// <summary>
    /// 战局内禁止交易；有欠债时禁止除还款外的所有操作。
    /// </summary>
    private TradeOutcome? CheckTradable(
        PmcData pmc, MongoId sessionId, out string profileId, bool allowDebt = false)
    {
        profileId = sessionId.ToString();

        if (pmc.Inventory?.Items is null)
            return Fail("profile_unavailable", "stockmarket/error/profile_unavailable");

        if (raidStates.IsInRaid(sessionId))
            return Fail("market_closed", "stockmarket/error/market_closed");

        if (!allowDebt && portfolios.HasDebt(profileId))
            return Fail("debt_outstanding", "stockmarket/error/debt_outstanding");

        return null;
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

    /// <summary>
    /// 通过 SPT 的 InventoryHelper 扣钱。
    /// 它会同时把 DeletedItems/ChangedItems 写进 output.ProfileChanges，
    /// 客户端收到 item/moving 的响应后会自己同步仓库。
    /// </summary>
    private bool TryRemoveMoney(
        PmcData pmc, MongoId sessionId, MongoId template, long amount,
        ItemEventRouterResponse output)
    {
        if (amount <= 0)
            return true;

        List<Item>? items = pmc.Inventory?.Items;
        if (items is null)
            return false;

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
            int take = (int)Math.Min(have, remaining);
            if (take <= 0)
                continue;

            inventoryHelper.RemoveItemByCount(pmc, stack.Id, take, sessionId, output);
            remaining -= take;
        }

        return remaining <= 0;
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

    private void SendMoneyMail(MongoId sessionId, MongoId template, long amount, string messageKey)
    {
        Item? money = CreateCurrencyItem(template, amount);
        if (money is null)
            return;

        mailSendService.SendLocalisedSystemMessageToPlayer(
            sessionId, messageKey, [money], null, config.MailLifetimeSeconds);
    }

    private static TradeOutcome Fail(string code, string messageKey) =>
        new(false, code, messageKey);
}
