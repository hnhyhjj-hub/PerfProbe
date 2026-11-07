# PerfProbe 数据分析指南（写给 AI）

本文件说明如何读 PerfProbe 的输出、能得出什么结论、不能得出什么结论，以及常见误判。
所有结论都基于 RimWorld `1.7.4995 rev309` 的实测数据（2026-10-30/11-02，96 pawn / 338 mods 环境）。

> **版本前提**：本指南描述的字段与判读规则对应 PerfProbe **1.4.0 及以后**。1.4.0 起模组同时提供
> 1.6（`net472`）与 1.7（`net10.0`）两个编译版本，但**只有 1.7 侧经过实测**。若数据来自 1.6，
> 字段结构完全相同、判读规则同样适用，但帧阶段那一组数值可能因渲染管线不同而出现明显异常
> （1.6 是 Unity 渲染，1.7 是 Waylith 引擎）。遇到不合理的帧数据，先确认客户端版本。

---

## 先读这一节：按你的 AI 形态选文件

同一个会话会产出三个文件，**不是每个 AI 都该读全部**：

| AI 形态 | 该读什么 | 能做什么 | 不能做什么 |
|---|---|---|---|
| **Agent**（DSH / Claude Code / Cursor 等能执行命令的） | `index.json` → `summary.md` → 必要时 `jsonl` | 定位瓶颈 + 反编译查根因（第 7 节） | —— |
| **网页 AI**（能上传文件，ChatGPT / Claude / Gemini 网页版） | **`brief.md`**（首选）或 `summary.md` 前段 | 定位瓶颈、给优化优先级 | 反编译查根因（没有本机文件系统） |
| **纯对话 AI**（只能粘贴文本） | **`brief.md`** 全文 | 定位瓶颈 | 同上，且拿不到完整数据 |

### 三个文件的分工

```
<mode>-<日期>-<时间>-sNN.brief.md    ← 约 10 KB / 2500 token。给网页 AI 用，可直接整份上传
<mode>-<日期>-<时间>-sNN.summary.md  ← 约 140 KB / 4 万 token。附录占 88%，只有 Agent 需要
<mode>-<日期>-<时间>-sNN.jsonl       ← 单会话逐窗口原始记录，3 MB 级。只有 Agent 逐窗口趋势分析时才读
```

**`brief.md` 是自足的**：含会话头（含帧率与帧阶段 Top5）、按类合计、单条最高、峰值窗口 Top5、
分类 Top5、模组 Top10、人物外壳三项 + 个体 Top5、帧阶段分解、5 条口径说明。
**做「先优化什么」这个决策，`brief.md` 已经够了。** 要「为什么贵、怎么改代码」才需要 Agent + 第 7 节。

> **给网页 AI 的标准提问模板**（把 `brief.md` 全文粘进去）：
> 「这是一份 RimWorld 性能采集报告。请找出最值得优化的三项，
> 说明判断依据（引用具体数字），并按『改动代价 / 收益』给出执行顺序。
> 注意 `selfMs` 可加总、`cat=pawn` 外壳三项不能与核心组重复相加。」

---

## 0. 先看哪里

```
<输出目录>/
├── index.json                       ← AI 从这里入手，所有会话的汇总
├── schema.md                        ← 字段口径，本文件之外的机器可读版
├── AI-ANALYSIS-GUIDE.md             ← 本文件（模组自动复制过来）
├── <mode>-<日期>-<时间>-sNN.jsonl       ← 单会话逐窗口明细，一行一个 JSON
├── <mode>-<日期>-<时间>-sNN.summary.md  ← 人类可读汇总 + 附录全量
└── <mode>-<日期>-<时间>-sNN.brief.md    ← 精简版，给网页/纯对话 AI
```

输出目录里有 `schema.md`、`AI-ANALYSIS-GUIDE.md`（也就是本文件）、`index.json` 与各会话文件。
**这些文档由模组在每次开始采集时自动复制到数据目录**，所以你在数据目录里就能读到全部判读规则，
不需要再去 mod 安装路径找。

默认输出目录按平台不同：

