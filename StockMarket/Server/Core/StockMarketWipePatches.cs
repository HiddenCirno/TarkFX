using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Launcher;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Servers;

namespace TarkFX.StockMarket.Server.Core;

/// <summary>
/// 启动器「手动清除存档 / Wipe」时平掉股市账。
///
/// <para>
/// SPT 的 Wipe 只是把 <c>ProfileInfo.IsWiped = true</c>，真正重建角色在下一次登录；
/// 这里在 Wipe 请求返回后立即清空债务/现货/杠杆/空头并归还券源，
/// 避免 Wipe 之后旧持仓/欠债还挂在账上。
/// </para>
/// </summary>
[Injectable]
public sealed class LauncherWipeResetPatch : AbstractPatch
{
    private static SaveServer? _saveServer;
    private static PortfolioStore? _portfolios;
    private static MarketStore? _market;
    private static ISptLogger<LauncherWipeResetPatch>? _logger;

    public LauncherWipeResetPatch(
        SaveServer saveServer,
        PortfolioStore portfolios,
        MarketStore market,
        ISptLogger<LauncherWipeResetPatch> logger)
    {
        _saveServer = saveServer;
        _portfolios = portfolios;
        _market = market;
        _logger = logger;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(LauncherV2Controller).GetMethod(
                   nameof(LauncherV2Controller.Wipe),
                   BindingFlags.Public | BindingFlags.Instance)
               ?? throw new MissingMethodException(
                   nameof(LauncherV2Controller), nameof(LauncherV2Controller.Wipe));
    }

    [PatchPostfix]
    public static void Postfix(RegisterData info)
    {
        try
        {
            if (info?.Username == null || _saveServer == null)
                return;

            KeyValuePair<MongoId, SptProfile> match = _saveServer.GetProfiles()
                .FirstOrDefault(pair => pair.Value.ProfileInfo?.Username == info.Username);

            MongoId? profileId = match.Value?.ProfileInfo?.ProfileId;
            if (profileId is null)
                return;

            WipeResetHelper.ResetProfile(
                _portfolios, _market, profileId.Value, "Wipe",
                message => _logger?.Info(message));
        }
        catch (Exception ex)
        {
            _logger?.Error($"[StockMarket] Wipe 平账失败：{ex}");
        }
    }
}

/// <summary>直接删除档案（启动器 Remove Profile）时同样平账。</summary>
[Injectable]
public sealed class ProfileRemoveResetPatch : AbstractPatch
{
    private static PortfolioStore? _portfolios;
    private static MarketStore? _market;
    private static ISptLogger<ProfileRemoveResetPatch>? _logger;

    public ProfileRemoveResetPatch(
        PortfolioStore portfolios,
        MarketStore market,
        ISptLogger<ProfileRemoveResetPatch> logger)
    {
        _portfolios = portfolios;
        _market = market;
        _logger = logger;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(SaveServer).GetMethod(
                   nameof(SaveServer.RemoveProfile),
                   BindingFlags.Public | BindingFlags.Instance)
               ?? throw new MissingMethodException(
                   nameof(SaveServer), nameof(SaveServer.RemoveProfile));
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionID)
    {
        try
        {
            WipeResetHelper.ResetProfile(
                _portfolios, _market, sessionID, "RemoveProfile",
                message => _logger?.Info(message));
        }
        catch (Exception ex)
        {
            _logger?.Error($"[StockMarket] 删档平账失败：{ex}");
        }
    }
}

internal static class WipeResetHelper
{
    public static void ResetProfile(
        PortfolioStore? portfolios,
        MarketStore? market,
        MongoId profileId,
        string reason,
        Action<string>? log)
    {
        if (portfolios == null)
            return;

        List<ReleasedBorrow> released = portfolios.ResetProfile(profileId.ToString());

        if (market != null)
        {
            foreach (ReleasedBorrow borrow in released)
                market.ReleaseBorrowedShares(borrow.StockId, borrow.Shares);
        }

        log?.Invoke(
            $"[StockMarket] {reason} 平账 {profileId}：释放 {released.Count} 笔空头券源");
    }
}
