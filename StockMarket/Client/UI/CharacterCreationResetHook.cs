using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using HarmonyLib;
using TarkFX.StockMarket.Client.Core;

namespace TarkFX.StockMarket.Client.UI
{
    /// <summary>
    /// 创建角色 → 主菜单时，请求服务端清空当前存档的股市记录。
    ///
    /// <para>
    /// Wipe / 转生之后客户端都会走一遍角色创建流程；在
    /// <see cref="ClientBackendSession.CreateProfile"/> 成功完成后置一个标记，
    /// 等主菜单打开（<see cref="MainMenuShowOperation.TryOpenMainMenu"/> /
    /// <see cref="MainMenuShowOperation.ShowMenuScreenSync"/>）时调用
    /// <c>/stock-market/reset</c>。
    /// </para>
    ///
    /// <para>
    /// 普通登录不会经过 CreateProfile，所以不会误清已有存档。
    /// 服务端侧的 Wipe/转生补丁仍然保留（尤其是"删除档案后新 id"的情况）。
    /// </para>
    /// </summary>
    internal static class CharacterCreationResetHook
    {
        private static volatile bool _pendingReset;

        [HarmonyPatch(typeof(ClientBackendSession), nameof(ClientBackendSession.CreateProfile))]
        internal static class CreateProfilePatch
        {
            private static void Postfix(Task<IResult> __result)
            {
                if (__result == null)
                    return;

                __result.ContinueWith(
                    (Task<IResult> task) =>
                    {
                        if (task.Status == TaskStatus.RanToCompletion &&
                            task.Result != null && task.Result.Succeed)
                        {
                            _pendingReset = true;
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        [HarmonyPatch(typeof(MainMenuShowOperation), nameof(MainMenuShowOperation.TryOpenMainMenu))]
        internal static class OpenMainMenuPatch
        {
            private static void Postfix() => TrySendReset();
        }

        [HarmonyPatch(typeof(MainMenuShowOperation), nameof(MainMenuShowOperation.ShowMenuScreenSync))]
        internal static class ShowMenuPatch
        {
            private static void Postfix() => TrySendReset();
        }

        private static void TrySendReset()
        {
            if (!_pendingReset)
                return;

            _pendingReset = false;
            _ = StockApi.ResetAsync();
            Plugin.Log?.LogInfo("[StockMarket] 创建角色完成，已请求服务端清空股市记录。");
        }
    }
}