| 平台 | 默认路径 |
|---|---|
| Windows | `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\PerfProbe\` |
| macOS | `~/Library/Application Support/Ludeon Studios/RimWorld/PerfProbe/` |
| Linux | `~/.local/share/Ludeon Studios/RimWorld by Ludeon Studios/PerfProbe/` |

注意 **`GenFilePaths.SaveDataFolderPath` 不含 `Saves` 层**，别去 `Saves\PerfProbe\` 找。
启动参数 `-savedatafolder <路径>` 会整体改变这个根目录；设置面板里的「输出目录」为空时才用上面的默认值。

**读取顺序**：`index.json` → 选目标会话 → `summary.md` 看结论与排行榜 → 需要逐窗口趋势时再读 `jsonl`。

---

## 1. 核心指标与判读

| 字段 | 含义 | 怎么用 |
|---|---|---|
| `msPerTick` | 本窗口每 tick 平均耗时（毫秒） | **主指标**。低于 5 通常流畅，5–10 有压力，>15 明显卡 |
| `tps` | 实测 tick/秒 | 6x 档理论上限约 360 TPS，实测远低说明 tick 太慢 |
| `msPerTickP95` | tick 耗时 P95 | 与平均值差距大 = 有尖峰，去看「出现窗口少但 selfMs 高」的行 |
| `frameMs` | 同期平均帧耗时 | 与 `msPerTick` 对比可区分「逻辑卡」和「渲染卡」 |
| `fps` | 本窗口帧率 | **30 是硬临界**。≥30 时帧率对 TPS 无影响；<30 时 TPS 直接随帧率掉，且不可补偿。见第 1.1 节 |
| `frameUnaccMs` | 未测部分累计（毫秒） | 帧长之和 − 各阶段 selfMs。**引擎渲染、Present、显存等待都在这里**，是判断「GPU 卡还是 CPU 卡」的关键 |
| `fstages[]` | 帧阶段数组 | 十段 self 耗时，分母是帧数。与 `rows[]` 是两套口径，不可相加 |
| `gc0/gc1/gc2` | 本窗口 GC 次数 | `gc0` 频繁 = 分配压力大；`gc2` 出现 = 有 full GC，通常是重灾区 |
| `gapMs` | 未采集档位造成的覆盖缺口 | 缺口大说明数据代表性差，结论要打折 |
| `sampleK` | 抽样分母 | 见第 4 节 |
| `dropLines` | 写盘队列溢出丢的行数 | 应恒为 0；非 0 说明写盘跟不上，加大 `WindowSec` |

### 1.1 帧率与 TPS 的关系（先判这一条，能省掉一半无用功）

引擎每帧最多跑 `TickRateMultiplier × 2` 个 tick，累加器余量**每帧清零不跨帧补偿**。
两式相除，**临界帧率恒为 30 FPS，与速度档无关**：

| 帧率 | 每帧 tick | 结果 |
|---|---|---|
| 60 | 1 | 满速，余量充足 |
| **30** | **2** | **刚好顶到上限，分界点** |
| 20 | 2（撞上限） | TPS 掉 1/3 |
| 15 | 2 | TPS 掉一半 |

判读顺序：

```
fps ≥ 30 且 TPS 低于 60×TRM
   → 不是渲染问题，纯 CPU 侧 tick 慢，去优化 msPerTick
fps < 30
   → 渲染卡口，TPS 正在被帧率拖累
   → 再看 frameUnaccMs：
        未测部分 > 渲染侧 + 逻辑侧  → 瓶颈在 GPU（显存换页 / 绘制批次 / 贴图尺寸）
        渲染侧 > 逻辑侧            → 「画多少」的问题
        逻辑侧 > 渲染侧            → 继续用 rows[] 的 tick 归因下钻
