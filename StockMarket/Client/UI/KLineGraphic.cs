using System;
using System.Collections.Generic;
using TarkFX.StockMarket.Client.Model;
using UnityEngine;
using UnityEngine.UI;

namespace TarkFX.StockMarket.Client.UI
{
    /// <summary>
    /// 自绘蜡烛图 + MA5/MA20 + 现价/昨收/成本参考线。
    ///
    /// <para>
    /// 只渲染最近 <see cref="VisibleCount"/> 根 —— 200 根挤在几百像素里，
    /// 影线只有 1px，会糊成"阶梯"。
    /// </para>
    ///
    /// <para>
    /// 【Y 轴范围】必须是"可视 K 线的 High/Low + 可视段的 MA + 持仓成本价"的并集。
    /// 只统计 K 线的话，上涨行情里滞后的 MA20 会低于窗口最低价，被线性外推到图表下方
    /// （表现为"紫色均线贴地飞行"）；成本价同理会被画到屏幕外。
    /// </para>
    /// </summary>
    internal sealed class KLineGraphic : MaskableGraphic
    {
        private const decimal MaInvalid = decimal.MinValue;

        public Color UpColor = new Color(0.26f, 0.85f, 0.45f, 1f);
        public Color DownColor = new Color(0.94f, 0.34f, 0.34f, 1f);
        public Color GridColor = new Color(1f, 1f, 1f, 0.07f);
        public Color BackgroundColor = new Color(0f, 0f, 0f, 0.28f);
        public Color Ma5Color = new Color(0.96f, 0.80f, 0.32f, 0.95f);
        public Color Ma20Color = new Color(0.62f, 0.55f, 0.95f, 0.95f);
        public Color CurrentPriceColor = new Color(0.98f, 0.96f, 0.86f, 0.9f);
        public Color ReferenceColor = new Color(0.70f, 0.70f, 0.66f, 0.55f);
        public Color CostColor = new Color(0.36f, 0.66f, 1f, 0.95f);

        private readonly List<CandleDto> _candles = new List<CandleDto>();
        private decimal[] _ma5 = Array.Empty<decimal>();
        private decimal[] _ma20 = Array.Empty<decimal>();

        private int _visibleCount = 120;
        private decimal _viewMin;
        private decimal _viewMax;

        private bool _hasCurrentPrice;
        private decimal _currentPrice;
        private bool _hasReferencePrice;
        private decimal _referencePrice;
        private bool _hasCostPrice;
        private decimal _costPrice;

        public int VisibleCount => _visibleCount;
        public decimal ViewMin => _viewMin;
        public decimal ViewMax => _viewMax;
        public IReadOnlyList<CandleDto> Candles => _candles;
        public int VisibleStart => Mathf.Max(0, _candles.Count - _visibleCount);

        protected override void Awake()
        {
            base.Awake();
            raycastTarget = true; // 需要接收滚轮事件做缩放
        }

        public void SetData(List<CandleDto> candles)
        {
            _candles.Clear();
            if (candles != null)
                _candles.AddRange(candles);

            BuildMovingAverages();
            RecalculateRange();
            SetVerticesDirty();
        }

        public void SetVisibleCount(int count)
        {
            int clamped = Mathf.Clamp(count, 20, 200);
            if (clamped == _visibleCount)
                return;

            _visibleCount = clamped;
            RecalculateRange();
            SetVerticesDirty();
        }

        public void SetColors(Color up, Color down)
        {
            if (up == UpColor && down == DownColor)
                return;

            UpColor = up;
            DownColor = down;
            SetVerticesDirty();
        }

        /// <summary>current = 现价，reference = 昨收，cost = 持仓成本。null 表示不画。</summary>
        public void SetPriceLines(decimal? current, decimal? reference, decimal? cost)
        {
            bool changed = UpdateValue(ref _hasCurrentPrice, ref _currentPrice, current)
                         | UpdateValue(ref _hasReferencePrice, ref _referencePrice, reference)
                         | UpdateValue(ref _hasCostPrice, ref _costPrice, cost);

            if (!changed)
                return;

            // 成本价会影响 Y 轴范围
            RecalculateRange();
            SetVerticesDirty();
        }

        private static bool UpdateValue(ref bool hasValue, ref decimal value, decimal? incoming)
        {
            if (incoming.HasValue == hasValue &&
                (!incoming.HasValue || incoming.Value == value))
                return false;

            hasValue = incoming.HasValue;
            value = incoming ?? 0m;
            return true;
        }

