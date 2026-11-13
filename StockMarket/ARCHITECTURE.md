# StockMarket（塔科夫证券交易所）— 规划方案与架构设计

> 目标版本：SPT 4.1（`I:\TKF`）
> 形态：客户端 BepInEx 插件 + 服务端 SPT 4.1 mod
> 参考实现：`operatortraits-spt`（顶端 Tab 注入、SPT 4.1 DI、邮件发送、状态持久化）

---

## 0. 一句话架构

**服务端是唯一权威**：它按塔科夫时间推进行情、生成 K 线、落盘、执行交易；
**客户端只是浏览器**：注入一个 Tab，轮询行情并渲染 K 线，下单请求全部回服务端校验。

```
┌──────────────────────── 客户端 (BepInEx) ────────────────────────┐
│ InventoryScreen.Show (Harmony Prefix)                            │
│   └─ 克隆 Skills Tab → StockTabController : ITabController       │
│        ├─ StockPanel (uGUI，手搭)                                │
│        │    ├─ 股票列表 / 报价                                    │
│        │    ├─ KLineGraphic : MaskableGraphic（自绘蜡烛图）        │
│        │    └─ 下单区（买入/卖出/持仓/T+1 倒计时）                 │
│        └─ 5s 轮询 → RequestHandler.PostJson                      │
└──────────────────────────────────────────────────────────────────┘
                              │ HTTP (SPT 本地服务)
┌──────────────────────── 服务端 (SPT 4.1 mod) ────────────────────┐
│ IOnUpdate (5s 心跳)                                              │
│   └─ MarketClock：由锚点 + 现实时间 ×7 推出 tickIndex            │
│        └─ 补齐缺失 tick → PriceEngine（确定性种子）→ Candle      │
│             └─ MarketStore（<=200 根/股，落盘）                   │
│ StaticRouter /stock-market/*                                     │
│   ├─ state / candles        行情查询                              │
│   ├─ buy / sell             交易（校验资金、T+1、持仓）            │
│   └─ dev/advance            调试快进（配置开关）                   │
│ PortfolioStore（按 sessionId） + SaveServer + MailSendService     │
└──────────────────────────────────────────────────────────────────┘
```

---

## 1. 时间模型（核心）

### 1.1 时钟

- **历元**：`2016-01-01T00:00:00Z`
- **流速**：7×（塔科夫时间 / 现实时间）
- **锚点持久化**：首次运行时记录 `anchorRealUnix = now - 200 tick`（这样启动时就有 200 根历史），写进 `data/market.json`。

```
marketSeconds(realNow) = (realNow - anchorRealUnix) × 7
tickIndex              = floor(marketSeconds / 900)        // 900s = 15 分钟塔科夫时间
candleOpenTime         = 2016-01-01T00:00:00 + tickIndex × 15min
```

- 一个 tick 的现实时长 = `900 / 7 = 128.571s ≈ 2 分 8.57 秒`
- 200 根 K 线 = 3000 塔科夫分钟 = 50 塔科夫小时 ≈ **7.14 现实小时**
- 用**绝对 tickIndex**而不是累加计数器，好处：
  - 服务端卡顿/重启不会漂移；
  - 同一 tick 重复计算是幂等的（可以安全补算）；
  - 调试快进 = 直接把锚点往前挪。

### 1.2 离线补齐（服务端关闭期间）

`IOnUpdate` 每 5 秒被调用一次（`SPTStartupHostedService` 的 `PeriodicTimer(5s)`），
每次拿到 `secondsSinceLastRun`。启动时先算：

```
missed = tickIndex(now) - lastPersistedTick
```

- `missed == 0`：正常等待下一 tick。
- `0 < missed <= 200`：按序补算，落盘，**与在线时生成的完全一致**（见 2.2 确定性种子）。
- `missed > 200`：仍然补算（代价是纯算术，2 万次也只是毫秒级），但**只保留最后 200 根**；
  若 `missed` 超过阈值（默认 20000 ≈ 1 个月现实时间），走"跳跃模式"：
  直接从长期均值重新起算，避免无意义的长循环。