```

**注意 `fps` 的分母是帧长之和，不是 `winActual`**：暂停时帧照跑但不产生 tick，
用墙钟做分母会让 FPS 虚高。所以暂停状态下也会看到合理的帧率数值。

**基准参考值**（96 pawn / 338 mods / 6x / sampleK=8，取自 `passive-20261029-2351-s01` 的 47 个窗口）：

```
msPerTick   平均 9.5   范围 4.5 – 163.4
窗口级 P95  平均 11.6  范围 6.6 – 163.4
TPS         平均 124.7 范围 0.2 – 167.2
```

注意这是**测量样本，不是及格线**。同一个存档在不同地图规模、不同 mod 组合下差异很大，
判断「卡不卡」请以自己机器上的历史会话纵向对比为准。

**会话质量的判据**：看 `gapMs` 与 `dropLines`。

- `gapMs` 小（几百 ms）+ `dropLines=0` → 数据质量高，可作主参考
- `gapMs` 大（数千 ms）→ 大部分时间不在采集档位（读档/暂停），代表性差
- 实测两轮对比：`s02` 缺口 556 ms（TPS 97.2）质量高；`s01(22:11)` 缺口 2812 ms（TPS 15.9）**不能用来下结论**

---

## 2. 七类 `cat` 的关系（最容易搞错的地方）

`rows[].cat` 有 7 个取值，**它们不是平级可相加的分类**：

```
building  建筑      ┐
item      物品      │
other     其它      ├─ 按 Thing 切，互不重叠，selfMs 可直接相加
pawn      人物方面  │
component 组件      ┘

pawninst  人物个体  ── 与 pawn 覆盖同一批 tick，两条线二选一
mod       模组汇总  ── 由上面各行二次汇总，不能再与它们相加
incident  事件      ── 预留（M2）
```

### 铁律

1. **`pawn` 与 `pawninst` 不能相加**。`pawn` 按「功能方面」切（思考/工作/寻路/状态/装备/其它），`pawninst` 按「个体」切。同一批 tick 的两种视角，各自加总都约等于全部 pawn 总耗时。
2. **`mod` 不能与任何行相加**。它是按 `mod` 字段的二次汇总，与原始行共享同一分母。用来看「哪个模组最贵」，不能用来算总账。
3. **`building` / `item` / `other` / `pawn` / `component` 这五类可以相加**，得到「全部已归因耗时」。
4. **每类只输出 Top-N**，见第 4 节。所以任一类内部的 `selfMs` 之和 **不等于** 该类真实总量。

### 2.1 `cat=pawn` 内部还有一层陷阱

`pawn` 的十四行**不是平级的**，分两层：

```
外壳层（可相加，三项之和 = 全部 pawn 的 Thing.DoTick 总时间）
├── pawn_body        Pawn.Tick            inclusive，每 tick 必跑
├── pawn_interval    Pawn.TickInterval    inclusive，按 UpdateRateTicks 跑
└── pawn_other       未被任何锚点覆盖的部分