        public void Clear()
        {
            _candles.Clear();
            _ma5 = Array.Empty<decimal>();
            _ma20 = Array.Empty<decimal>();
            _hasCurrentPrice = false;
            _hasReferencePrice = false;
            _hasCostPrice = false;
            RecalculateRange();
            SetVerticesDirty();
        }

        /// <summary>
        /// 滚动求和计算完整序列的 MA5 / MA20（不足窗口的位置标记为无效）。
        /// O(n)，每次 SetData 只算一次。
        /// </summary>
        private void BuildMovingAverages()
        {
            int count = _candles.Count;
            _ma5 = new decimal[count];
            _ma20 = new decimal[count];

            decimal sum5 = 0m;
            decimal sum20 = 0m;

            for (int i = 0; i < count; i++)
            {
                decimal close = _candles[i].C;
                sum5 += close;
                sum20 += close;

                if (i >= 5) sum5 -= _candles[i - 5].C;
                if (i >= 20) sum20 -= _candles[i - 20].C;

                _ma5[i] = i >= 4 ? sum5 / 5m : MaInvalid;
                _ma20[i] = i >= 19 ? sum20 / 20m : MaInvalid;
            }
        }

        private void RecalculateRange()
        {
            int start = VisibleStart;
            if (_candles.Count == 0 || start >= _candles.Count)
            {
                _viewMin = 0m;
                _viewMax = 0m;
                return;
            }

            decimal min = _candles[start].L;
            decimal max = _candles[start].H;

            for (int i = start; i < _candles.Count; i++)
            {
                CandleDto candle = _candles[i];
                if (candle.L < min) min = candle.L;
                if (candle.H > max) max = candle.H;
            }

            // 均线必须进范围，否则上涨行情里滞后的 MA20 会被画到图表外
            IncludeMovingAverages(start, ref min, ref max);

            // 成本线必须进范围（用户持仓成本常常在可视窗口之外）
            if (_hasCostPrice)
            {
                if (_costPrice < min) min = _costPrice;
                if (_costPrice > max) max = _costPrice;
            }

            if (_hasCurrentPrice)
            {
                if (_currentPrice < min) min = _currentPrice;
                if (_currentPrice > max) max = _currentPrice;
            }

            if (max <= min)
            {
                decimal pad = max == 0m ? 1m : max * 0.01m;
                _viewMin = min - pad;
                _viewMax = max + pad;
            }
            else
            {
                decimal pad = (max - min) * 0.08m;
                _viewMin = min - pad;
                _viewMax = max + pad;
            }
        }

        private void IncludeMovingAverages(int start, ref decimal min, ref decimal max)
        {
            for (int i = start; i < _candles.Count; i++)
            {
                decimal ma5 = _ma5[i];
                if (ma5 != MaInvalid)
                {
                    if (ma5 < min) min = ma5;
                    if (ma5 > max) max = ma5;
                }

                decimal ma20 = _ma20[i];
                if (ma20 != MaInvalid)
                {
                    if (ma20 < min) min = ma20;
                    if (ma20 > max) max = ma20;
                }
            }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            Rect rect = rectTransform.rect;
            if (rect.width <= 2f || rect.height <= 2f)
                return;

            AddQuad(vh, rect, BackgroundColor);

            // 横向网格
            for (int i = 1; i <= 4; i++)
            {
                float y = rect.yMin + rect.height * i / 5f;
                AddQuad(vh, new Rect(rect.xMin, y, rect.width, 1f), GridColor);
            }

            int start = VisibleStart;
            int count = _candles.Count - start;
            if (count <= 0)
                return;

            // 参考线：昨收（虚线、暗）
            if (_hasReferencePrice && InRange(_referencePrice))
            {
                AddDashedLine(vh, rect.xMin, rect.xMax, ToY(rect, _referencePrice),
                    4f, 4f, 1f, ReferenceColor);
            }

            // 持仓成本（蓝色虚线）
            if (_hasCostPrice)
            {
                AddDashedLine(vh, rect.xMin, rect.xMax, ToY(rect, _costPrice),
                    10f, 4f, 1.4f, CostColor);
            }

            // 现价线（贯穿全图，亮色虚线）
            if (_hasCurrentPrice)
            {
                AddDashedLine(vh, rect.xMin, rect.xMax, ToY(rect, _currentPrice),
                    6f, 3f, 1.2f, CurrentPriceColor);
            }

            float slot = rect.width / count;
            float bodyWidth = Mathf.Max(1f, slot * 0.62f);

            for (int i = start; i < _candles.Count; i++)
            {
                CandleDto candle = _candles[i];
                float centerX = rect.xMin + slot * (i - start + 0.5f);

                float yHigh = ToY(rect, candle.H);
                float yLow = ToY(rect, candle.L);
                float yOpen = ToY(rect, candle.O);
                float yClose = ToY(rect, candle.C);

                Color color = candle.C >= candle.O ? UpColor : DownColor;

                // 影线
                float wickTop = Mathf.Max(yHigh, yLow);
                float wickBottom = Mathf.Min(yHigh, yLow);
                AddQuad(vh, new Rect(centerX - 0.5f, wickBottom, 1f,
                    Mathf.Max(1f, wickTop - wickBottom)), color);

                // 实体
                float bodyTop = Mathf.Max(yOpen, yClose);
                float bodyBottom = Mathf.Min(yOpen, yClose);
                float bodyHeight = Mathf.Max(1f, bodyTop - bodyBottom);
                AddQuad(vh, new Rect(centerX - bodyWidth * 0.5f, bodyBottom,
                    bodyWidth, bodyHeight), color);
            }

            // 均线（用完整序列算，只画可视段）
            DrawMovingAverage(vh, rect, start, _ma5, Ma5Color);
            DrawMovingAverage(vh, rect, start, _ma20, Ma20Color);
        }

