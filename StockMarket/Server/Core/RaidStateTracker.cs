using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace TarkFX.StockMarket.Server.Core;

/// <summary>
/// 服务端战局状态跟踪。
///
/// <para>
/// SPT 4.1 的 <c>SptProfile.InraidData</c> 在服务端已经不再写入 raid 状态，
/// 所以这里用 LocationLifecycleService 的 start/end 补丁自己维护一份内存状态，
/// 并在登录（AddActiveProfile）时清空，避免玩家崩溃后永久被锁。
/// </para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class RaidStateTracker
{
    private readonly ConcurrentDictionary<MongoId, byte> _inRaid = new();

    public void SetInRaid(MongoId sessionId, bool inRaid)
    {
        if (inRaid)
            _inRaid[sessionId] = 0;
        else
            _inRaid.TryRemove(sessionId, out _);
    }

    public bool IsInRaid(MongoId sessionId) => _inRaid.ContainsKey(sessionId);
}