叶子层（pawn_body 的内部细分，已在上面那个数里）
├── pawn_work / pawn_path / pawn_state / pawn_gear / pawn_think
└── pawn_gene / pawn_ability / pawn_mutant / pawn_rare / pawn_render / pawn_misc
```

**叶子层不能再加一次。** 判断方法：`SummaryWriter.PawnCost.IsShell()` 返回 `true` 的才是外壳层。

算人物层占比时用 `pawn_body + pawn_interval + pawn_other`。若把十四个行全加起来，会得到远超真实值的数——早期版本就是这样，「合计已归因」曾达到 **119.68%**，同一个 summary 里「人物合计」两处数字互相矛盾。v1.3.1 起已修正，但**旧数据仍有这个问题**，读到 1.3.1 之前生成的 summary.md 时，请按本节自行重算。

---

## 3. 各表的用途与限制

### 3.1 组件（`cat=component`）—— 最值得看的表

实际数据显示组件常常是最大头（实测 `passive-20261029-2216-s02` 占 11.4% 全 tick 时间，五类之首）。

`name` 是 **C# 类名**（MapComponent 子类没有对应 def）。**看到类名不要停在这一步**，要反编译到具体方法。

读法：

- `selfMs` 高 + `n=1` → 单实例的系统级组件，通常是「每 tick 全量遍历」的实现
- `perInstMs` 高 → 该组件的 tick 本身很重
- 常见反模式：`EnsureTopology` / `RebuildGrid` / `ProcessBucket` 这类「每 N tick 全量重建」

### 3.2 建筑 / 物品（`cat=building` / `cat=item`）

`name` 是 `defName`。读法：

- `perInstMs` 高 + `n` 小 → **单台成本高**，拆掉 1–2 个就见效（性价比最高）
- `perInstMs` 低 + `n` 大 → **靠数量堆积**，要减数量或降 tick 频率
- `n=0` 但 `selfMs>0` → 该物品不作为独立 Thing 存在，tick 发生在**持有它的 pawn** 身上（装备/背包）。此时 `perInstMs` 恒为 0，**只看 `selfMs` 总量**

### 3.3 人物方面（`cat=pawn`）

**先分清两种量纲，否则一定会读错：**

**外壳层**（记录 inclusive 时间，三项相加 = 该 pawn 的 `DoTick` 总时间）：

| 方面 | 来源 | 说明 |
|---|---|---|
| `pawn_body` | `Pawn.Tick` | **每 tick 必跑**，不受任何降频 mod 影响 |
| `pawn_interval` | `Pawn.TickInterval` | 频率由 `UpdateRateTicks` 决定：视野内 1–4 tick 一次，视野外 15 tick 一次 |
| `pawn_other` | —— | `Thing.DoTick` 自身外壳：缓存检查、`UpdateRateTicks` 属性读、`directlyHeldThings.DoTick` |

**叶子层**（`pawn_body` **内部**的细分，不能与外壳层相加）：

| 方面 | 覆盖的 patch |
|---|---|
| `pawn_think` | `Pawn_JobTracker.JobTrackerTick` |
| `pawn_work` | `JobDriver.DriverTick` |
| `pawn_path` | `Pawn_PathFollower.PatherTick` |
| `pawn_state` | `HealthTick` + `NeedsTrackerTickInterval` + `MindStateTickInterval` + `StanceTrackerTick` + `VerbsTick` |
| `pawn_gear` | `EquipmentTrackerTick` + `ApparelTrackerTickInterval` |
| `pawn_gene` / `pawn_ability` / `pawn_mutant` / `pawn_rare` / `pawn_render` / `pawn_misc` | `GeneTrackerTick` / `AbilitiesTick` / `MutantTrackerTick` / `Pawn.TickRare` / `PawnRenderer.EffectersTick` / `Inventory`+`Rope`+`Flight`+`NativeVerbs` |

**读法**：问「pawn 为什么是大头」→ 看 `pawn_body` 与 `pawn_interval` 谁大。
问「body 里花在哪」→ 看叶子层。**两者不能相加。**

叶子层之和小于 `pawn_body` 的差值，就是 `Pawn.Tick` 里尚未打点的调用（音效维持段、`Suspended` 判断等）。

### 3.4 人物个体（`cat=pawninst`）

两档命名：

| 单位类型 | 判定 | `name` 形态 | `n` 的含义 |
|---|---|---|---|
| 人类、异星种族 | `!RaceProps.Animal && !RaceProps.IsMechanoid` | `名字[kindDef]` | 恒为 1 |
| 动物 | `RaceProps.Animal` | `kindDef` | 同类总数 |
| 机械体 | `RaceProps.IsMechanoid` | `kindDef` | 同类总数 |

读法：

- 人类行 `perInstMs` = 该个体总耗时，可直接横向比「谁最贵」
- 合并行 `perInstMs` = 同类均值，看不出个体差异
- **「出现窗口数」比 `selfMs` 更有信息量**：出现在 47/47 窗口 = 持续开销；只在 1–3 个窗口 = 瞬时尖峰，可能是初始化或一次性大计算

### 3.5 模组（`cat=mod`）

「哪个模组最贵」的入口。注意它的分母与原始行相同，占比加起来不会到 100%（因为每类只取 Top-N）。

**第一行通常是 `(引擎自身)`，这是正常的，不是某个 mod 的问题。** 它汇总两类开销：

| 来源 | 说明 |
|---|---|
| `cat=pawn` 外壳层三项 | `pawn_body` / `pawn_interval` / `pawn_other` —— 引擎对每个 pawn 的基础处理 |
| `cat=global` 全部系统行 | `ticklist` / `map_post_tick` / `storyteller` / `world_tick` 等引擎调度代码 |

这两类都不属于任何模组的 def，换掉任何 mod 都去不掉。**先把「引擎自身」排除在外，看它下面的行才有意义。**

此表只累加 `cat=pawn` 的外壳层三项，`pawn_work` / `pawn_path` / `pawn_state` 等叶子行**不参与**（它们是 `pawn_body` 的内部细分，再加一次就重复了）。1.3.1 及更早版本没有排除叶子层，那一行会虚高到 90% 以上甚至超过 100%。

要定位某模组的具体开销，回到 `building` / `component` / `pawninst` 表按 `mod` 字段过滤。

---

## 4. 抽样与截断（结论精度取决于此）

两个独立机制，都会造成"数据比实际少"。

### 4.1 抽样分母 `sampleK` —— 默认已改为 1（全量）

`SampleGate.ShouldSample(thingIDNumber)` 用 `(thingIDNumber + phase) % K == 0` 决定某个对象本 tick 是否计时。

**关键事实：不同 `cat` 的抽样规则不一样。**

| cat | 是否抽样 |
|---|---|
| `component` | **不抽样**，`MapComponentTick` 每次被调都计时 |
| `building` / `item` / `other` | 走 `thingIDNumber % K` |
| `pawn` / `pawninst` | 走 `thingIDNumber % K` |

所以 **K > 1 时，各类的 `selfMs` 倍率不同，横向相加或比较占比会得到错误结论**。
这是 0.9.x 把默认值改成 1 的原因。

实测反例（`passive-20261029-2216-s02`，K=8）：

```
直接相加                修正后（抽样类 ×8）
组件  9,030.6  11.41%    组件  9,030.6  11.41%
人物  3,888.0   4.91%    人物 31,103.6  39.28%   ← 排名从第2变第1
建筑  2,321.5   2.93%    建筑 18,571.9  23.46%   ← 排名从第3变第2
```

`summary.md` 的结论段会把「原始累计 / 倍率 / 修正后 / 占全 tick」四列并排列出，
并给出合计覆盖率与未归因残差，不需要自己乘。

残差下面还有两行，读的时候不要混：

| 行 | 含义 |
|---|---|
| *其中：探针自身开销* | 探针自己花的（时间戳 + Harmony 跳转），不是游戏的 |
| *未归因残差* | 总耗时 − 已归因，含探针开销 |
| *扣掉探针后的真实未归因* | **这一行才是「游戏里还有多少找不到来源」** |

真实未归因现在主要剩两类：`TickManager.DoSingleTick` 里的少数未挂点逻辑，
以及 `Pawn.TickInterval` 里 29 个尚未拆分的子调用（它们落在 `pawn_interval` 里，已经不算未归因了）。

### 4.2 每类截断

| 位置 | 上限 | 后果 |
|---|---|---|
| `jsonl` 每窗口每类 | **32 行**（`TopPerCatJsonl`） | jsonl 里某类 `selfMs` 之和远小于该类真实总量 |
| `summary.md` 附录每类 | **4096 行**（`TopPerCatAppendix` = `PerCatCap`） | 不再是瓶颈 |
| `CostTable` 每类桶数 | **4096**（`PerCatCap`） | 超出合并成 `name = others` 一行 |

三个上限的关系：`SessionSnapshot(SessionRowCap)` 的 `SessionRowCap` 也等于 `PerCatCap`，
保证「建了多少桶就输出多少行」，「按类合计」不会因输出截断而少算。

**旧的 200/512 组合曾导致建筑段被截断**：实测 `s02.summary.md` 的建筑与物品附录都顶到 200 行，
即合计数字本身已经漏掉了第 201 名之后的部分。

**jsonl 的 32 行上限仍然存在**，所以：

- jsonl 只适合看「谁最贵」和逐窗口趋势
- 要算占比与总量必须用 `summary.md`

**另一个会踩到的截断**：会话被强杀（直接关游戏、崩溃）时，`jsonl` 最后一行可能只写了一半。
判据是最后一行不以 `}]}` 结尾。此时**用倒数第二行代替**，不要拿残行去解析。

### 因此

| 想知道 | 用什么 |
|---|---|
| 谁最贵（排名） | `jsonl` 或 `summary.md` 附录都行 |
| 各类占全 tick 的百分比 | `summary.md` 结论段的「修正后」表（K>1 时必须看这张，不能自己加） |
| 未归因的部分有多大 | 同一张表的「未归因残差」行 |
| 最精确的分层占比 | `sampleK=1` 跑一轮，此时修正表里倍率全是 ×1 |

---

## 5. 未采集的档位与覆盖缺口

只采集玩家勾选的速度档（默认 `fast` 3x + `superfast` 6x，`ultrafast` 15x 默认关）。

- 暂停、正常 1x、以及未勾选的档位 → 只累加 `gapMs`，不进任何成本行
- `ultrafast` 默认关的原因：一帧跑多个 tick，把每帧固定开销（渲染/UI/GC 边界）摊薄到更多 tick 上，`msPerTick` 会被系统性低估
- **混档平均不可比**：每一行的 `speed` 字段记录该窗口的速度档，同一行不会混算两档。跨会话比较前先确认 `speeds` 一致

---

## 6. 排查流程（从数字到结论）

```
1. index.json 找 msPerTick 最高的会话
2. 该会话 summary.md 的「结论」段读三件事：
   - 按类合计 → 哪个大类是最大头
   - 单条耗时最高 → 单个对象的冠军
   - 耗时最高的模组 → 模组层面的冠军
