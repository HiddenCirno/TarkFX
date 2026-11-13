using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace TarkFX.StockMarket.Client.Core
{
    /// <summary>
    /// 客户端 → 服务端。所有路由都是 POST。
    ///
    /// <para>
    /// RequestHandler.PostJson 是**同步阻塞**的，直接在主线程轮询会掉帧，
    /// 所以统一丢到线程池，调用方在 Update 里检查 IsCompleted 再回主线程处理。
    /// </para>
    /// </summary>
    internal static class StockApi
    {
        internal static Task<JObject> PostAsync(string route, object body)
        {
            string json = JsonConvert.SerializeObject(body ?? new { });

            return Task.Run(delegate
            {
                try
                {
                    return JObject.Parse(RequestHandler.PostJson(route, json));
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[StockMarket] {route} 请求失败：{ex.Message}");
                    return null;
                }
            });
        }

        internal static Task<JObject> GetStateAsync() =>
            PostAsync("/stock-market/state", new { });

        internal static Task<JObject> GetCandlesAsync(string stockId, int count = 200) =>
            PostAsync("/stock-market/candles", new { stockId, count });

        internal static Task<JObject> BuyAsync(string stockId, int shares) =>
            PostAsync("/stock-market/buy", new { stockId, shares });

        internal static Task<JObject> SellAsync(string stockId, int shares) =>
            PostAsync("/stock-market/sell", new { stockId, shares });
    }
}