> 结论：**服务端关着的时候行情照走**。这与"2016 起跑 + 7 倍速"的设定一致。

### 1.3 与游戏内时间的关系

行情时钟**独立于每局战局时间**（战局时间是每局从服务器下发的起点重新算的，局与局之间不连续）。
两者都用 7× 流速，所以体感一致；但行情不能读 `GameDateTime`（战局外没有它）。

客户端只显示服务端给的 `marketTime`，不做本地推算。

---

## 2. 价格引擎

### 2.1 模型：对数空间均值回归 + 波动率状态切换 + 跳变

```
regime[t] ∈ {Quiet, Active}        // 两状态马尔可夫链
x[t+1] = x[t] + κ·(ln(mean) − x[t]) + μ + σ·scale(regime[t])·z + jump
price  = exp(x)
```

- `κ` 回归强度（防止价格起飞/归零）
- `σ` 每 tick 基准波动率 **0.6%**；实际波动率乘 `QuietVolatilityScale`（0.35 → 0.21%）或 `ActiveVolatilityScale`（2.0 → 1.2%）
- `RegimeActivateChance`（0.04）/ `RegimeCalmChance`（0.25）：活跃段较短
- `JumpChance` = 0.005（200 根里约 1 次跳变），单次幅度 3σ × 随机系数
- `μ` 漂移（可以为 0 或轻微正漂移）
- `jump`：以 `jumpChance` 概率触发的跳变，幅度 `±JumpScale·σ·N`
- 价格夹在 `[MinPrice, MaxPrice]`

**为什么加状态切换**：恒定 σ 会让每根 K 线长得一模一样，连着一排同尺寸实体像心电图。
两状态波动率能制造"长期横盘磨底（小实体/十字星）→ 放量大实体"的真实交替。

**为什么用均值回归**：单支股票用 GBM 会在 200 根内漂到离谱的价位；OU 更可控，也方便以后做"多股共享市场因子"。

### 2.2 确定性种子（关键设计）

```
seed = Hash128(stockId + ":" + tickIndex + ":market")
rng  = SplitMix64(seed)
```

**不准用 `Random.Shared` / `Guid` / 时间**。原因：
- 离线补算必须与在线生成**逐位一致**；
- 同一 tick 被重复调用（补算 + 在线）时不能产生不同结果；
- 以后要做回测/复现也只依赖 `(stockId, tickIndex)`。

`regime` 是跨 tick 的状态，因此持久化在 `StockRuntime.VolatilityRegime` 里；
离线补算按 tick 顺序重放，结果与在线时一致。

**模型版本**：任何改动随机数消费顺序 / 子步数量 / 分布形状的修改，都必须把
`PriceEngine.ModelVersion` +1。`MarketStore` 读到版本不匹配会丢弃旧 `market.json`
重新生成，避免"前半段旧分布、后半段新分布"的假行情。

### 2.3 单根 K 线（OHLC）

```
open   = prev.Close（首根 = InitialPrice）
把本 tick 再切成 16 个小步：每步按 2.1 演进
close  = 最后一步
high   = max(open, close, 中间各步)
low    = min(open, close, 中间各步)
volume = BaseVolume × regime 系数 × (0.5 + rng.NextDouble())
```

`TickSize`（最小报价单位，如 0.01）在最后统一取整，保证 `High >= max(Open,Close)`、`Low <= min(...)`。

### 2.4 多股票扩展点

```csharp
interface IPriceModel
{
    decimal Next(StockDefinition def, decimal lastClose, long tickIndex);
}
```

- v1 只实现 `MeanRevertingJumpModel`。
- 以后加：指数/板块（多股共享一个 `marketFactor[tickIndex]`，个股加 `beta`）、
  事件驱动跳变、股息除权等，都只是新增 `IPriceModel`。
- 每支股票的价格状态完全独立（`StockRuntime`），互不影响。

---

## 3. 数据模型与持久化

### 3.1 目录