3. 先判帧率（第 1.1 节）：fps < 30 就是渲染卡口，优化 msPerTick 收益有限
4. 按大类深入：
   组件  → 附录「组件」段，看 selfMs 与 perInstMs
   建筑  → 区分 perInstMs 高（单台贵）vs n 大（数量堆）
   人物  → 「人物十四方面分解」先看外壳层：`pawn_body` 大 = 每 tick 必跑的逻辑贵；`pawn_interval` 大 = 按间隔跑的逻辑贵（可调频率）
   个体  → 「人物个体 / 种族排行」看谁最贵 + 出现窗口数
5. 定位到类名/defName 后，转第 7 节
```

---

## 7. 从数据到代码：查模组方法前先要路径

> **本节仅 Agent 可用。** 网页 AI / 纯对话 AI 没有本机文件系统，跑不了 `ilspycmd`，
> 也拿不到 workshop 目录。**跳过本节不影响定位瓶颈** —— 前面几节能告诉你「哪个类最贵、
> 占多少、是持续型还是爆发型」，这已经够决定「先动谁」。本节只解决「它内部为什么贵」。
> 如果你的 AI 不能执行命令，读完第 6 节就可以停了，把结论交给会写代码的人。

数据只能告诉你**哪个类的 tick 最贵**（例如 `MapComponent_RavenConveyorSystem` 2446 ms），
但**为什么贵**必须反编译看 `MapComponentTick()` 里干了什么。

### 不要做的事

**不要拿类名去全目录检索**。RimWorld 玩家环境常见 300+ 模组、
`workshop\content\294100\` 下数万个文件，一次全目录 `rg` 或 `Get-ChildItem -Recurse`
可能跑 2 分钟以上还超时（本机实测多次超时）。

### 正确流程

**第一步：AI 给出认为的模组包名，等玩家确认路径后再去查**

数据里能直接拿到两个线索：

- `rows[].mod` —— **模组显示名**（如 `Raven Race - Embers in Shadow`）。注意 `AttribIndex.SafeModName`
  优先取 `mcp.Name`，所以这一列通常是显示名而不是 packageId
- `rows[].asm` —— **程序集名**（如 `ZuoYao_RavenRace`）。这个通常就是 dll 文件名去掉扩展名

AI 应当输出一份明确的确认请求，把能推的信息都摆出来：

```
热点的类：MapComponent_RavenConveyorSystem（2446 ms）

