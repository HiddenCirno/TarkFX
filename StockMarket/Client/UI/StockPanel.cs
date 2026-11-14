using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using TarkFX.StockMarket.Client.Core;
using TarkFX.StockMarket.Client.Model;
using TarkFX.StockMarket.Client.Operations;
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
        private TMP_Text _margin;
        private TMP_Text _equity;
        private TMP_Text _liq;
        private TMP_Text _pnl;
        private TMP_Text _dividend;
        private KLineGraphic _chart;

        private readonly Button[] _leverageButtons = new Button[8];
        private int _leverage = 1;

        private readonly Button[] _directionButtons = new Button[2];
        private bool _isShort;

        private TMP_InputField _leverageInput;
        private bool _suppressLeverageCallback;

        private TMP_Text _debt;
        private TMP_InputField _repayInput;
        private Button _repayButton;
        private long _repayValue;

        private readonly TMP_Text[] _yLabels = new TMP_Text[YLabelCount];
        private readonly TMP_Text[] _timeLabels = new TMP_Text[3];

        private Button _buy;
        private Button _sell;
        private TMP_InputField _amountInput;
        private bool _suppressAmountCallback;

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

        private string _currentCurrency = "RUB";
        private static FieldInfo _screenInventoryControllerField;
        private static FieldInfo _screenControllerField;
        private static PropertyInfo _inRaidProperty;

        /// <summary>当前状态栏显示的是不是"涨停/跌停/停牌"这类行情状态（离开时要清掉）</summary>
        private bool _statusIsMarketState;

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
            rect.offsetMin = new Vector2(8f, 24f);
            rect.offsetMax = new Vector2(-16f, -60f);

            Color buttonColor = new Color(0.16f, 0.18f, 0.18f, 0.95f);

            // ── 交易区（上移到这里） ──
            _price = CreateTopLabel(side.transform, "Price", 4f, 36f, 28f, TextAlignmentOptions.MidlineLeft);
            _change = CreateTopLabel(side.transform, "Change", 42f, 26f, 19f, TextAlignmentOptions.MidlineLeft);

            BuildDirectionRow(side.transform, 72f, 32f);
            BuildLeverageRow(side.transform, 108f, 32f);

            _amount = CreateTopLabel(side.transform, "Amount", 144f, 24f, 17f, TextAlignmentOptions.MidlineLeft);

            // 单行：−1000 −100 −10 −1 [输入框] +1 +10 +100 +1000
            // 元素多、列窄，按钮字体交给 autoSizing 自动缩小（数字而已，缩到 8pt 也看得清）
            const float rowTop = 172f;
            const float rowBottom = 212f;

            CreateButton(side.transform, "Minus1000", "-1000",
                new Vector2(0.000f, 1f), new Vector2(0.098f, 1f),
                new Vector2(0f, -rowBottom), new Vector2(0f, -rowTop), buttonColor)
                .onClick.AddListener((UnityAction)(() => AddAmount(-1000)));

            CreateButton(side.transform, "Minus100", "-100",
                new Vector2(0.103f, 1f), new Vector2(0.201f, 1f),
                new Vector2(0f, -rowBottom), new Vector2(0f, -rowTop), buttonColor)
                .onClick.AddListener((UnityAction)(() => AddAmount(-100)));

            CreateButton(side.transform, "Minus10", "-10",
                new Vector2(0.206f, 1f), new Vector2(0.304f, 1f),
                new Vector2(0f, -rowBottom), new Vector2(0f, -rowTop), buttonColor)
                .onClick.AddListener((UnityAction)(() => AddAmount(-10)));

            CreateButton(side.transform, "Minus1", "-1",
                new Vector2(0.309f, 1f), new Vector2(0.407f, 1f),
                new Vector2(0f, -rowBottom), new Vector2(0f, -rowTop), buttonColor)
                .onClick.AddListener((UnityAction)(() => AddAmount(-1)));

            _amountInput = CreateAmountInput(side.transform, "AmountInput",
                new Vector2(0.412f, 1f), new Vector2(0.588f, 1f),
                new Vector2(0f, -rowBottom), new Vector2(0f, -rowTop));

            CreateButton(side.transform, "Plus1", "+1",
                new Vector2(0.593f, 1f), new Vector2(0.691f, 1f),
                new Vector2(0f, -rowBottom), new Vector2(0f, -rowTop), buttonColor)
                .onClick.AddListener((UnityAction)(() => AddAmount(1)));

            CreateButton(side.transform, "Plus10", "+10",
                new Vector2(0.696f, 1f), new Vector2(0.794f, 1f),
                new Vector2(0f, -rowBottom), new Vector2(0f, -rowTop), buttonColor)
                .onClick.AddListener((UnityAction)(() => AddAmount(10)));

            CreateButton(side.transform, "Plus100", "+100",
                new Vector2(0.799f, 1f), new Vector2(0.897f, 1f),
                new Vector2(0f, -rowBottom), new Vector2(0f, -rowTop), buttonColor)
                .onClick.AddListener((UnityAction)(() => AddAmount(100)));

            CreateButton(side.transform, "Plus1000", "+1000",
                new Vector2(0.902f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -rowBottom), new Vector2(0f, -rowTop), buttonColor)
                .onClick.AddListener((UnityAction)(() => AddAmount(1000)));

            _buy = CreateButton(side.transform, "Buy", "stockmarket/ui/buy".Localized(),
                new Vector2(0f, 1f), new Vector2(0.49f, 1f),
                new Vector2(0f, -258f), new Vector2(0f, -216f),
                new Color(0.12f, 0.32f, 0.18f, 0.95f));
            _buy.onClick.AddListener((UnityAction)OnBuy);

            _sell = CreateButton(side.transform, "Sell", "stockmarket/ui/sell".Localized(),
                new Vector2(0.51f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -258f), new Vector2(0f, -216f),
                new Color(0.38f, 0.14f, 0.14f, 0.95f));
            _sell.onClick.AddListener((UnityAction)OnSell);

            // ── 持仓信息（交易区下方） ──
            _holding = CreateTopLabel(side.transform, "Holding", 270f, 26f, 17f, TextAlignmentOptions.MidlineLeft);
            _sellable = CreateTopLabel(side.transform, "Sellable", 296f, 26f, 17f, TextAlignmentOptions.MidlineLeft);
            _avgCost = CreateTopLabel(side.transform, "AvgCost", 322f, 26f, 17f, TextAlignmentOptions.MidlineLeft);
            _value = CreateTopLabel(side.transform, "Value", 348f, 26f, 17f, TextAlignmentOptions.MidlineLeft);
            _unlock = CreateTopLabel(side.transform, "Unlock", 374f, 24f, 15f, TextAlignmentOptions.MidlineLeft);
            _margin = CreateTopLabel(side.transform, "Margin", 400f, 26f, 16f, TextAlignmentOptions.MidlineLeft);
            _equity = CreateTopLabel(side.transform, "Equity", 426f, 26f, 16f, TextAlignmentOptions.MidlineLeft);
            _liq = CreateTopLabel(side.transform, "Liquidation", 452f, 26f, 16f, TextAlignmentOptions.MidlineLeft);
            _pnl = CreateTopLabel(side.transform, "Pnl", 480f, 24f, 15f, TextAlignmentOptions.MidlineLeft);
            _dividend = CreateTopLabel(side.transform, "Dividend", 506f, 24f, 15f, TextAlignmentOptions.MidlineLeft);
            _debt = CreateTopLabel(side.transform, "Debt", 532f, 24f, 15f, TextAlignmentOptions.MidlineLeft);
            // 欠债 = 亏损，用"下跌色"，跟随红涨绿跌/绿涨红跌设置
            _debt.color = ClientConfig.DownColor;

            // 盈亏/分红两行各自用富文本着色，所以标签本身保持中性色
            _pnl.richText = true;
            _dividend.richText = true;

            BuildRepayRow(side.transform, 558f, 36f);

            _balance = CreateTopLabel(side.transform, "Balance", 602f, 26f, 17f, TextAlignmentOptions.MidlineLeft);

            UpdateDirectionButtons();
            UpdateLeverageButtons();
            UpdateTradeButtonLabels();
            ApplyButtonColors();
        }

        private void BuildDirectionRow(Transform parent, float top, float height)
        {
            CreateLabel(parent, "DirectionLabel", "stockmarket/ui/direction".Localized(),
                15f,
                new Vector2(0f, 1f), new Vector2(0.17f, 1f),
                new Vector2(0f, -top - height), new Vector2(0f, -top),
                TextAlignmentOptions.MidlineLeft);

            _directionButtons[0] = CreateButton(parent, "Long", "stockmarket/ui/long".Localized(),
                new Vector2(0.18f, 1f), new Vector2(0.40f, 1f),
                new Vector2(0f, -top - height), new Vector2(0f, -top),
                LeverageColor(false));
            _directionButtons[0].onClick.AddListener((UnityAction)(() => SetDirection(false)));

            _directionButtons[1] = CreateButton(parent, "Short", "stockmarket/ui/short".Localized(),
                new Vector2(0.42f, 1f), new Vector2(0.64f, 1f),
                new Vector2(0f, -top - height), new Vector2(0f, -top),
                LeverageColor(false));
            _directionButtons[1].onClick.AddListener((UnityAction)(() => SetDirection(true)));
        }

        private void BuildLeverageRow(Transform parent, float top, float height)
        {
            // 和数量行一致的布局：[-20][-10][-5][-1] [输入框] [+1][+5][+10][+20]
            Color buttonColor = new Color(0.16f, 0.18f, 0.18f, 0.95f);

            float[] minusX0 = { 0.000f, 0.103f, 0.206f, 0.309f };
            float[] minusX1 = { 0.098f, 0.201f, 0.304f, 0.407f };
            int[] minusDeltas = { -20, -10, -5, -1 };

            for (int i = 0; i < minusDeltas.Length; i++)
            {
                int delta = minusDeltas[i];
                Button button = CreateButton(parent, "LeverageMinus" + (-delta), delta.ToString(),
                    new Vector2(minusX0[i], 1f), new Vector2(minusX1[i], 1f),
                    new Vector2(0f, -top - height), new Vector2(0f, -top), buttonColor);
                button.onClick.AddListener((UnityAction)(() => AddLeverage(delta)));
                _leverageButtons[i] = button;
            }

            _leverageInput = CreateIntegerInput(
                parent, "LeverageInput",
                new Vector2(0.412f, 1f), new Vector2(0.588f, 1f),
                new Vector2(0f, -top - height), new Vector2(0f, -top),
                3, _leverage, 1, 100, 16f,
                value =>
                {
                    if (_suppressLeverageCallback)
                        return;

                    _leverage = (int)Mathf.Clamp((int)value, 1, 100);
                    UpdateTradeButtonLabels();
                    UpdateAmountLabels();
                    RefreshButtonStateFromState();
                });

            float[] plusX0 = { 0.593f, 0.696f, 0.799f, 0.902f };
            float[] plusX1 = { 0.691f, 0.794f, 0.897f, 1.000f };
            int[] plusDeltas = { 1, 5, 10, 20 };

            for (int i = 0; i < plusDeltas.Length; i++)
            {
                int delta = plusDeltas[i];
                Button button = CreateButton(parent, "LeveragePlus" + delta, "+" + delta,
                    new Vector2(plusX0[i], 1f), new Vector2(plusX1[i], 1f),
                    new Vector2(0f, -top - height), new Vector2(0f, -top), buttonColor);
                button.onClick.AddListener((UnityAction)(() => AddLeverage(delta)));
                _leverageButtons[i + plusDeltas.Length] = button;
            }
        }

        private void BuildRepayRow(Transform parent, float top, float height)
        {
            _repayInput = CreateIntegerInput(
                parent, "RepayInput",
                new Vector2(0f, 1f), new Vector2(0.60f, 1f),
                new Vector2(0f, -top - height), new Vector2(0f, -top),
                12, 0L, 0L, long.MaxValue, 15f,
                value => { _repayValue = value; });

            _repayButton = CreateButton(parent, "Repay", "stockmarket/ui/repay".Localized(),
                new Vector2(0.62f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -top - height), new Vector2(0f, -top),
                new Color(0.34f, 0.24f, 0.12f, 0.95f));
            _repayButton.onClick.AddListener((UnityAction)OnRepay);
        }

        private static Color LeverageColor(bool selected) =>
            selected
                ? new Color(0.22f, 0.38f, 0.60f, 0.95f)
                : new Color(0.16f, 0.18f, 0.18f, 0.95f);

        private void SetDirection(bool shortSide)
        {
            _isShort = shortSide;
            UpdateDirectionButtons();
            UpdateTradeButtonLabels();
            UpdateAmountLabels();
            RefreshButtonStateFromState();
        }

        private void UpdateDirectionButtons()
        {
            for (int i = 0; i < _directionButtons.Length; i++)
            {
                Button button = _directionButtons[i];
                if (button == null)
                    continue;

                bool selected = (i == 1) == _isShort;
                Image image = button.targetGraphic as Image;
                if (image != null)
                    image.color = LeverageColor(selected);
            }
        }

        private void AddLeverage(int delta)
        {
            SetLeverage(_leverage + delta);
        }

        private void SetLeverage(int value)
        {
            _leverage = Mathf.Clamp(value, 1, 100);

            if (_leverageInput != null)
            {
                _suppressLeverageCallback = true;
                _leverageInput.text = _leverage.ToString(CultureInfo.InvariantCulture);
                _suppressLeverageCallback = false;
            }

            UpdateTradeButtonLabels();
            UpdateAmountLabels();
            RefreshButtonStateFromState();
        }

        private void UpdateLeverageButtons()
        {
            // 加减按钮没有"选中态"，保持中性色（创建时已设置）
        }

        private void UpdateTradeButtonLabels()
        {
            string buyKey;
            string sellKey;

            if (_isShort)
            {
                buyKey = "stockmarket/ui/open-short";
                sellKey = "stockmarket/ui/close-short";
            }
            else if (_leverage > 1)
            {
                buyKey = "stockmarket/ui/open-long";
                sellKey = "stockmarket/ui/close-position";
            }
            else
            {
                buyKey = "stockmarket/ui/buy";
                sellKey = "stockmarket/ui/sell";
            }

            SetButtonLabel(_buy, buyKey.Localized());
            SetButtonLabel(_sell, sellKey.Localized());
        }

        private static void SetButtonLabel(Button button, string text)
        {
            if (button == null)
                return;

            TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
                label.text = text;
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

            if (_stateTask != null && _stateTask.IsCompleted)
            {
                // 先清空再处理：即使 ApplyState 抛异常，下一轮轮询也不会被卡死
                Task<JObject> stateTask = _stateTask;
                _stateTask = null;

                try
                {
                    HandleState(stateTask);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[StockMarket] 处理 state 失败：{ex}");
                }
            }

            if (_candleTask != null && _candleTask.IsCompleted)
            {
                HandleCandles(_candleTask);
                _candleTask = null;
            }

            if (_stateTask == null && Time.realtimeSinceStartup >= _nextPoll)
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
            _chart.SetYAxisZoom(ClientConfig.YAxisZoom.Value);
            ApplyButtonColors();

            _currentSymbol = Symbol(stock.Currency);
            _currentCurrency = stock.Currency;

            _price.text = _currentSymbol + FormatPrice(stock.Price);
            _price.color = new Color(0.86f, 0.86f, 0.80f, 1f);

            // 涨跌幅相对【昨收】（服务端计算），不是相对上一根 K 线
            _change.text = string.Format(
                "{0}{1}  ({2:0.00}%)",
                stock.Change >= 0m ? "+" : "",
                FormatPrice(stock.Change),
                stock.ChangePct);
            _change.color = stock.Change >= 0m ? ClientConfig.UpColor : ClientConfig.DownColor;

            HoldingDto holding = state.Holdings.Find(h => h.StockId == stock.Id);
            bool hasShort = holding != null && holding.ShortLeverage > 0;

            if (_isShort)
            {
                if (holding == null || !hasShort)
                {
                    _holding.text = "stockmarket/ui/short-position".Localized() + ": " +
                                    "stockmarket/ui/no-holdings".Localized();
                    _sellable.text = "stockmarket/ui/closable".Localized() + ": -";
                    _avgCost.text = "stockmarket/ui/short-entry".Localized() + ": -";
                    _value.text = "stockmarket/ui/proceeds".Localized() + ": -";
                    _unlock.text = "";
                    _margin.text = "";
                    _equity.text = "";
                    _liq.text = "";
                }
                else
                {
                    _holding.text = "stockmarket/ui/short-position".Localized() + ": " +
                                    holding.ShortShares + " " +
                                    "stockmarket/ui/shares".Localized() +
                                    "  (" + holding.ShortLeverage + "x)";
                    _sellable.text = "stockmarket/ui/closable".Localized() + ": " +
                                     (holding.ShortUnlockIn <= 0 ? holding.ShortShares : 0);
                    _avgCost.text = "stockmarket/ui/short-entry".Localized() + ": " +
                                    _currentSymbol + FormatPrice(holding.ShortEntry);
                    _value.text = "stockmarket/ui/proceeds".Localized() + ": " +
                                  _currentSymbol + FormatPrice(holding.ShortProceeds);
                    _unlock.text = holding.ShortUnlockIn > 0
                        ? "stockmarket/ui/t-plus-one".Localized() + ": " +
                          FormatDuration(holding.ShortUnlockIn)
                        : "";
                    _margin.text = "stockmarket/ui/margin".Localized() + ": " +
                                   _currentSymbol + FormatPrice(holding.ShortMargin);
                    _equity.text = "stockmarket/ui/equity".Localized() + ": " +
                                   _currentSymbol + FormatPrice(holding.ShortEquity);
                    _liq.text = "stockmarket/ui/liquidation".Localized() + ": " +
                                _currentSymbol + FormatPrice(holding.ShortLiquidationPrice) +
                                "  (" + holding.ShortLeverage + "x · " + holding.ShortShares + ")";
                }
            }
            else
            {
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
                    // 杠杆仓没有现货批次时 Shares 也是 0，必须把 LeverageShares 一起算进来
                    int totalShares = holding.Shares + holding.LeverageShares;
                    decimal avgCost = holding.Shares > 0 ? holding.AvgCost : holding.LeverageEntry;
                    int sellableShown = holding.Shares > 0
                        ? holding.Sellable
                        : (holding.Leverage > 0 && holding.LeverageUnlockIn <= 0
                            ? holding.LeverageShares
                            : 0);

                    _holding.text = "stockmarket/ui/holding".Localized() + ": " +
                                    (totalShares > 0
                                        ? totalShares.ToString(CultureInfo.InvariantCulture)
                                        : "stockmarket/ui/no-holdings".Localized());
                    _sellable.text = "stockmarket/ui/sellable".Localized() + ": " + sellableShown;
                    _avgCost.text = "stockmarket/ui/avg-cost".Localized() + ": " +
                                    (totalShares > 0
                                        ? _currentSymbol + FormatPrice(avgCost)
                                        : "-");
                    _value.text = "stockmarket/ui/value".Localized() + ": " +
                                  (totalShares > 0
                                      ? _currentSymbol + FormatPrice(stock.Price * totalShares)
                                      : "-");
                    _unlock.text = holding.NextUnlockIn > 0
                        ? string.Format("stockmarket/ui/next-unlock".Localized(),
                            FormatDuration(holding.NextUnlockIn))
                        : "";
                }

                if (holding == null || holding.Leverage <= 0)
                {
                    _margin.text = "";
                    _equity.text = "";
                    _liq.text = "";
                }
                else
                {
                    _margin.text = "stockmarket/ui/margin".Localized() + ": " +
                                   _currentSymbol + FormatPrice(holding.LeverageMargin);
                    _equity.text = "stockmarket/ui/equity".Localized() + ": " +
                                   _currentSymbol + FormatPrice(holding.LeverageEquity);
                    _liq.text = "stockmarket/ui/liquidation".Localized() + ": " +
                                _currentSymbol + FormatPrice(holding.LiquidationPrice) +
                                "  (" + holding.Leverage + "x · " + holding.LeverageShares + ")";

                    if (holding.LeverageUnlockIn > 0)
                    {
                        string lockText = "stockmarket/ui/t-plus-one".Localized() + ": " +
                                          FormatDuration(holding.LeverageUnlockIn);
                        _unlock.text = string.IsNullOrEmpty(_unlock.text)
                            ? lockText
                            : _unlock.text + "   " + lockText;
                    }
                }
            }

            // ── 盈亏：只展示账面（浮动）与变现（立刻平仓的净额），不展示已实现/总盈亏 ──
            if (holding == null)
            {
                _pnl.text = "stockmarket/ui/pnl".Localized() + ": -";
                _dividend.text = "stockmarket/ui/dividend-expected".Localized() + ": -";
            }
            else
            {
                _pnl.text = "stockmarket/ui/pnl".Localized() + ": " +
                            "stockmarket/ui/pnl-book".Localized() + " " +
                            Colored(SignedMoney(holding.UnrealizedPnl),
                                    PnlColor(holding.UnrealizedPnl)) +
                            "  " +
                            "stockmarket/ui/pnl-liquid".Localized() + " " +
                            Colored(SignedMoney(holding.LiquidationPnl),
                                    PnlColor(holding.LiquidationPnl));

                // 分红单列一行；预计分红属于收益，用"上涨色"
                _dividend.text = "stockmarket/ui/dividend-expected".Localized() + ": " +
                                 Colored(SignedMoney(holding.ExpectedDividend),
                                         holding.ExpectedDividend > 0m
                                             ? ClientConfig.UpColor
                                             : NeutralColor);
            }

            _balance.text =
                "stockmarket/ui/balance".Localized() + ": " +
                _currentSymbol + Format(state.Balances.RUB) + "  $" + Format(state.Balances.USD) +
                "  €" + Format(state.Balances.EUR);

            _debt.text = "stockmarket/ui/debt".Localized() + ": " +
                         _currentSymbol + Format(state.Debts.RUB) + "  $" + Format(state.Debts.USD) +
                         "  €" + Format(state.Debts.EUR);
            _debt.color = ClientConfig.DownColor;

            bool canRepay = state.DebtBlocked && !IsInRaid();
            if (_repayButton != null) _repayButton.interactable = canRepay;
            if (_repayInput != null) _repayInput.interactable = canRepay;

            UpdateAmountLabels();

            if (stock.CircuitBreakerActive)
            {
                double breakerHours = stock.CircuitBreakerRemainingTicks * 0.25;
                _status.text = string.Format(
                    "stockmarket/ui/circuit-breaker".Localized(), breakerHours);
                _statusIsMarketState = true;
            }
            else if (stock.AtLimitUp)
            {
                _status.text = "stockmarket/ui/limit-up".Localized();
                _statusIsMarketState = true;
            }
            else if (stock.AtLimitDown)
            {
                _status.text = "stockmarket/ui/limit-down".Localized();
                _statusIsMarketState = true;
            }
            else if (stock.Halted)
            {
                _status.text = "stockmarket/ui/halted".Localized();
                _statusIsMarketState = true;
            }
            else if (_statusIsMarketState)
            {
                _status.text = "";
                _statusIsMarketState = false;
            }

            // 现价线 + 昨收参考线 + 成本线。
            // 现货为空时 GetHolding 会返回 0 成本，绝不能把 0 塞进 Y 轴范围
            // （会把整张图压成一条平线）。杠杆仓改用开仓价作为成本线。
            decimal? costLine = null;
            if (holding != null)
            {
                if (holding.Shares > 0 && holding.AvgCost > 0m)
                    costLine = holding.AvgCost;
                else if (holding.Leverage > 0 && holding.LeverageEntry > 0m)
                    costLine = holding.LeverageEntry;
                else if (holding.ShortLeverage > 0 && holding.ShortEntry > 0m)
                    costLine = holding.ShortEntry;
            }

            _chart.SetPriceLines(stock.Price, stock.PrevClose, costLine);
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
                _yLabels[i].text = _currentSymbol + FormatPrice(value);
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
            bool blocked = _state != null && _state.DebtBlocked;
            bool tradable = !inRaid && !stock.Halted && !blocked;
            bool hasLeverage = holding != null && holding.Leverage > 0;
            bool hasShort = holding != null && holding.ShortLeverage > 0;

            if (_isShort)
            {
                bool canBorrow = stock.LenderAvailable >= _amountValue;
                if (_buy != null)
                    _buy.interactable = tradable && !hasShort && canBorrow && _amountValue > 0;
                if (_sell != null)
                    _sell.interactable = tradable && hasShort && holding.ShortUnlockIn <= 0;
                return;
            }

            int total = holding == null
                ? 0
                : holding.Shares + holding.LeverageShares + holding.ShortShares;
            bool withinCap = _state == null || total + _amountValue <= _state.MaxHoldingShares;

            if (_leverage > 1)
            {
                // 杠杆模式：开仓要求当前没有杠杆仓；平仓要求存在杠杆仓
                if (_buy != null)
                    _buy.interactable = tradable && !hasLeverage && !hasShort && withinCap;
                if (_sell != null)
                    _sell.interactable = tradable && hasLeverage &&
                                         holding.LeverageUnlockIn <= 0;
                return;
            }

            if (_buy != null)
                _buy.interactable = tradable && _amountValue > 0 && !hasShort && withinCap;

            if (_sell != null)
                _sell.interactable = tradable && holding != null && holding.Sellable >= _amountValue;
        }

        // BaseScreen<,,>.ScreenController 是 protected **字段**（不是属性），
        // 而且声明在泛型基类上；沿继承链逐层找 DeclaredOnly 字段最稳。
        private static object GetScreenController(InventoryScreen screen)
        {
            if (_screenControllerField == null)
            {
                for (Type type = typeof(InventoryScreen); type != null; type = type.BaseType)
                {
                    _screenControllerField = type.GetField(
                        "ScreenController",
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                    if (_screenControllerField != null)
                        break;
                }
            }

            return _screenControllerField?.GetValue(screen);
        }

        private bool IsInRaid()
        {
            try
            {
                object controller = GetScreenController(_screen);
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
            SetAmount(_amountValue + delta);
        }

        /// <summary>统一的数量入口：按钮、输入框结束编辑都走这里。</summary>
        private void SetAmount(int value)
        {
            _amountValue = Mathf.Clamp(value, 1, 1_000_000);

            if (_amountInput != null)
            {
                _suppressAmountCallback = true;
                _amountInput.text = _amountValue.ToString(CultureInfo.InvariantCulture);
                _suppressAmountCallback = false;
            }

            UpdateAmountLabels();
            RefreshButtonStateFromState();
        }

        private void UpdateAmountLabels()
        {
            if (_amount == null)
                return;

            string text = "stockmarket/ui/amount".Localized() + ": " + _amountValue;

            // 杠杆/做空时顺带显示需要的保证金与券源
            if ((_leverage > 1 || _isShort) && _state != null)
            {
                StockDto stock = _state.Stocks.Find(s => s.Id == _selectedStock);
                if (stock != null)
                {
                    long margin = (long)Math.Ceiling(stock.Price * _amountValue / _leverage);
                    text += "   " + "stockmarket/ui/margin".Localized() +
                            " ≈ " + _currentSymbol + Format(margin);

                    if (_isShort)
                    {
                        text += "   " + "stockmarket/ui/lender".Localized() +
                                ": " + Format(stock.LenderAvailable);
                    }
                }
            }

            _amount.text = text;
        }

        private void RefreshButtonStateFromState()
        {
            if (_state == null)
                return;

            StockDto stock = _state.Stocks.Find(s => s.Id == _selectedStock);
            HoldingDto holding = _state.Holdings.Find(h => h.StockId == _selectedStock);
            if (stock != null)
                UpdateButtonState(stock, holding);
        }

        private TMP_InputField CreateAmountInput(
            Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            GameObject inputObject = new GameObject(
                name, typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(TMP_InputField));
            inputObject.transform.SetParent(parent, false);

            Image background = inputObject.GetComponent<Image>();
            background.color = new Color(0.07f, 0.08f, 0.08f, 0.95f);

            RectTransform rect = (RectTransform)inputObject.transform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            TextMeshProUGUI text = CreateInputText(
                inputObject.transform, "Text", "1", new Color(0.90f, 0.90f, 0.84f, 1f));
            TextMeshProUGUI placeholder = CreateInputText(
                inputObject.transform, "Placeholder", "1", new Color(0.60f, 0.60f, 0.56f, 0.65f));

            TMP_InputField input = inputObject.GetComponent<TMP_InputField>();
            input.targetGraphic = background;
            input.textComponent = text;
            input.placeholder = placeholder;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.contentType = TMP_InputField.ContentType.IntegerNumber;
            input.characterLimit = 7;

            _suppressAmountCallback = true;
            input.text = _amountValue.ToString(CultureInfo.InvariantCulture);
            _suppressAmountCallback = false;

            input.onValueChanged.AddListener((UnityAction<string>)(value =>
            {
                if (_suppressAmountCallback)
                    return;

                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int parsed))
                    return;

                _amountValue = Mathf.Clamp(parsed, 1, 1_000_000);
                UpdateAmountLabels();
                RefreshButtonStateFromState();
            }));

            input.onEndEdit.AddListener((UnityAction<string>)(value =>
            {
                int parsed = 1;
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int typed))
                    parsed = typed;

                SetAmount(parsed);
            }));

            return input;
        }

        /// <summary>
        /// 通用整数输入框（杠杆/还款用）。回车或失焦时夹到 [min, max] 并回写。
        /// </summary>
        private TMP_InputField CreateIntegerInput(
            Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax,
            int characterLimit, long initialValue, long minValue, long maxValue,
            float fontSize, Action<long> onChanged)
        {
            GameObject inputObject = new GameObject(
                name, typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(TMP_InputField));
            inputObject.transform.SetParent(parent, false);

            Image background = inputObject.GetComponent<Image>();
            background.color = new Color(0.07f, 0.08f, 0.08f, 0.95f);

            RectTransform rect = (RectTransform)inputObject.transform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            TextMeshProUGUI text = CreateInputText(
                inputObject.transform, "Text", initialValue.ToString(CultureInfo.InvariantCulture),
                new Color(0.90f, 0.90f, 0.84f, 1f));
            TextMeshProUGUI placeholder = CreateInputText(
                inputObject.transform, "Placeholder", initialValue.ToString(CultureInfo.InvariantCulture),
                new Color(0.60f, 0.60f, 0.56f, 0.65f));

            text.fontSize = fontSize;
            placeholder.fontSize = fontSize;

            TMP_InputField input = inputObject.GetComponent<TMP_InputField>();
            input.targetGraphic = background;
            input.textComponent = text;
            input.placeholder = placeholder;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.contentType = TMP_InputField.ContentType.IntegerNumber;
            input.characterLimit = characterLimit;
            input.text = initialValue.ToString(CultureInfo.InvariantCulture);

            input.onValueChanged.AddListener((UnityAction<string>)(value =>
            {
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out long parsed))
                    return;

                onChanged?.Invoke(Clamp(parsed, minValue, maxValue));
            }));

            input.onEndEdit.AddListener((UnityAction<string>)(value =>
            {
                long parsed = initialValue;
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out long typed))
                    typed = minValue;

                parsed = Clamp(typed, minValue, maxValue);
                input.text = parsed.ToString(CultureInfo.InvariantCulture);
                onChanged?.Invoke(parsed);
            }));

            return input;
        }

        private static long Clamp(long value, long min, long max) =>
            value < min ? min : value > max ? max : value;

        private TextMeshProUGUI CreateInputText(
            Transform parent, string name, string text, Color color)
        {
            GameObject labelObject = new GameObject(
                name, typeof(RectTransform), typeof(TextMeshProUGUI));
            labelObject.transform.SetParent(parent, false);

            TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
            if (_template != null)
            {
                label.font = _template.font;
                label.fontSharedMaterial = _template.fontSharedMaterial;
                label.fontStyle = _template.fontStyle;
            }

            label.text = text;
            label.fontSize = 16f;
            label.enableAutoSizing = true;
            label.fontSizeMin = 10f;
            label.fontSizeMax = 16f;
            label.alignment = TextAlignmentOptions.Center;
            label.color = color;
            label.raycastTarget = false;

            RectTransform rect = label.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(6f, 2f);
            rect.offsetMax = new Vector2(-6f, -2f);

            return label;
        }

        private void OnBuy()
        {
            if (string.IsNullOrEmpty(_selectedStock))
                return;

            if (IsInRaid())
            {
                SetStatus("stockmarket/error/market_closed".Localized());
                return;
            }

            if (_state != null && _state.DebtBlocked)
            {
                SetStatus("stockmarket/error/debt_outstanding".Localized());
                return;
            }

            if (_isShort)
                ExecuteTrade("open_short");
            else if (_leverage > 1)
                ExecuteTrade("open_long");
            else
                ExecuteTrade("buy");
        }

        private void OnSell()
        {
            if (string.IsNullOrEmpty(_selectedStock))
                return;

            if (IsInRaid())
            {
                SetStatus("stockmarket/error/market_closed".Localized());
                return;
            }

            if (_state != null && _state.DebtBlocked)
            {
                SetStatus("stockmarket/error/debt_outstanding".Localized());
                return;
            }

            if (_isShort)
                ExecuteTrade("close_short");
            else if (_leverage > 1)
                ExecuteTrade("close_long");
            else
                ExecuteTrade("sell");
        }

        /// <summary>把交易塞进原版 item/moving 队列；扣钱与 ProfileChanges 都由服务端处理。</summary>
        private void ExecuteTrade(string op)
        {
            InventoryController controller = GetInventoryController();
            if (controller == null)
            {
                SetStatus("stockmarket/error/inventory_unavailable".Localized());
                return;
            }

            StockTradeCommand command = new StockTradeCommand
            {
                StockId = _selectedStock,
                Shares = _amountValue,
                Leverage = _leverage,
                Op = op
            };

            StockTradeOperation operation = new StockTradeOperation(
                controller.GetAndIncrementNextOperationId(), controller, command);

            controller.Execute(operation, null);

            SetStatus("stockmarket/ui/submitted".Localized());
            _nextPoll = 0f;
        }

        private void OnRepay()
        {
            if (IsInRaid())
            {
                SetStatus("stockmarket/error/market_closed".Localized());
                return;
            }

            if (_repayValue <= 0)
            {
                SetStatus("stockmarket/error/invalid_amount".Localized());
                return;
            }

            InventoryController controller = GetInventoryController();
            if (controller == null)
            {
                SetStatus("stockmarket/error/inventory_unavailable".Localized());
                return;
            }

            StockRepayCommand command = new StockRepayCommand
            {
                Currency = _currentCurrency,
                Amount = _repayValue
            };

            StockRepayOperation operation = new StockRepayOperation(
                controller.GetAndIncrementNextOperationId(), controller, command);

            controller.Execute(operation, null);

            _repayValue = 0;
            if (_repayInput != null)
                _repayInput.text = "0";

            SetStatus("stockmarket/ui/submitted".Localized());
            _nextPoll = 0f;
        }

        private InventoryController GetInventoryController()
        {
            try
            {
                // 首选 InventoryScreen 自己的私有字段（Show 里赋值，点击时一定已就绪）
                if (_screenInventoryControllerField == null)
                {
                    _screenInventoryControllerField = typeof(InventoryScreen).GetField(
                        "_inventoryController",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }

                if (_screenInventoryControllerField?.GetValue(_screen) is InventoryController direct)
                    return direct;

                // 兜底：ScreenController.InventoryController
                object screenController = GetScreenController(_screen);
                if (screenController == null)
                    return null;

                FieldInfo field = screenController.GetType().GetField(
                    "InventoryController",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                return field?.GetValue(screenController) as InventoryController;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[StockMarket] 获取 InventoryController 失败：{ex}");
                return null;
            }
        }

        private void SetStatus(string text, Color? color = null)
        {
            if (_status == null)
                return;

            _statusIsMarketState = false;
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

        /// <summary>
        /// 价格显示：1 万级卢布股用千分位整数（10,150），
        /// 100 起步的美元/欧元股仍保留两位小数（100.50）。
        /// </summary>
        private static string FormatPrice(decimal value) =>
            Math.Abs(value) >= 1000m
                ? value.ToString("N0", CultureInfo.InvariantCulture)
                : value.ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>带符号的金额，例如 +₽12,300 / -₽450 / ₽0。</summary>
        private string SignedMoney(decimal value)
        {
            if (value == 0m)
                return _currentSymbol + "0";

            return (value > 0m ? "+" : "-") + _currentSymbol + FormatPrice(Math.Abs(value));
        }

        /// <summary>盈利用上涨色、亏损用下跌色，0 用中性色（跟随红涨绿跌/绿涨红跌设置）。</summary>
        private static Color PnlColor(decimal value) =>
            value > 0m ? ClientConfig.UpColor :
            value < 0m ? ClientConfig.DownColor : NeutralColor;

        private static readonly Color NeutralColor = new Color(0.82f, 0.82f, 0.76f, 1f);

        /// <summary>TextMeshPro 富文本着色，用于一行里分别给账面/变现上色。</summary>
        private static string Colored(string text, Color color) =>
            "<color=#" + ColorUtility.ToHtmlStringRGB(color) + ">" + text + "</color>";

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