```
<TKF>\SPT_Runtime\user\mods\TarkFX.StockMarket\
    TarkFX.StockMarket.Server.dll
    config\stocks.json          // 股票定义（静态，可热改）
    config\config.json          // feeRate / mailLifetimeSeconds / enableDevRoutes
    locales\ch.json / en.json   // 服务端下发本地化（沿用 OperatorTraits 的做法）
    data\market.json            // ModelVersion + 时钟锚点 + 每股 <=200 根 K 线
    data\portfolios.json        // sessionId -> 持仓/批次
```

客户端：`<TKF>\BepInEx\plugins\TarkFX.StockMarket\TarkFX.StockMarket.Client.dll`

### 3.2 结构

```csharp
// 静态定义
sealed record StockDefinition {
    string Id;            // "TEST-01"
    string NameKey;       // "stockmarket/stock/test01/name"
    string Currency;      // "RUB" | "USD" | "EUR"
    decimal InitialPrice, MeanPrice, MinPrice, MaxPrice, TickSize;
    double Volatility, MeanReversion, Drift, JumpChance, JumpScale;
    long BaseVolume;
}

// 运行时行情
sealed record Candle { long Tick; decimal Open, High, Low, Close; long Volume; }
sealed record StockRuntime {
    decimal LastClose;
    int     VolatilityRegime;   // 0 = 安静 / 1 = 活跃，随 K 线一起持久化
    List<Candle> Candles;       // Count <= 200
}

// 全局
sealed record MarketFile {
    int    ModelVersion;        // 与 PriceEngine.ModelVersion 不一致时整档重建
    long   AnchorRealUnix;
    long   LastTick;
    Dictionary<string, StockRuntime> Stocks;
}

// 持仓（按 profile）
sealed record Portfolio {
    Dictionary<string, List<Lot>> Positions;   // stockId -> 批次
}
sealed record Lot {
    int      Shares;
    decimal  Price;            // 成交价
    long     BuyTick;
    double   BuyMarketSeconds; // T+1 判定基准
}
```

### 3.3 落盘策略

- **每个 tick 追加后立即写** `market.json`（体积很小：200×N×~60B）。
- 写入用 `写 .tmp → File.Move(overwrite)`，避免断电/崩溃产生半截 JSON。
- 两个文件各一把 `lock`；`buy/sell` 的"扣钱 + 加仓"必须在同一把锁内完成。
- 交易成功后再 `SaveServer.SaveProfileAsync(sessionId)`。

### 3.4 并发

服务端 `IOnUpdate` 与 HTTP 请求可能在不同线程：

- `MarketStore`：`lock` 包住"读-改-写"。
- `PortfolioStore`：`lock` + 立即落盘。
- 先动 `MarketStore` 再动 `PortfolioStore`（固定顺序），避免死锁。

---

## 4. 交易规则

### 4.1 T+1

- 买入生成一个 `Lot`，记 `BuyMarketSeconds`。
- 卖出时只有满足
  `marketSeconds(now) - lot.BuyMarketSeconds >= 24 * 3600`
  的批次可卖（FIFO 消耗）。
- **注意**：这里的 24 小时是**塔科夫时间** → 现实约 **3.43 小时**。
  如需改成现实 24 小时，改 `PortfolioStore.TPlusOneMarketSeconds` 并同步换算。

### 4.2 币种

- 每支股票在自己的 `StockDefinition.Currency` 里定价（`RUB` / `USD` / `EUR`）。
- 服务端货币模板 id 直接复用 SPT 常量：

```csharp
Money.ROUBLES = "5449016a4bdc2d6f028b456f"
Money.DOLLARS = "5696686a4bdc2da3298b456a"
Money.EUROS   = "569668774bdc2da2298b4568"
```

- 买入：从 `pmcData.Inventory.Items` 里找该模板的钱（`Inventory.Items` 是**扁平全量列表**，
  容器里的钱也在里面），累加 `Upd.StackObjectsCount`；不足则返回错误码。