疑似所属模组
  显示名    ：Raven Race - Embers in Shadow
  程序集名  ：ZuoYao_RavenRace
  推测包名  ：RavenRace.EmbersInShadow（仅为猜测，需你确认）

请给我该模组的文件夹路径。它一般是
  <RimWorld>\Mods\<任意名>\         ← 本地安装
  或
  <Steam>\steamapps\workshop\content\294100\<数字ID>\   ← 创意工坊订阅
```

**推测包名的依据**（只用于提高确认效率，不是权威结论）：

1. 程序集名 `ZuoYao_RavenRace` → 包名常是 `作者.模组名`
2. 如果玩家能打开模组的 `About\About.xml`，`<packageId>` 就是权威答案
3. 创意工坊订阅的模组，**文件夹名是纯数字 ID**，看工坊页面 URL 的 `id=` 即可：
   `steamcommunity.com/sharedfiles/filedetails/?id=3781005562` → 文件夹 `294100\3781005562\`

**不要自己猜路径就去扫**。猜错的表现是「跑了两分钟什么都没找到」，
比直接问玩家慢得多。**问一句的成本约等于 0，扫一遍的成本是几分钟。**

### 第二步：玩家给出路径后，只在那个目录里查

```powershell
# 已知目录时，定位 dll 很快
Get-ChildItem "<玩家给的路径>" -Recurse -File -Filter '*.dll' | Select-Object FullName, Length

