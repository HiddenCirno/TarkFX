# TarkFX

SPT 4.1 的客户端/服务端插件集合。

## StockMarket — 塔科夫证券交易所

在背包界面顶部新增一个「交易所」Tab，提供一支（未来多支）股票的行情、K 线
与买卖。服务端是唯一权威：价格推进、K 线生成、持仓与资金全部在服务端完成。

完整设计见 [`StockMarket/ARCHITECTURE.md`](StockMarket/ARCHITECTURE.md)。

### 结构

```
StockMarket/
  Client/    netstandard2.1  BepInEx 插件（UI + 轮询）
  Server/    net10.0         SPT 4.1 服务端 mod（路由 + 行情引擎）
```

### 构建

```bash
dotnet build -c Release
```

默认把客户端部署到 `I:\TKF\BepInEx\plugins\TarkFX.StockMarket`，
服务端部署到 `I:\TKF\SPT_Runtime\user\mods\TarkFX.StockMarket`。
换安装目录：`dotnet build -c Release -p:SptRoot=D:\path\to\SPT`。

只编译不部署：`-p:SkipDeploy=true`。

### 运行

服务端首次启动会：

1. 记录行情时钟锚点（2016-01-01 起，7× 塔科夫时间）；
2. 回填 200 根历史 K 线，之后每 15 分钟塔科夫时间（≈128.6 现实秒）推一根；
3. 每根 K 线追加后写 `data/market.json`，持仓写 `data/portfolios.json`。

### 当前口径

- 价格：小数，最小报价 `tickSize`（默认 0.01）
- 波动率：15 分钟基准 σ 0.6%，安静/活跃两状态切换，跳变约每 200 根一次
- 成交金额：`round(单价 × 股数)`，与原版商店一致（19.19×1→19，19.19×10→192）
- 涨跌幅：相对**昨收**（96 个 tick 为一个塔科夫交易日）
- T+1：24 小时**塔科夫时间**（现实约 3.43 小时）
- 手续费：0（`config/config.json` 的 `feeRate`，计算点已预留）
- 卖出回款：系统邮件
- 战局内：可看行情，不能买卖

### 图表

- Y 轴价格刻度 + 底部时间刻度
- 现价虚线 + 昨收参考虚线 + 蓝色持仓成本线
- MA5 / MA20
- 默认显示最近 120 根（配置项 `1. 图表 → 可视 K 线根数`），图表上可用鼠标滚轮缩放
- 红涨绿跌 / 绿涨红跌可切换（配置项 `1. 图表 → 红涨绿跌`）
