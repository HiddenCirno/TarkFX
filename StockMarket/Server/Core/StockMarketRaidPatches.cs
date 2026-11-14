using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Services.Profile;

namespace TarkFX.StockMarket.Server.Core;

/// <summary>进入战局：标记该 session 正在 raid 中。</summary>
[Injectable]
public sealed class RaidStateStartPatch : AbstractPatch
{
    private static RaidStateTracker? _tracker;

    public RaidStateStartPatch(RaidStateTracker tracker)
    {
        _tracker = tracker;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocationLifecycleService).GetMethod(
                   nameof(LocationLifecycleService.StartLocalRaidAsync),
                   BindingFlags.Public | BindingFlags.Instance)
               ?? throw new MissingMethodException(
                   nameof(LocationLifecycleService),
                   nameof(LocationLifecycleService.StartLocalRaidAsync));
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionId)
    {
        _tracker?.SetInRaid(sessionId, true);
    }
}

/// <summary>战局结束（撤离/死亡）：解除标记。</summary>
[Injectable]
public sealed class RaidStateEndPatch : AbstractPatch
{
    private static RaidStateTracker? _tracker;

    public RaidStateEndPatch(RaidStateTracker tracker)
    {
        _tracker = tracker;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocationLifecycleService).GetMethod(
                   nameof(LocationLifecycleService.EndLocalRaidAsync),
                   BindingFlags.Public | BindingFlags.Instance)
               ?? throw new MissingMethodException(
                   nameof(LocationLifecycleService),
                   nameof(LocationLifecycleService.EndLocalRaidAsync));
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionId)
    {
        _tracker?.SetInRaid(sessionId, false);
    }
}

/// <summary>客户端重新登录：清空可能残留的战局标记。</summary>
[Injectable]
public sealed class RaidStateLoginPatch : AbstractPatch
{
    private static RaidStateTracker? _tracker;

    public RaidStateLoginPatch(RaidStateTracker tracker)
    {
        _tracker = tracker;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(ProfileActivityService).GetMethod(
                   nameof(ProfileActivityService.AddActiveProfile),
                   BindingFlags.Public | BindingFlags.Instance)
               ?? throw new MissingMethodException(
                   nameof(ProfileActivityService),
                   nameof(ProfileActivityService.AddActiveProfile));
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionId)
    {
        _tracker?.SetInRaid(sessionId, false);
    }
}
