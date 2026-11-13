using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT.UI;
using HarmonyLib;

namespace TarkFX.StockMarket.Client.UI
{
    /// <summary>
    /// 拦截 InventoryScreen 的所有 Show 重载。
    ///
    /// <para>
    /// 必须是 Prefix：InventoryScreen.Show 的方法体里会用 _tabDictionary
    /// 重新构造 TabGroup，只有在那之前把新 Tab 塞进字典，它才会被纳入选中/切换逻辑。
    /// </para>
    /// </summary>
    [HarmonyPatch]
    internal static class InventoryScreenShowPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            AccessTools.GetDeclaredMethods(typeof(InventoryScreen))
                .Where(method => method.Name == "Show");

        private static void Prefix(InventoryScreen __instance)
        {
            try
            {
                StockMarketTab.TryAttach(__instance);
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[StockMarket] 注入 Tab 失败：{ex}");
            }
        }
    }
}
