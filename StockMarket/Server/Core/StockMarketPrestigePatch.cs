using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;

namespace TarkFX.StockMarket.Server.Core;

/// <summary>
/// 转生平账。
///
/// <para>
/// 挂在 <see cref="PrestigeHelper.ProcessPendingPrestige"/> 之后 —— 这是服务端真正
/// 应用转生、生成新档的位置。平账只清我们自己的持仓数据：
/// 债务、现货、杠杆多、空头全部释放，空头股数归还券行，
/// <b>不触发任何行情冲击、不发邮件、不动玩家仓库</b>。
/// </para>
/// </summary>
[Injectable]
public sealed class StockMarketPrestigePatch : AbstractPatch
{
    private static PortfolioStore? _portfolios;
    private static MarketStore? _market;
    private static ISptLogger<StockMarketPrestigePatch>? _logger;

    public StockMarketPrestigePatch(
        PortfolioStore portfolios,
        MarketStore market,
        ISptLogger<StockMarketPrestigePatch> logger)
    {
        _portfolios = portfolios;
        _market = market;
        _logger = logger;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(PrestigeHelper).GetMethod(
                   nameof(PrestigeHelper.ProcessPendingPrestige),
                   BindingFlags.Public | BindingFlags.Instance)
               ?? throw new MissingMethodException(
                   nameof(PrestigeHelper), nameof(PrestigeHelper.ProcessPendingPrestige));
    }

    [PatchPostfix]
    public static void Postfix(SptProfile newProfile)
    {
        try
        {
            MongoId? profileId = newProfile?.ProfileInfo?.ProfileId;
            if (profileId is null || _portfolios is null)
                return;

            List<ReleasedBorrow> released = _portfolios.ResetProfile(profileId.Value.ToString());

            if (_market is not null)
            {
                foreach (ReleasedBorrow borrow in released)
                    _market.ReleaseBorrowedShares(borrow.StockId, borrow.Shares);
            }

            _logger?.Info(
                $"[StockMarket] 转生平账 {profileId.Value}：释放 {released.Count} 笔空头券源");
        }
        catch (Exception ex)
        {
            _logger?.Error($"[StockMarket] 转生平账失败：{ex}");
        }
    }
}

/// <summary>启用本 mod 的 AbstractPatch（DI 会注入所有 [Injectable] 的 patch）。</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public sealed class StockMarketPatchEnabler(
    IEnumerable<IRuntimePatch> patches,
    ISptLogger<StockMarketPatchEnabler> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        int enabled = 0;
        Assembly ownAssembly = typeof(StockMarketPatchEnabler).Assembly;

        foreach (IRuntimePatch patch in patches)
        {
            // 只启用本 mod 的补丁；DI 会注入所有 mod 的 IRuntimePatch
            if (patch.GetType().Assembly != ownAssembly)
                continue;

            try
            {
                if (!patch.IsActive)
                {
                    patch.Enable();
                    enabled++;
                }
            }
            catch (Exception ex)
            {
                logger.Error($"[StockMarket] 启用补丁 {patch.GetType().Name} 失败：{ex.Message}");
            }
        }

        logger.Info($"[StockMarket] 已启用 {enabled} 个补丁");
        return Task.CompletedTask;
    }
}