- 扣款按堆逐个扣（`InventoryHelper.RemoveItemByCount` 只处理单个 item，需要循环 + 自己拆堆），
  等价的简单写法：遍历所有该模板的 item，按 `stackSize` 从后往前扣，扣完为止。
- 卖出：`MailSendService.SendLocalisedSystemMessageToPlayer(...)`，
  附件是一枚 `Item { Template = 货币模板, Upd.StackObjectsCount = 金额 }`，
  由 `ItemHelper.GenerateUpdForItem` 生成 `Upd`。

### 4.2.1 涨跌幅口径

涨跌幅 = **相对昨收**（上一交易日最后一根 K 线的收盘价），不是相对上一根 K 线。

- 一个交易日 = 24 塔科夫小时 = 96 个 tick；
- 昨收由 `MarketStore.PreviousDayClose()` 在 200 根窗口里回溯取得；
- 新档第一天没有"昨天"，退化为窗口首根的开盘价。

> 如果按"上一根"算，每 128 秒就会刷新一次涨跌，看不出当天累计涨跌，也容易误导。

### 4.3 价格成交口径

- 成交价 = **当前 tick 的 `Close`**（服务端最后一根未完成 K 线也用实时值）。
- 同一 tick 内多次买卖同价（简化，无滑点）。
- 金额取整到 `TickSize`，再乘股数；不足 1 个最小单位向上取整以保证不出现 0 元成交。

### 4.4 市场开放时间

- 行情 7×24 运行（跟随塔科夫时间，无休市）。
- 建议**只允许在非战局中交易**（`ScreenController.InRaid == false`）；
  战局内可以看行情，但不允许下单（避免"蹲坑时单手炒股"和库存刷新问题）。
  → 待确认。

---

## 5. 网络接口（全部 POST，沿用 `RequestHandler.PostJson`）

| 路由 | 请求 | 响应 | 说明 |
|---|---|---|---|
| `/stock-market/state` | `{}` | `{ success, marketTime, tick, stocks:[{id,nameKey,currency,price,prevClose,changePct}], balances:{RUB,USD,EUR}, holdings:[{stockId,shares,sellable,avgCost,value,nextSellableIn}] }` | 轻量快照，5s 一拉 |
| `/stock-market/candles` | `{ stockId, count? }` | `{ success, stockId, candles:[{t,o,h,l,c,v}] }` | 打开 Tab / 切股票 / 换 tick 时拉 |
| `/stock-market/buy` | `{ stockId, shares }` | `{ success, code?, messageKey?, params?, state? }` | 服务端校验资金 |
| `/stock-market/sell` | `{ stockId, shares }` | 同上 | 校验可卖批次，成交后发邮件 |
| `/stock-market/dev/advance` | `{ ticks }` | `{ success, tick }` | 仅当配置 `Debug.EnableDevRoutes=true` |

**错误码约定**：

```
insufficient_funds     资金不足
insufficient_shares    可卖持仓不足（含 T+1 未解锁部分）
unknown_stock          股票不存在
market_closed          当前不允许交易（战局内 / 休市）
invalid_amount         股数非法（<=0 或超过单笔上限）
```

响应里带 `messageKey`，客户端用 `.Localized()` 显示；不在服务端拼中文。

---

## 6. 客户端设计

### 6.1 Tab 注入（照抄 OperatorTraits 的成熟做法）

```csharp
[HarmonyPatch]  // InventoryScreen 的所有 Show 重载
Prefix(InventoryScreen __instance) => StockTab.TryAttach(__instance);
```

`TryAttach`：
1. `screen._tabDictionary[EInventoryTab.Skills]` 取出 Skills 的 `Tab`；
2. `Instantiate` 一份，改名 `StockMarketTab`，插到同一 sibling index；
3. 清掉克隆出来的 `LocalizedText`（否则会继承"技能"文字）；
4. 自建面板 `StockPanel`；
5. `clonedTab.Init(new StockTabController(panel))`。

`StockTabController : ITabController`
- `Show()` → 面板激活 + 启动轮询协程；
- `TryHide()` → 停止轮询 + 面板隐藏。

