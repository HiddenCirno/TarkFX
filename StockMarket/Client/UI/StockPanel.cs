using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using TarkFX.StockMarket.Client.Core;
using TarkFX.StockMarket.Client.Model;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace TarkFX.StockMarket.Client.UI
{
    /// <summary>
    /// 交易所面板：行情轮询、K 线、坐标轴、下单。
    ///
    /// <para>
    /// 轮询是“后台线程发请求 → 主线程取结果”：
    /// <see cref="StockApi"/> 用 Task.Run 包住同步的 PostJson，
    /// 这里只在 <see cref="Update"/> 里检查 IsCompleted，不会卡帧。
    /// </para>
    /// </summary>
    internal sealed class StockPanel : MonoBehaviour
    {
        private const float PollIntervalSeconds = 5f;
        private const int YLabelCount = 5;

        private InventoryScreen _screen;
        private TMP_Text _template;
        private bool _built;

        private TMP_Text _header;
        private TMP_Text _price;
        private TMP_Text _change;
        private TMP_Text _holding;
        private TMP_Text _sellable;
        private TMP_Text _avgCost;
        private TMP_Text _value;
        private TMP_Text _unlock;
        private TMP_Text _balance;
        private TMP_Text _amount;
        private TMP_Text _status;
        private KLineGraphic _chart;

        private readonly TMP_Text[] _yLabels = new TMP_Text[YLabelCount];
        private readonly TMP_Text[] _timeLabels = new TMP_Text[3];

        private Button _buy;
        private Button _sell;

        private bool _active;
        private int _amountValue = 1;
        private string _selectedStock = "";
        private string _currentSymbol = "₽";
        private long _lastTick = -1;
        private float _nextPoll;

        // 可视根数：滚轮缩放用运行时的值；配置项只在被修改时重新覆盖
        private int _visibleCount = -1;
        private int _configVisibleSnapshot = -1;

        // 按钮当前颜色，避免每 5 秒重复写 Image.color 触发重建
        private Color _buyColor;
        private Color _sellColor;

        private Task<JObject> _stateTask;
        private Task<JObject> _candleTask;
        private Task<JObject> _tradeTask;

        private StateDto _state;

        // ══════════════════ 构建 ══════════════════

        internal void Build(InventoryScreen screen, TMP_Text template)
        {
            _screen = screen;
            _template = template;

            _header = CreateLabel(transform, "Header", "stockmarket/ui/title".Localized(),
                24f, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(16f, -54f), new Vector2(-16f, -10f),
                TextAlignmentOptions.MidlineLeft);

            BuildChart();
            BuildPriceAxis();
            BuildTimeAxis();
            BuildSideColumn();
            BuildStatus();

            _built = true;
            _active = gameObject.activeInHierarchy;
        }

        private void BuildChart()
        {
            GameObject chartObject = new GameObject("Chart", typeof(RectTransform));
            chartObject.transform.SetParent(transform, false);

            RectTransform rect = (RectTransform)chartObject.transform;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0.655f, 1f);
            rect.offsetMin = new Vector2(16f, 84f);
            rect.offsetMax = new Vector2(-4f, -60f);

            _chart = chartObject.AddComponent<KLineGraphic>();
            _chart.raycastTarget = true;

            ChartScrollZoom zoom = chartObject.AddComponent<ChartScrollZoom>();
            zoom.Initialize(Zoom);
        }

        private int ResolveVisibleCount()
        {
            int configured = ClientConfig.VisibleCandles.Value;

            // 配置被改过就重置；否则保留滚轮缩放的结果
            if (_visibleCount < 0 || configured != _configVisibleSnapshot)
            {
                _configVisibleSnapshot = configured;
                _visibleCount = Mathf.Clamp(configured, 20, 200);
            }

            return _visibleCount;
        }

        private void Zoom(int delta)
        {
            _visibleCount = Mathf.Clamp(ResolveVisibleCount() + delta, 20, 200);
            _chart.SetVisibleCount(_visibleCount);
            UpdateAxes();
        }

        private void BuildPriceAxis()
        {
            GameObject axis = new GameObject("PriceAxis", typeof(RectTransform));
            axis.transform.SetParent(transform, false);

            RectTransform rect = (RectTransform)axis.transform;
            rect.anchorMin = new Vector2(0.655f, 0f);
            rect.anchorMax = new Vector2(0.70f, 1f);
            rect.offsetMin = new Vector2(0f, 84f);
            rect.offsetMax = new Vector2(0f, -60f);

            for (int i = 0; i < YLabelCount; i++)
            {
                float fraction = i / (float)(YLabelCount - 1);
                _yLabels[i] = CreateLabel(axis.transform, "Y" + i, "--",
                    12f,
                    new Vector2(0f, fraction), new Vector2(1f, fraction),
                    new Vector2(0f, -9f), new Vector2(0f, 9f),
                    TextAlignmentOptions.MidlineRight);
                _yLabels[i].color = new Color(0.72f, 0.72f, 0.68f, 1f);
            }
        }

        private void BuildTimeAxis()
        {
            GameObject axis = new GameObject("TimeAxis", typeof(RectTransform));
            axis.transform.SetParent(transform, false);

            RectTransform rect = (RectTransform)axis.transform;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0.655f, 0f);
            rect.offsetMin = new Vector2(16f, 58f);
            rect.offsetMax = new Vector2(-4f, 80f);

            _timeLabels[0] = CreateLabel(axis.transform, "TimeLeft", "",
                12f,
                new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(130f, 0f),
                TextAlignmentOptions.MidlineLeft);

            _timeLabels[1] = CreateLabel(axis.transform, "TimeMid", "",
                12f,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 1f),
                new Vector2(-80f, 0f), new Vector2(80f, 0f),
                TextAlignmentOptions.Center);

            _timeLabels[2] = CreateLabel(axis.transform, "TimeRight", "",
                12f,
                new Vector2(1f, 0f), new Vector2(1f, 1f),
                new Vector2(-130f, 0f), new Vector2(0f, 0f),
                TextAlignmentOptions.MidlineRight);

            foreach (TMP_Text label in _timeLabels)
                label.color = new Color(0.66f, 0.66f, 0.62f, 1f);
        }

        private void BuildSideColumn()
        {
            GameObject side = new GameObject("Side", typeof(RectTransform));
            side.transform.SetParent(transform, false);

            RectTransform rect = (RectTransform)side.transform;
            rect.anchorMin = new Vector2(0.70f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.offsetMin = new Vector2(8f, 64f);
            rect.offsetMax = new Vector2(-16f, -60f);

            _price = CreateTopLabel(side.transform, "Price", 4f, 42f, 28f, TextAlignmentOptions.MidlineLeft);
            _change = CreateTopLabel(side.transform, "Change", 48f, 30f, 19f, TextAlignmentOptions.MidlineLeft);
            _holding = CreateTopLabel(side.transform, "Holding", 90f, 28f, 17f, TextAlignmentOptions.MidlineLeft);
            _sellable = CreateTopLabel(side.transform, "Sellable", 120f, 28f, 17f, TextAlignmentOptions.MidlineLeft);
            _avgCost = CreateTopLabel(side.transform, "AvgCost", 150f, 28f, 17f, TextAlignmentOptions.MidlineLeft);
            _value = CreateTopLabel(side.transform, "Value", 180f, 28f, 17f, TextAlignmentOptions.MidlineLeft);
            _unlock = CreateTopLabel(side.transform, "Unlock", 210f, 26f, 15f, TextAlignmentOptions.MidlineLeft);
            _balance = CreateTopLabel(side.transform, "Balance", 246f, 30f, 17f, TextAlignmentOptions.MidlineLeft);
            _amount = CreateTopLabel(side.transform, "Amount", 288f, 34f, 20f, TextAlignmentOptions.MidlineLeft);

            BuildControls(side.transform);
        }

        private void BuildControls(Transform parent)
        {
            GameObject controls = new GameObject("Controls", typeof(RectTransform));
            controls.transform.SetParent(parent, false);

            RectTransform rect = (RectTransform)controls.transform;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(0f, 104f);
            rect.anchoredPosition = new Vector2(0f, 8f);

            Color buttonColor = new Color(0.16f, 0.18f, 0.18f, 0.95f);

            CreateButton(controls.transform, "Minus10", "-10",
                new Vector2(0.00f, 1f), new Vector2(0.235f, 1f),
                new Vector2(0f, -44f), new Vector2(0f, -2f), buttonColor)
                .onClick.AddListener((UnityAction)(() => AddAmount(-10)));

            CreateButton(controls.transform, "Minus1", "-1",
                new Vector2(0.255f, 1f), new Vector2(0.49f, 1f),
                new Vector2(0f, -44f), new Vector2(0f, -2f), buttonColor)
                .onClick.AddListener((UnityAction)(() => AddAmount(-1)));

            CreateButton(controls.transform, "Plus1", "+1",
                new Vector2(0.51f, 1f), new Vector2(0.745f, 1f),
                new Vector2(0f, -44f), new Vector2(0f, -2f), buttonColor)
                .onClick.AddListener((UnityAction)(() => AddAmount(1)));

            CreateButton(controls.transform, "Plus10", "+10",
                new Vector2(0.765f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -44f), new Vector2(0f, -2f), buttonColor)
                .onClick.AddListener((UnityAction)(() => AddAmount(10)));

            _buy = CreateButton(controls.transform, "Buy", "stockmarket/ui/buy".Localized(),
                new Vector2(0f, 0f), new Vector2(0.49f, 0f),
                new Vector2(0f, 2f), new Vector2(0f, 44f),
                new Color(0.12f, 0.32f, 0.18f, 0.95f));
            _buy.onClick.AddListener((UnityAction)OnBuy);

            _sell = CreateButton(controls.transform, "Sell", "stockmarket/ui/sell".Localized(),
                new Vector2(0.51f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 2f), new Vector2(0f, 44f),
                new Color(0.38f, 0.14f, 0.14f, 0.95f));
            _sell.onClick.AddListener((UnityAction)OnSell);

            // 初始配色也跟随配置（否则会先闪一下写死的绿买红卖）
            ApplyButtonColors();
        }

        private void BuildStatus()
        {
            _status = CreateLabel(transform, "Status", "",
                16f,
                new Vector2(0f, 0f), new Vector2(0.70f, 0f),
                new Vector2(16f, 10f), new Vector2(-8f, 54f),
                TextAlignmentOptions.MidlineLeft);
            _status.color = new Color(0.75f, 0.75f, 0.70f, 1f);
        }

        // ══════════════════ 生命周期 ══════════════════

        private void OnEnable()
        {
            if (!_built)
                return;

            _active = true;
            _nextPoll = 0f;
            SetStatus("");
        }

        private void OnDisable()
        {
            _active = false;
        }

        private void Update()
        {
            if (!_built || !_active)
                return;

            if (_tradeTask != null && _tradeTask.IsCompleted)
            {
                HandleTradeResult(_tradeTask);
                _tradeTask = null;
                _nextPoll = 0f;
            }

            if (_stateTask != null && _stateTask.IsCompleted)
            {
                HandleState(_stateTask);
                _stateTask = null;
            }

            if (_candleTask != null && _candleTask.IsCompleted)
            {
                HandleCandles(_candleTask);
                _candleTask = null;
            }

            if (_stateTask == null && _tradeTask == null &&
                Time.realtimeSinceStartup >= _nextPoll)
            {
                _nextPoll = Time.realtimeSinceStartup + PollIntervalSeconds;
                _stateTask = StockApi.GetStateAsync();
            }
        }

        // ══════════════════ 数据处理 ══════════════════

        private void HandleState(Task<JObject> task)
        {
            JObject json = task.Result;
            if (json == null)
                return;

            StateDto state = json.ToObject<StateDto>();
            if (state == null || !state.Success)
                return;

            _state = state;
            ApplyState(state);

            if (state.Tick != _lastTick)
            {
                _lastTick = state.Tick;
                RequestCandles();
            }
        }

        private void RequestCandles()
        {
            if (string.IsNullOrEmpty(_selectedStock))
                return;

            _candleTask = StockApi.GetCandlesAsync(_selectedStock);
        }

        private void HandleCandles(Task<JObject> task)
        {
            JObject json = task.Result;
            if (json == null)
                return;

            CandlesDto dto = json.ToObject<CandlesDto>();
            if (dto == null || !dto.Success)
                return;

            _chart.SetData(dto.Candles);
            UpdateAxes();
        }

        private void ApplyState(StateDto state)
        {
            if (state.Stocks.Count == 0)
            {
                _header.text = "stockmarket/ui/no-data".Localized();
                _chart.Clear();
                UpdateAxes();
                return;
            }

            StockDto stock = state.Stocks.Find(s => s.Id == _selectedStock);
            if (stock == null)
            {
                stock = state.Stocks[0];
                _selectedStock = stock.Id;
            }

            _header.text = stock.NameKey.Localized();

            _chart.SetColors(ClientConfig.UpColor, ClientConfig.DownColor);
            _chart.SetVisibleCount(ResolveVisibleCount());
            ApplyButtonColors();

            _currentSymbol = Symbol(stock.Currency);

            _price.text = _currentSymbol + Format(stock.Price);
            _price.color = new Color(0.86f, 0.86f, 0.80f, 1f);

            // 涨跌幅相对【昨收】（服务端计算），不是相对上一根 K 线
            _change.text = string.Format(
                "{0}{1}  ({2:0.00}%)",
                stock.Change >= 0m ? "+" : "",
                Format(stock.Change),
                stock.ChangePct);
            _change.color = stock.Change >= 0m ? ClientConfig.UpColor : ClientConfig.DownColor;

            HoldingDto holding = state.Holdings.Find(h => h.StockId == stock.Id);
            if (holding == null)
            {
                _holding.text = "stockmarket/ui/holding".Localized() + ": " +
                                "stockmarket/ui/no-holdings".Localized();
                _sellable.text = "stockmarket/ui/sellable".Localized() + ": -";
                _avgCost.text = "stockmarket/ui/avg-cost".Localized() + ": -";
                _value.text = "stockmarket/ui/value".Localized() + ": -";
                _unlock.text = "";
            }
            else
            {
                _holding.text = "stockmarket/ui/holding".Localized() + ": " + holding.Shares;
                _sellable.text = "stockmarket/ui/sellable".Localized() + ": " + holding.Sellable;
                _avgCost.text = "stockmarket/ui/avg-cost".Localized() + ": " +
                                _currentSymbol + Format(holding.AvgCost);
                _value.text = "stockmarket/ui/value".Localized() + ": " +
                              _currentSymbol + Format(holding.Value);
                _unlock.text = holding.NextUnlockIn > 0
                    ? string.Format("stockmarket/ui/next-unlock".Localized(),
                        FormatDuration(holding.NextUnlockIn))
                    : "";
            }

            _balance.text =
                "stockmarket/ui/balance".Localized() + ": " +
                _currentSymbol + Format(state.Balances.RUB) + "  $" + Format(state.Balances.USD) +
                "  €" + Format(state.Balances.EUR);

            _amount.text = "stockmarket/ui/amount".Localized() + ": " + _amountValue;

            if (stock.Halted)
                _status.text = "stockmarket/ui/halted".Localized();

            // 现价线 + 昨收参考线 + 持仓成本线（成本同时参与 Y 轴范围）
            _chart.SetPriceLines(stock.Price, stock.PrevClose, holding?.AvgCost);
            UpdateAxes();

            UpdateButtonState(stock, holding);
        }

        private void UpdateAxes()
        {
            IReadOnlyList<CandleDto> candles = _chart.Candles;
            int start = _chart.VisibleStart;

            bool hasData = candles.Count > 0 && start < candles.Count;

            for (int i = 0; i < _yLabels.Length; i++)
            {
                if (!hasData)
                {
                    _yLabels[i].text = "--";
                    continue;
                }

                float fraction = i / (float)(_yLabels.Length - 1);
                decimal value = _chart.ViewMin +
                                (_chart.ViewMax - _chart.ViewMin) * (decimal)fraction;
                _yLabels[i].text = _currentSymbol + Format(value);
            }

            if (!hasData)
            {
                foreach (TMP_Text label in _timeLabels)
                    label.text = "";
                return;
            }

            int end = candles.Count - 1;
            int mid = start + (end - start) / 2;

            _timeLabels[0].text = MarketTime.Short(candles[start].T);
            _timeLabels[1].text = MarketTime.Short(candles[mid].T);
            _timeLabels[2].text = MarketTime.Short(candles[end].T);
        }

        /// <summary>
        /// 买卖按钮的底色跟随配色方案：
        /// 红涨绿跌 → 买入红、卖出绿；绿涨红跌 → 买入绿、卖出红。
        /// 避免出现"红色既代表上涨又代表卖出"的语义打架。
        /// </summary>
        private void ApplyButtonColors()
        {
            Color buy = Darken(ClientConfig.UpColor, 0.42f);
            Color sell = Darken(ClientConfig.DownColor, 0.42f);

            if (_buy != null && buy != _buyColor)
            {
                _buyColor = buy;
                (_buy.targetGraphic as Image)?.color = buy;
            }

            if (_sell != null && sell != _sellColor)
            {
                _sellColor = sell;
                (_sell.targetGraphic as Image)?.color = sell;
            }
        }

        private static Color Darken(Color color, float factor) =>
            new Color(color.r * factor, color.g * factor, color.b * factor, 0.95f);

        private void UpdateButtonState(StockDto stock, HoldingDto holding)
        {
            bool inRaid = IsInRaid();
            bool tradable = !inRaid && !stock.Halted;

            if (_buy != null)
                _buy.interactable = tradable && _amountValue > 0;

            if (_sell != null)
                _sell.interactable = tradable && holding != null && holding.Sellable >= _amountValue;
        }

        // ScreenController 在 BaseScreen<,,> 里是 protected，
        // 从 mod 里拿不到，只能反射一次并缓存。
        private static readonly PropertyInfo ScreenControllerProperty =
            typeof(InventoryScreen).GetProperty(
                "ScreenController",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static PropertyInfo _inRaidProperty;

        private bool IsInRaid()
        {
            try
            {
                object controller = ScreenControllerProperty?.GetValue(_screen);
                if (controller == null)
                    return false;

                if (_inRaidProperty == null)
                    _inRaidProperty = controller.GetType().GetProperty("InRaid");

                return _inRaidProperty != null && (bool)(_inRaidProperty.GetValue(controller) ?? false);
            }
            catch
            {
                return false;
            }
        }

        // ══════════════════ 操作 ══════════════════

        private void AddAmount(int delta)
        {
            _amountValue = Mathf.Clamp(_amountValue + delta, 1, 100000);
            _amount.text = "stockmarket/ui/amount".Localized() + ": " + _amountValue;

            if (_state != null)
            {
                StockDto stock = _state.Stocks.Find(s => s.Id == _selectedStock);
                HoldingDto holding = _state.Holdings.Find(h => h.StockId == _selectedStock);
                if (stock != null)
                    UpdateButtonState(stock, holding);
            }
        }

        private void OnBuy()
        {
            if (_tradeTask != null || string.IsNullOrEmpty(_selectedStock))
                return;

            if (IsInRaid())
            {
                SetStatus("stockmarket/error/market_closed".Localized());
                return;
            }

            SetStatus("stockmarket/ui/loading".Localized());
            _tradeTask = StockApi.BuyAsync(_selectedStock, _amountValue);
        }

        private void OnSell()
        {
            if (_tradeTask != null || string.IsNullOrEmpty(_selectedStock))
                return;

            if (IsInRaid())
            {
                SetStatus("stockmarket/error/market_closed".Localized());
                return;
            }

            SetStatus("stockmarket/ui/loading".Localized());
            _tradeTask = StockApi.SellAsync(_selectedStock, _amountValue);
        }

        private void HandleTradeResult(Task<JObject> task)
        {
            JObject json = task.Result;
            if (json == null)
            {
                SetStatus("stockmarket/error/profile_unavailable".Localized());
                return;
            }

            TradeResultDto result = json.ToObject<TradeResultDto>();
            if (result == null)
                return;

            if (!result.Success)
            {
                string key = result.MessageKey ?? "stockmarket/error/invalid_amount";
                SetStatus(key.Localized());
                return;
            }

            string text = result.MessageKey != null
                ? string.Format(result.MessageKey.Localized(), result.Shares ?? 0, result.Total ?? 0)
                : "OK";
            SetStatus(text, ClientConfig.UpColor);
        }

        private void SetStatus(string text, Color? color = null)
        {
            if (_status == null)
                return;

            _status.text = text ?? "";
            _status.color = color ?? new Color(0.75f, 0.75f, 0.70f, 1f);
        }

        // ══════════════════ UI 工具 ══════════════════

        private TMP_Text CreateTopLabel(
            Transform parent, string name, float top, float height,
            float fontSize, TextAlignmentOptions alignment)
        {
            return CreateLabel(parent, name, "",
                fontSize,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -top - height), new Vector2(0f, -top),
                alignment);
        }

        private TMP_Text CreateLabel(
            Transform parent, string name, string text, float fontSize,
            Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax,
            TextAlignmentOptions alignment)
        {
            GameObject labelObject = new GameObject(
                name, typeof(RectTransform), typeof(TextMeshProUGUI));
            labelObject.transform.SetParent(parent, false);

            TMP_Text label = labelObject.GetComponent<TMP_Text>();
            if (_template != null)
            {
                label.font = _template.font;
                label.fontSharedMaterial = _template.fontSharedMaterial;
                label.fontStyle = _template.fontStyle;
            }

            label.text = text;
            label.fontSize = fontSize;
            label.enableAutoSizing = true;
            label.fontSizeMin = 8f;
            label.fontSizeMax = fontSize;
            label.enableWordWrapping = false;
            label.alignment = alignment;
            label.color = new Color(0.82f, 0.82f, 0.76f, 1f);
            label.raycastTarget = false;

            RectTransform rect = label.rectTransform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            return label;
        }

        private Button CreateButton(
            Transform parent, string name, string label,
            Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax,
            Color color)
        {
            GameObject buttonObject = new GameObject(
                name, typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);

            Image background = buttonObject.GetComponent<Image>();
            background.color = color;

            Button button = buttonObject.GetComponent<Button>();
            button.targetGraphic = background;

            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.18f, 1.18f, 1.18f, 1f);
            colors.pressedColor = new Color(0.72f, 0.72f, 0.72f, 1f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.6f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            RectTransform rect = (RectTransform)buttonObject.transform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            TMP_Text text = CreateLabel(buttonObject.transform, "Label", label,
                18f, Vector2.zero, Vector2.one,
                new Vector2(4f, 2f), new Vector2(-4f, -2f),
                TextAlignmentOptions.Center);
            text.color = new Color(0.88f, 0.88f, 0.82f, 1f);

            return button;
        }

        // ══════════════════ 格式化 ══════════════════

        private static string Symbol(string currency)
        {
            switch (currency)
            {
                case "USD": return "$";
                case "EUR": return "€";
                default: return "₽";
            }
        }

        private static string Format(decimal value) =>
            value.ToString("0.00", CultureInfo.InvariantCulture);

        private static string Format(long value) =>
            value.ToString("N0", CultureInfo.InvariantCulture);

        private static string FormatDuration(double marketSeconds)
        {
            // 入参是塔科夫秒，显示时换算成现实秒（÷7）
            double realSeconds = Math.Max(0, marketSeconds / 7.0);
            TimeSpan span = TimeSpan.FromSeconds(realSeconds);
            if (span.TotalHours >= 1)
                return $"{(int)span.TotalHours}h {span.Minutes}m";
            return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        }
    }
}