# 列类型
& "C:\Users\<用户>\.dotnet\tools\ilspycmd.exe" -l c <dll路径> | Select-String -Pattern '热点的类名'
```

### 第三步：反编译那个类

```powershell
& ilspycmd -t <完整命名空间.类名> <dll路径> | Out-File out.cs -Encoding utf8
```

两个必须注意的点：

- `-Encoding utf8` 必须写。PowerShell 的 `>` 默认写 UTF-16，会让后续 grep 全失效
- **类名不一定在顶层命名空间**。`-t A.B.C` 报 `Could not find type definition` 时，
  先用 `-l c <dll>` 列出全部类型再挑完整名；嵌套类用 `+` 连接，
  例如 `VersatranZeroOne.VersatranNavigator+ClearanceGridComponent`

### 如果拿不到路径，才允许的降级手段

```powershell
# 只在 About.xml 里找模组名（文件小，快得多）
& "$env:DSH_HOME\.desktop-bin\rg.exe" --max-filesize 64K -l -g 'About.xml' '<模组显示名>' "<workshop目录>"
```

仍慢的话，让玩家在 Steam 创意工坊页面直接看 URL 里的数字 ID，
那就是文件夹名：`workshop\content\294100\<数字ID>\`。

---

## 8. 常见误判清单

| 现象 | 错误解读 | 正确解读 |
|---|---|---|
| 某类 `selfMs` 之和远小于 100% | 「数据丢了」 | 每类只取 Top-N，见第 4.2 节 |
| `pawn_other` 占比最高 | 「有个叫 other 的功能很贵」 | 这是未 patch 的残差，说明探针覆盖不全 |
| 物品 `n=0` 却有耗时 | 「计数 bug」 | 物品 tick 在持有它的 pawn 身上，不作为独立 Thing |
| 动物 `n=0` 却有耗时 | 「计数 bug」 | 动物可能被收纳进建筑（如 TAPI_Barn 的 off-map 存储），不在 `listerThings.AllThings` 里 |
| `mod` 行加起来 > 100% | 「统计错了」 | 它与其他行共享分母，是二次汇总，本身就不该单独看总和 |
| 两个会话 `msPerTick` 差异大 | 「性能变差/变好」 | 先确认 `speed` 与 `sampleK` 一致，混档不可比 |
| 某对象只在 1 个窗口出现但 `selfMs` 很高 | 「忽略它」 | 这是瞬时尖峰，往往是初始化或一次性大计算，值得单独查 |
| 改了代码但数字没变 | 「优化无效」 | 确认新 dll 真的被加载：看 `Logs\Player.log` 里的 `[PerfProbe]` 行 |
| `jsonl` 最后一行解析失败 | 「文件损坏」 | 进程被强杀时异步写盘的最后一行会残。用倒数第二行，前 N-1 行都有效 |
| 只有 `jsonl` 没有 `summary.md` | 「采集失败」 | 没点「停止并输出汇总」。见第 9.1 节 |
| 某会话 `msPerTick` 很低但 `tps` 也极低 | 「性能很好」 | `gapMs` 大说明大部分时间没在采集档位，该会话不可用 |

---

## 9. 让数据更有用的两个操作

### 9.1 停止采集要手动点

`EndSession` 只在两种情况触发：

- 玩家点「停止并输出汇总」按钮
- 检测到读档/新开局

**直接关游戏不会生成 `summary.md` 和更新 `index.json`**，只会留下 `jsonl`。
所以让玩家「跑完点停止」这一步不能省。

### 9.2 想继续细分 pawn

pawn 已经拆到十四个方面，还剩两处可以再细化：

| 目标 | 现状 | 说明 |
|---|---|---|
| `Pawn.Tick` 音效维持段 | 落在 `pawn_body` 与叶子层之差里 | `RaceProps.soundAmbience` / `soundMoving` 的 sustainer 维护，多数 pawn 为 null |
| `Pawn.TickInterval` 里 29 个子调用 | 全部落在 `pawn_interval` | 想拆需在 `Patch_Pawn_Shell.cs` 之外再补锚点，按语义分四组（job / skill / env / phys） |

补 patch 时注意三条不变量，破坏任何一条都会让数字自相矛盾：

1. 同一 tick 内各叶子桶**两两不重叠**（子树里有别的 patch 就记 self，叶子帧才记 total）
2. **外壳层三项（body + interval + other）之和恒等于该 pawn 的 `DoTick` 总时间**
3. **叶子层不能与外壳层相加** —— 叶子层的时间已经含在 `pawn_body` 里了

外壳层只挂基类、不扫子类：若有 Pawn 子类 override 了 `Tick` 且内部调 `base.Tick()`，
两层 patch 会把同一段时间记两遍。启动日志里会打印「子类 override 数」，
那些子类的 pawn 不产生 body/interval 桶，`PawnTickContext` 会自动退回旧口径，不会算错。
---|---|---|
| `Pawn.TickRare()` | 视情况 | 每 250 tick，通常不是大头 |
| `PawnRenderer.EffectersTick` | `pawn_state` | 视觉特效逻辑，可能不便宜 |
| `Pawn_NeedsTracker.NeedsTrackerTick` | `pawn_state` | 比 Interval 版更频繁 |
| `GeneTracker.GeneTrackerTick` | `pawn_state` | 异星种族的主要嫌疑点 |
| `AbilityTracker.AbilitiesTick` | `pawn_state` | 同上 |
| `Pawn_InventoryTracker.InventoryTrackerTick` | `pawn_gear` | 与装备同类 |
| `Pawn_MutantTracker.MutantTrackerTick` | `pawn_state` | 异星种族 |
| `BloodRainUtility.BloodRainTick` | `pawn_other` | 全局，与特定 pawn 无关 |

**异星种族个体特别贵时（实测最贵两只各 475–505 ms，均来自种族 mod），优先怀疑
genes / abilities / mutant 三处** —— 它们都在 `Pawn.Tick()` 里且当前未被 patch。

补 patch 时注意两条不变量，破坏任何一条都会让数字自相矛盾：

1. 同一 tick 内各 aspect 桶**两两不重叠**（子树里有别的 patch 就记 self，叶子帧才记 total）
2. **外壳层三项（body + interval + other）之和恒等于该 pawn 的 `DoTick` 总时间**；叶子层不能与外壳层相加

---

## 10. 一句话速查

```
msPerTick 是主指标；cat 七类不是平级；pawn 与 pawninst 二选一；
mod 不能相加；每类只有 Top-N 所以别算百分比；pawn_other 是残差不是功能；
物品 n=0 是正常的；查代码前先要模组路径；跑完要点停止才出 summary；
jsonl 末行可能是残的；gapMs 大的会话不可用。
```

---

## 附：已知的机制结论

本文件只讲**怎么读**。已经得出的**具体性能结论**写在这几份文档里：

```
MECH-PERF-REPORT.md    ← 机械体专项：成本构成、原版休眠为何无效、
                          WVC SelfShutdown 为何不省、三种改造方案与代码骨架
                          仅在开发仓库中提供，未随模组发布
PUBLISH.md             ← 打包发布流程与版本变更记录（同上，仅开发仓库）
```

这些结论来自特定存档（96 pawn / 338 mods），**换环境后数字不能照搬**，
但「原版休眠对 pawn 无效」「Shutdown 只省 tickAction」这类**机制结论可以复用**。
新会话要做优化时，先读机制结论，再按新数据重新定位。