> 已验证 4.1 的 `InventoryScreen._tabDictionary`、`ITabController { void Show(); Task<bool> TryHide(); }`
> 与 `Tab.Init(...)` 与 OperatorTraits 使用的完全一致。

### 6.2 面板布局

```
┌──────────────────────────────────────────────────────────────┐
│ [股票列表]        │  价格 / 涨跌幅 / 币种                     │
│  TEST-01  ₽123.4  │  ┌───────────────────────────────────┐   │
│  (未来可滚动)      │  │                                   │   │
│                   │  │        KLineGraphic               │   │
│                   │  │        (自绘蜡烛 + 网格)           │   │
│                   │  └───────────────────────────────────┘   │
│                   │  持仓: 100 股 · 可卖 40 · 均价 ₽…        │
│                   │  [买入 √] [数量 10] [卖出 √]            │
│                   │  余额: ₽…  $…  €…                     │
└──────────────────────────────────────────────────────────────┘
```

- 全部用 `RectTransform` + `TMP_Text` + `Button`，不依赖预制体。
- 股票列表做成 `ScrollRect`（v1 只有一支，也按列表写，后续零改动）。

### 6.3 K 线绘制

自定义 `KLineGraphic : MaskableGraphic`，重写 `OnPopulateMesh(VertexHelper vh)`：

- 背景 + 4 条横向网格线；
- 蜡烛：`实体矩形 + 上下影线`，颜色由配置决定（红涨绿跌 / 绿涨红跌）；
- **现价水平虚线** + **昨收参考虚线**，避免"无刻度自动缩放"造成的暴跌错觉；
- **MA5 / MA20** 折线：用完整 200 根序列计算，只画可视段
  （窗口边界处的 MA20 会正确用到 19 根看不见的历史）；
- **只渲染最近 N 根**（默认 120，可配 20~200，支持**鼠标滚轮缩放**）：200 根挤在几百像素里
  影线只有 1px，会糊成"阶梯"，这是视觉问题、换模型也解决不了；
- **Y 轴价格刻度**（右侧 5 个 `TMP_Text`）与**时间刻度**（底部首/中/末 3 个）；
- **蓝色持仓成本虚线**；Y 轴范围 = 可视 K 线 High/Low ∪ 可视段 MA5/MA20 ∪ 成本价
  （否则滞后的 MA20 或窗口外的成本线会被画到图表外）；
- 数据更新时 `SetVerticesDirty()` + 重建轴标签。

### 6.6 客户端配置（BepInEx）

| 配置项 | 默认 | 说明 |
|---|---|---|
| `可视 K 线根数 / Visible Candles` | 120 | 20~200，纯显示窗口；游戏内可用滚轮临时缩放 |
| `红涨绿跌 / Red Up, Green Down` | true | 关闭即美股配色 |

两项都只影响客户端渲染，不改服务端任何数据；`ApplyState()` 每 5 秒重读一次，改完最多 5 秒生效。

> 备选：生成 `Texture2D` 再贴到 `RawImage`，但抗锯齿差、改缩放麻烦，不推荐。

### 6.4 轮询与线程

`RequestHandler.PostJson` 是**同步阻塞**的。5 秒一次的轮询不能直接在主线程 `Update()` 里调（会掉帧）：

```
后台线程 Task.Run(PostJson) → 结果放进并发槽
Update() 里非阻塞取出 → 主线程更新 UI
```

- 行情快照：5 秒一次；
- K 线：仅在 `tick` 变化 / 切换股票 / 首次打开时拉取；
- 下单后：立即拉一次 `state` + `candles`，清输入框，弹提示。

### 6.5 本地化

- 服务端 `locales/ch.json` / `en.json` 下发（OperatorTraits 同款 `ServerLocalisationService` 注入）。
- 客户端所有文案 `"stockmarket/ui/buy".Localized()`。
- 数字格式化：货币符号 + 千分位。

---

## 7. 项目骨架