        private void DrawMovingAverage(
            VertexHelper vh, Rect rect, int start, decimal[] values, Color color)
        {
            if (values.Length != _candles.Count || start >= _candles.Count)
                return;

            int count = _candles.Count - start;
            float slot = rect.width / count;

            Vector2? previous = null;

            for (int i = start; i < _candles.Count; i++)
            {
                decimal value = values[i];
                if (value == MaInvalid)
                {
                    previous = null;
                    continue;
                }

                float x = rect.xMin + slot * (i - start + 0.5f);
                float y = ToY(rect, value);
                Vector2 point = new Vector2(x, y);

                if (previous.HasValue)
                    AddLine(vh, previous.Value, point, 1.4f, color);

                previous = point;
            }
        }

        private bool InRange(decimal value) => value >= _viewMin && value <= _viewMax;

        private float ToY(Rect rect, decimal value)
        {
            if (_viewMax <= _viewMin)
                return rect.center.y;

            double t = (double)((value - _viewMin) / (_viewMax - _viewMin));
            return rect.yMin + (float)t * rect.height;
        }

        private static void AddQuad(VertexHelper vh, Rect rect, Color color)
        {
            if (rect.width <= 0f || rect.height <= 0f)
                return;

            int start = vh.currentVertCount;

            UIVertex vertex = UIVertex.simpleVert;
            vertex.color = color;

            float y0 = Mathf.Min(rect.yMin, rect.yMax);
            float y1 = Mathf.Max(rect.yMin, rect.yMax);

            vertex.position = new Vector3(rect.xMin, y0);
            vh.AddVert(vertex);
            vertex.position = new Vector3(rect.xMin, y1);
            vh.AddVert(vertex);
            vertex.position = new Vector3(rect.xMax, y1);
            vh.AddVert(vertex);
            vertex.position = new Vector3(rect.xMax, y0);
            vh.AddVert(vertex);

            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

        private static void AddLine(VertexHelper vh, Vector2 a, Vector2 b,
            float thickness, Color color)
        {
            Vector2 direction = b - a;
            float length = direction.magnitude;
            if (length < 0.001f)
                return;

            Vector2 normal = new Vector2(-direction.y, direction.x) / length * (thickness * 0.5f);

            int start = vh.currentVertCount;
            UIVertex vertex = UIVertex.simpleVert;
            vertex.color = color;

            vertex.position = a - normal; vh.AddVert(vertex);
            vertex.position = a + normal; vh.AddVert(vertex);
            vertex.position = b + normal; vh.AddVert(vertex);
            vertex.position = b - normal; vh.AddVert(vertex);

            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

        private static void AddDashedLine(VertexHelper vh, float x0, float x1, float y,
            float dash, float gap, float thickness, Color color)
        {
            for (float x = x0; x < x1; x += dash + gap)
            {
                float end = Mathf.Min(x + dash, x1);
                AddQuad(vh, new Rect(x, y - thickness * 0.5f, end - x, thickness), color);
            }
        }
    }
}
