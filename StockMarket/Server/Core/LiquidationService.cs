using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services.Commerce;

namespace TarkFX.StockMarket.Server.Core;

/// <summary>
/// 保证金强平扫描。
///
/// <para>
/// 多头：权益 = 市值 − 借入；权益 ≤ 0 时强平，卖出冲击后的差额记欠债。
/// 空头：权益 = 保证金 + 冻结卖出款 − 股数 × 现价；权益 ≤ 0 时买回，
/// 买回冲击后的差额同样记欠债。
/// </para>
///
/// <para>
/// 【熔断】股票首次触及涨/跌停后，该股票的系统强平暂停 96 tick（24 塔科夫小时）。
/// 冻结期间玩家仍可主动平仓，只有系统强平被暂停。
/// </para>
///
/// <para>
/// 先处理多头、再处理空头，最多 8 轮，处理"一笔强平推动价格、让另一笔也爆仓"的连锁。
/// </para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class LiquidationService(
    MarketStore market,
    PortfolioStore portfolios,
    ItemHelper itemHelper,
    MailSendService mailSendService,
    LiquidationJournal journal,
    StockMarketConfig config,
    ISptLogger<LiquidationService> logger)
{
    private const int MaxPasses = 8;

    public void CheckAll()
    {
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            bool liquidatedAny = false;

            // ───── 多头 ─────
            foreach (LeveragedSnapshot snapshot in portfolios.GetOpenLeveragedSnapshot())
            {
                if (market.IsLiquidationFrozen(snapshot.StockId))
                    continue;

                StockQuote? quote = market.GetQuote(snapshot.StockId);
                if (quote is null || quote.Price <= 0m)
                    continue;

                decimal equity = snapshot.Shares * quote.Price - snapshot.Borrowed;
                if (equity > 0m)
                    continue;

                if (!portfolios.RemoveLeveraged(snapshot.ProfileId, snapshot.StockId))
                    continue;

                // 强平单同样分批成交并产生市场冲击
                FillResult fill = market.SellWithImpact(
                    snapshot.StockId, snapshot.Shares, config.SellStepShares);

                decimal net = fill.Gross - snapshot.Borrowed;
                decimal debt = net < 0m ? -net : 0m;
                decimal averageFill = snapshot.Shares > 0
                    ? fill.Gross / snapshot.Shares
                    : 0m;

                journal.WriteLong(
                    market.LastTick, snapshot.ProfileId, snapshot.StockId, snapshot.Shares,
                    quote.Price, averageFill, fill.Gross, snapshot.Borrowed,
                    snapshot.Margin, net, debt);

                if (net < 0m)
                {
                    portfolios.AddDebt(
                        snapshot.ProfileId, quote.Definition.Currency, -net);
                    SendMail(snapshot.ProfileId, "stockmarket/mail/liquidated_debt", null);
                }
                else
                {
                    SendMail(snapshot.ProfileId, "stockmarket/mail/liquidated", null);
                    long surplus = (long)Math.Round(net, MidpointRounding.AwayFromZero);
                    if (surplus > 0)
                    {
                        Item? money = CreateCurrencyItem(quote.Definition.Currency, surplus);
                        if (money is not null)
                            SendMail(snapshot.ProfileId, "stockmarket/mail/sell", [money]);
                    }
                }

                logger.Info(
                    $"[StockMarket] 强平多 {snapshot.ProfileId} / {snapshot.StockId}: " +
                    $"{snapshot.Shares} 股 @ {quote.Price:F2}，结算 {net:F2}");

                liquidatedAny = true;
            }

            // ───── 空头 ─────
            foreach (ShortSnapshot snapshot in portfolios.GetOpenShortsSnapshot())
            {
                if (market.IsLiquidationFrozen(snapshot.StockId))
                    continue;

                StockQuote? quote = market.GetQuote(snapshot.StockId);
                if (quote is null || quote.Price <= 0m)
                    continue;

                decimal equity =
                    snapshot.Margin + snapshot.Proceeds - snapshot.Shares * quote.Price;
                if (equity > 0m)
                    continue;

                if (!portfolios.RemoveShort(snapshot.ProfileId, snapshot.StockId))
                    continue;

                // 买回会产生向上的冲击（轧空）
                FillResult cover = market.CoverWithImpact(
                    snapshot.StockId, snapshot.Shares, config.SellStepShares);

                market.ReleaseBorrowedShares(snapshot.StockId, snapshot.Shares);

                decimal settled = snapshot.Margin + snapshot.Proceeds - cover.Gross;
                decimal debt = settled < 0m ? -settled : 0m;
                decimal averageCover = snapshot.Shares > 0
                    ? cover.Gross / snapshot.Shares
                    : 0m;

                journal.WriteShort(
                    market.LastTick, snapshot.ProfileId, snapshot.StockId, snapshot.Shares,
                    quote.Price, averageCover, cover.Gross, snapshot.Margin,
                    snapshot.Proceeds, settled, debt);

                if (settled < 0m)
                {
                    portfolios.AddDebt(
                        snapshot.ProfileId, quote.Definition.Currency, -settled);
                    SendMail(snapshot.ProfileId, "stockmarket/mail/liquidated_short_debt", null);
                }
                else
                {
                    SendMail(snapshot.ProfileId, "stockmarket/mail/liquidated_short", null);
                    long surplus = (long)Math.Round(settled, MidpointRounding.AwayFromZero);
                    if (surplus > 0)
                    {
                        Item? money = CreateCurrencyItem(quote.Definition.Currency, surplus);
                        if (money is not null)
                            SendMail(snapshot.ProfileId, "stockmarket/mail/sell", [money]);
                    }
                }

                logger.Info(
                    $"[StockMarket] 强平空 {snapshot.ProfileId} / {snapshot.StockId}: " +
                    $"{snapshot.Shares} 股 @ {quote.Price:F2}，结算 {settled:F2}");

                liquidatedAny = true;
            }

            if (!liquidatedAny)
                break;
        }
    }

    private void SendMail(string profileId, string messageKey, List<Item>? items)
    {
        try
        {
            mailSendService.SendLocalisedSystemMessageToPlayer(
                new MongoId(profileId),
                messageKey,
                items,
                null,
                config.MailLifetimeSeconds);
        }
        catch (Exception ex)
        {
            logger.Error($"[StockMarket] 发送强平通知失败：{ex.Message}");
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