```
StockMarket/
  StockMarket.sln
  Client/
    StockMarket.Client.csproj       // netstandard2.1
    Core/Plugin.cs                  // BepInEx 入口 + Harmony
    Core/StockApi.cs                // 路由封装
    UI/InventoryScreenShowPatch.cs
    UI/StockTab.cs                  // 克隆 Tab + 面板搭建
    UI/StockPanelController.cs      // 数据绑定 / 轮询
    UI/KLineGraphic.cs              // 自绘蜡烛图
    Model/Dto.cs                    // 与服务器对齐的 DTO
  Server/
    StockMarket.Server.csproj       // net10.0
    Core/ModMetadata.cs             // [Injectable] IModMetadata
    Core/StockMarketUpdateService.cs// IOnUpdate：tick 循环
    Core/MarketClock.cs
    Core/PriceEngine.cs             // IPriceModel
    Core/MarketStore.cs             // 行情持久化
    Core/PortfolioStore.cs          // 持仓持久化
    Core/TradeService.cs            // 买/卖/校验/扣钱/发邮件
    Api/StockMarketRouter.cs        // StaticRouter
    config/stocks.json
    locales/ch.json, en.json
```

**项目引用与部署**：直接照搬 `operatortraits-spt` 的双 csproj + `DeployClient` / `DeployServer` MSBuild Target：
- 客户端 → `I:\TKF\BepInEx\plugins\StockMarket\`
- 服务端 → `I:\TKF\SPT_Runtime\user\mods\StockMarket\`
- SPT 版本约束 `~4.1.0`，依赖 `SPTarkov.Server.Core / DI / Reflection / Common` 4.1.1。

---

## 8. 分阶段实施

| 阶段 | 内容 | 产出 |
|---|---|---|
| **M0** 骨架 | 两个 csproj、DI 注册、`/stock-market/state` 返回假数据、客户端 Tab 能点开 | 打通链路 |
| **M1** 引擎 | 时钟 + 确定性价格模型 + tick 落盘 + 离线补齐 + `/candles` | 行情会动，重启不丢 |
| **M2** 界面 | KLineGraphic、报价/持仓面板、5s 轮询、切股票 | 能看盘 |
| **M3** 交易 | buy/sell、资金校验、T+1、邮件回款、库存刷新 | 能交易 |
| **M4** 打磨 | 本地化、错误提示、配置化（波动率/手续费）、打包脚本、`dev/advance` | 可发布 |

每个阶段都能单独编译运行，不会出现"全写完才发现方向错了"。

---

## 9. 风险点

1. **客户端库存刷新**：服务端直接改 `profile.Inventory` 并保存后，**已经打开的背包界面不会自动重画**。
   - 方案 A：下单成功后客户端主动重建当前 Tab（反射调 `InventoryScreen.Show`）——待实测；
   - 方案 B：只保证下次打开背包时数值正确，配合顶部提示。
   - 这是 M3 的主要不确定项，需要实机验证。

2. **金额上限**：卢布单堆有上限；大额交易要能跨多堆扣款/回款（回款邮件拆成多枚 `Item`）。

3. **战局内访问**：背包界面战局内可开。默认建议禁止下单（见 4.4）。

4. **服务端长时间离线**：补算量会变大，必须有"跳跃模式"兜底，避免启动卡死。

5. **JSON 序列化命名**：SPT 服务端的 `JsonUtil` 与客户端 Newtonsoft 的大小写约定不同，
   沿用 OperatorTraits 的写法（`response["x"] ?? response["X"]`）即可，但要在 DTO 上统一。

---

## 10. 已确认口径

1. **T+1** = 24 小时**塔科夫时间**（现实约 3.43 小时）。
2. **服务端关闭期间行情继续走**，启动时按 tick 顺序补齐。
3. **战局内可看不可交易**（客户端禁用按钮；服务端没有可靠的 in-raid 标志，暂不强制）。
4. **手续费**：计算点已预留（`total × feeRate`），当前 `feeRate = 0`。
5. **价格用小数**，成交金额 `round(单价 × 股数)`：
   19.19 × 1 → 19，19.19 × 10 → 192（和原版商店一致）。
