using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TarkFX.StockMarket.Client.UI
{
    /// <summary>
    /// 图表滚轮缩放：向上滚少画几根（放大），向下滚多画几根（缩小）。
    /// 挂在图表 GameObject 上，由 KLineGraphic 的 raycastTarget 接收事件。
    /// </summary>
    internal sealed class ChartScrollZoom : MonoBehaviour, IScrollHandler
    {
        private Action<int> _onZoom;

        internal void Initialize(Action<int> onZoom)
        {
            _onZoom = onZoom;
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (_onZoom == null)
                return;

            float delta = eventData.scrollDelta.y;
            if (Mathf.Approximately(delta, 0f))
                return;

            // 向上滚 = 放大 = 可视根数减少
            _onZoom(delta > 0f ? -12 : 12);
            eventData.Use();
        }
    }
}
