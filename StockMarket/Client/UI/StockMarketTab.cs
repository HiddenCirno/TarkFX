using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EFT;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TarkFX.StockMarket.Client.UI
{
    /// <summary>
    /// 在背包界面顶部注入“交易所”Tab。
    ///
    /// <para>
    /// 做法与 OperatorTraits 一致（已实测）：
    /// 克隆 Skills 的 Tab 预制体 → 清掉它自带的 LocalizedText →
    /// 往 <c>_tabDictionary</c> 里插一个自定义 key →
    /// InventoryScreen.Show 内部会用这个字典重建 TabGroup，所以新 Tab 自动被纳入选中逻辑。
    /// </para>
    ///
    /// <para>
    /// ⚠ 补丁必须是 **Prefix**：TabGroup 是在 Show 方法体里用 _tabDictionary 构造的，
    /// 晚于 Prefix 就行，晚于方法体（Postfix）就晚了。
    /// </para>
    /// </summary>
    internal static class StockMarketTab
    {
        private const string TabObjectName = "TarkFXStockMarketTab";
        private const string PanelObjectName = "TarkFXStockMarketPanel";

        // 避开现有枚举值（4.1 的 Prestige = 8）
        internal const EInventoryTab TabKey = (EInventoryTab)1000;

        internal static bool TryAttach(InventoryScreen screen)
        {
            if (screen == null)
                return false;

            if (FindDeep(screen.transform, TabObjectName) != null)
                return true;

            if (screen._tabDictionary == null ||
                !screen._tabDictionary.TryGetValue(EInventoryTab.Skills, out Tab skillsTab) ||
                skillsTab == null || screen._tasksScreen == null)
                return false;

            GameObject tabObject = Object.Instantiate(
                skillsTab.gameObject, skillsTab.transform.parent, false);
            tabObject.name = TabObjectName;
            tabObject.transform.SetSiblingIndex(skillsTab.transform.GetSiblingIndex());

            Tab clonedTab = tabObject.GetComponent<Tab>();
            RemoveCopiedLocalization(tabObject, clonedTab);
            SetAllLabels(tabObject, "stockmarket/ui/tab".Localized());

            TMP_Text template = tabObject
                .GetComponentsInChildren<TMP_Text>(true)
                .FirstOrDefault();
            GameObject panel = CreatePanel(screen, template);

            clonedTab.Init(new StockTabController(panel));
            clonedTab.UpdateVisual(false);
            clonedTab.SetInteractable(true);

            RegisterTab(screen, skillsTab, clonedTab);
            InstallRowLayout(screen, skillsTab.transform.parent);

            Plugin.Log.LogInfo("[StockMarket] 已注入交易所 Tab。");
            return true;
        }

        private static void RegisterTab(
            InventoryScreen screen,
            Tab skillsTab,
            Tab stockTab)
        {
            var tabs = new Dictionary<EInventoryTab, Tab>();

            foreach (KeyValuePair<EInventoryTab, Tab> entry in screen._tabDictionary)
            {
                if (entry.Value == skillsTab)
                    tabs.Add(TabKey, stockTab);
                tabs.Add(entry.Key, entry.Value);
            }

            screen._tabDictionary = tabs;
        }

        private static void InstallRowLayout(InventoryScreen screen, Transform tabRow)
        {
            StockTabRowLayout layout =
                tabRow.gameObject.GetComponent<StockTabRowLayout>() ??
                tabRow.gameObject.AddComponent<StockTabRowLayout>();
            layout.Initialize(screen._tabDictionary.Values);
        }

        private static void SetAllLabels(GameObject root, string value)
        {
            foreach (TMP_Text label in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (string.IsNullOrWhiteSpace(label.text))
                    continue;

                label.text = value;
                label.enableAutoSizing = true;
                label.fontSizeMin = 10f;
                label.fontSizeMax = label.fontSize;
                label.enableWordWrapping = false;
            }
        }

        private static void RemoveCopiedLocalization(GameObject tabObject, Tab tab)
        {
            // 克隆出来的 Tab 还挂着原版 LocalizedText，它的本地化刷新会把我们的文字改回“技能”
            foreach (LocalizedText localizedText in
                     tabObject.GetComponentsInChildren<LocalizedText>(true))
                Object.DestroyImmediate(localizedText);

            tab.LocalizedText = null;
        }

        private static GameObject CreatePanel(InventoryScreen screen, TMP_Text template)
        {
            Transform tasksScreen = screen._tasksScreen.transform;

            GameObject panel = new GameObject(
                PanelObjectName,
                typeof(RectTransform),
                typeof(CanvasGroup),
                typeof(Image));
            panel.transform.SetParent(tasksScreen.parent, false);
            panel.transform.SetSiblingIndex(tasksScreen.GetSiblingIndex());

            RectTransform rect = (RectTransform)panel.transform;
            RectTransform nativeContent = screen._skillsAndMasteringScreen.transform as RectTransform;
            if (nativeContent != null)
            {
                rect.anchorMin = nativeContent.anchorMin;
                rect.anchorMax = nativeContent.anchorMax;
                rect.pivot = nativeContent.pivot;
                rect.anchoredPosition = nativeContent.anchoredPosition;
                rect.sizeDelta = nativeContent.sizeDelta;
            }

            Image background = panel.GetComponent<Image>();
            background.color = new Color(0.055f, 0.062f, 0.062f, 0.98f);
            background.raycastTarget = true;

            StockPanel controller = panel.AddComponent<StockPanel>();
            controller.Build(screen, template);

            panel.SetActive(false);
            panel.transform.SetAsLastSibling();
            return panel;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name)
                    return child;
            return null;
        }

        private sealed class StockTabController : ITabController
        {
            private readonly GameObject _panel;

            internal StockTabController(GameObject panel)
            {
                _panel = panel;
            }

            public void Show()
            {
                _panel.SetActive(true);
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(_panel.transform as RectTransform);
            }

            public Task<bool> TryHide()
            {
                _panel.SetActive(false);
                return Task.FromResult(true);
            }
        }
    }

    /// <summary>
    /// 顶部 Tab 行原本是固定间距，多一个 Tab 会重叠；
    /// 这里在每帧把可见 Tab 重新排一遍，给新 Tab 留出位置。
    /// （逻辑与 OperatorTraits 的 TraitsTabRowLayout 相同）
    /// </summary>
    internal sealed class StockTabRowLayout : MonoBehaviour
    {
        private readonly List<RectTransform> _tabs = new List<RectTransform>();
        private readonly Dictionary<RectTransform, float> _originalCenters =
            new Dictionary<RectTransform, float>();
        private readonly Dictionary<RectTransform, float> _originalWidths =
            new Dictionary<RectTransform, float>();

        private RectTransform _row;
        private float _originalRowWidth;

        internal void Initialize(IEnumerable<Tab> tabs)
        {
            _row = transform as RectTransform;
            _originalRowWidth = _row != null ? _row.rect.width : 0f;

            _tabs.Clear();
            _tabs.AddRange(tabs
                .Where(tab => tab != null)
                .Select(tab => tab.transform as RectTransform)
                .Where(rect => rect != null));

            _originalCenters.Clear();
            _originalWidths.Clear();
            foreach (RectTransform rect in _tabs)
            {
                _originalCenters[rect] = rect.localPosition.x;
                _originalWidths[rect] = rect.rect.width;
            }

            ApplyLayout();
        }

        private void LateUpdate()
        {
            ApplyLayout();
        }

        private void ApplyLayout()
        {
            List<RectTransform> visibleTabs = _tabs
                .Where(rect => rect != null && rect.gameObject.activeSelf)
                .ToList();
            if (visibleTabs.Count == 0)
                return;

            List<RectTransform> nativeTabs = visibleTabs
                .Where(rect => rect.name != "TarkFXStockMarketTab")
                .OrderBy(rect => _originalCenters[rect])
                .ToList();
            if (nativeTabs.Count < 2)
                return;

            float firstCenter = _originalCenters[nativeTabs.First()];
            float lastCenter = _originalCenters[nativeTabs.Last()];
            float nativeSpacing = (lastCenter - firstCenter) / (nativeTabs.Count - 1);
            if (nativeSpacing <= 0f)
                return;

            if (_row != null)
                _row.SetSizeWithCurrentAnchors(
                    RectTransform.Axis.Horizontal,
                    _originalRowWidth + nativeSpacing);

            bool inserted = false;
            float previousNativeCenter = firstCenter;

            foreach (RectTransform rect in visibleTabs)
            {
                Vector3 position = rect.localPosition;
                if (rect.name == "TarkFXStockMarketTab")
                {
                    position.x = previousNativeCenter + nativeSpacing;
                    inserted = true;
                }
                else
                {
                    position.x = _originalCenters[rect] + (inserted ? nativeSpacing : 0f);
                    previousNativeCenter = _originalCenters[rect];
                }

                rect.localPosition = position;
                rect.SetSizeWithCurrentAnchors(
                    RectTransform.Axis.Horizontal,
                    _originalWidths[rect]);
            }
        }
    }
}
