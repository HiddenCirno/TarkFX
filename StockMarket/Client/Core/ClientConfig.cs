using BepInEx.Configuration;
using UnityEngine;

namespace TarkFX.StockMarket.Client.Core
{
    /// <summary>
    /// 客户端显示配置。只影响渲染，不改任何服务端数据。
    /// </summary>
    internal static class ClientConfig
    {
        private static readonly Color Green = new Color(0.26f, 0.85f, 0.45f, 1f);
        private static readonly Color Red = new Color(0.94f, 0.34f, 0.34f, 1f);

        internal static ConfigEntry<int> VisibleCandles { get; private set; }
        internal static ConfigEntry<bool> RedUpGreenDown { get; private set; }
        internal static ConfigEntry<float> YAxisZoom { get; private set; }

        internal static Color UpColor => RedUpGreenDown.Value ? Red : Green;
        internal static Color DownColor => RedUpGreenDown.Value ? Green : Red;

        internal static void Init(ConfigFile config)
        {
            VisibleCandles = config.Bind(
                "1. 图表 / Chart",
                "可视 K 线根数 / Visible Candles",
                120,
                new ConfigDescription(
                    "图上同时显示多少根 K 线。200 根挤在几百像素里会让影线糊成阶梯，\n" +
                    "默认 120 根；均线仍按完整历史计算，所以边界处的 MA20 是正确的。",
                    new AcceptableValueRange<int>(20, 200)));

            RedUpGreenDown = config.Bind(
                "1. 图表 / Chart",
                "红涨绿跌 / Red Up, Green Down",
                true,
                "开启 = A 股习惯（红涨绿跌）；关闭 = 美股习惯（绿涨红跌）。");

            YAxisZoom = config.Bind(
                "1. 图表 / Chart",
                "Y 轴放宽系数 / Y-Axis Zoom",
                1.35f,
                new ConfigDescription(
                    "Y 轴总跨度 = 数据区间 × 该系数。\n" +
                    "1.0 = 贴边铺满；1.35 = 上下各留约 15% 空白（默认）；越大越平缓。\n" +
                    "想让小波动看起来不那么陡就调大，想看清细节就调小。",
                    new AcceptableValueRange<float>(1.0f, 2.5f)));
        }
    }
}
