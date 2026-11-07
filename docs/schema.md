# PerfProbe 输出字段说明

本文件由 PerfProbe 自动写入，与模组目录下的 `docs/schema.md` 内容一致。
所有时间单位为**毫秒**，除非字段名另有说明。所有占比单位为**百分比**。

## 文件清单

| 文件 | 作用 |
|---|---|
| `index.json` | 所有会话的汇总索引，每次会话结束重写。AI 从这里入手 |
| `schema.md` | 本文件 |
| `AI-ANALYSIS-GUIDE.md` | 判读指南：怎么读、能得出什么结论、常见误判。开始分析前先读它 |
| `<mode>-<yyyyMMdd>-<HHmm>-s<NN>.jsonl` | 单次会话的窗口快照，一行一个 JSON 对象 |
| `<mode>-<yyyyMMdd>-<HHmm>-s<NN>.summary.md` | 该会话的人类可读汇总，含结论段与附录段 |
| `<mode>-<yyyyMMdd>-<HHmm>-s<NN>.brief.md` | 该会话的精简版（约 10 KB），给网页版 / 纯对话 AI 用 |

`<mode>` 目前恒为 `passive`。历史版本还有 `bench`（基准测试），已删除；
若读到旧的 `bench-*` 文件，字段口径完全相同，可以直接按 passive 解读。

**完整读法与常见误判见同目录下的 `AI-ANALYSIS-GUIDE.md`**，本文件只讲字段口径。
两份文档都由模组在开始采集时自动放置，无需去模组安装目录查找。

## jsonl 单行字段

| 字段 | 类型 | 含义 |
|---|---|---|
| `t` | number | 会话内已过秒数 |
| `gameTick` | int | 游戏内 tick 计数 |
| `gameDay` | number | 游戏内天数 |
| `win` | number | 本窗口长度（秒，配置值） |
| `winActual` | number | 本窗口的真实墙钟跨度（秒）。与 `win` 不等说明期间有暂停或卡帧 |
| `mode` | string | 恒为 `passive`。旧数据的 `bench` 已废弃 |
| `speed` | string | `fast`（3 倍速）/ `superfast`（6 倍速）/ `ultrafast`（15 倍速，默认关闭）。同一行不会混算两档 |
| `tps` | number | 本窗口实测 tick/秒，分母是 `winActual` 而不是 `win` |
| `msPerTick` | number | 本窗口每 tick 平均耗时，**核心指标** |
| `msPerTickP95` | number | 本窗口 tick 耗时 P95 |
| `frameMs` | number | 同期平均帧耗时 |
| `gc0` `gc1` `gc2` | int | 本窗口内 GC 回收次数 |
| `dropLines` | int | 因写盘队列满被丢弃的行数，正常为 0 |
| `gapMs` | number | 未采集档位（暂停 / `normal` / `ultrafast`）造成的覆盖缺口累计 |
| `sampleK` | int | 当前抽样分母，1 表示全量；大于 1 时 `selfMs` 是抽样后的累计值，不做外推 |
| `probeMs` | number | 本窗口探针的**计时**开销（事件数 × 会话开始时标定的单次成本）。这部分时间不落任何 `selfMs` |
| `frameCostMs` | number | 本窗口探针的**框架**开销（Harmony 委托跳转 + 静态判断 + 字典查找），由 1/64 抽样实测外推。与 `probeMs` 相加才是探针总开销 |
| `fps` | number | 本窗口帧率。分母是帧长之和而不是 `winActual`：暂停时帧照跑但不产生 tick，用墙钟会虚高 |
| `frameP50Ms` | number | 帧长中位数（毫秒） |
| `frameP95Ms` | number | 帧长 P95（毫秒），反映最近这段最差的帧 |
| `frames` | int | 本窗口采到的**活跃帧**数（本帧至少跑了一个 tick） |
| `idleFrames` | int | 本窗口被排除的**空闲帧**数（本帧一个 tick 都没跑：暂停 / 读档 / 主菜单）。它们的帧长只有十几毫秒，混进帧率会把结果抬高近一倍，因此不计入 `fps` 与 `frameAvgMs` |
| `frameAvgMs` | number | 本窗口平均帧长（毫秒） |
| `frameUnaccMs` | number | 本窗口**未测部分**累计（毫秒）= 帧长之和 − 各阶段 selfMs 之和。引擎渲染、Present、显存等待都在这里，是判断「GPU 卡还是 CPU 卡」的依据 |
| `ticksPerFrame` | number | 本窗口每帧平均跑几个 tick |
| `fstages` | array | 本窗口帧阶段数组，见下 |
| `rows` | array | 本窗口 Top-N 成本行，见下 |

## `fstages` 元素字段

帧阶段与 `rows` 是**两套独立口径**：`rows` 的分母是「本窗口所有 tick 的耗时和」，`fstages` 的分母是「本窗口的帧数」。两者不可相加，也不可互相换算占比。

| 字段 | 类型 | 含义 |
|---|---|---|
| `s` | string | 阶段名：`f_engine` / `f_play` / `f_tick` / `f_world` / `f_map` / `f_draw_prep` / `f_draw_mesh` / `f_draw_dyn` / `f_draw_ovl` / `f_gui` |
| `ms` | number | 本窗口该阶段的累计耗时（self，已扣除嵌套子段） |
| `msPerFrame` | number | 平均每帧该阶段耗时 |
| `pct` | number | 占本窗口帧长之和的百分比 |

只输出非零阶段，按 `ms` 降序。`f_tick` 是 `TickManagerUpdate` 的总耗时，它的内部细节要看 `rows`；反过来 `rows` 里的 `cat=global` 各项是 tick 内部的分解，不要与 `fstages` 混读。

## `rows` 元素字段

| 字段 | 类型 | 含义 |
|---|---|---|
| `cat` | string | `building` / `item` / `other` / `pawn` / `pawninst` / `component` / `global` / `mod` / `incident` |
| `name` | string | `defName`；`cat=mod` 时是模组名；`cat=pawn` 时是方面名；`cat=pawninst` 时是 `名字[kindDef]` 或合并后的 kindDef；`cat=global` 时是全局系统名 |
| `mod` | string | 归属模组名。`(引擎自身)` = pawn 方面行与 global 系统行（不属于任何 mod 的 def）；`(unknown)` = def 索引查不到（运行时动态生成或索引后改名）；`(merged)` = 超出每类上限后的合并槽 |
| `asm` | string | 归属程序集名 |
| `selfMs` | number | 自身耗时，**可跨行加总** |
| `inclMs` | number | 含子调用耗时，**不可加总**（会重复计数） |
| `pct` | number | `selfMs` 占本窗口总 tick 耗的百分比 |
| `n` | int | 活跃实例数（`tickerType == Never` 的不算）。summary.md 里若该行有耗时但扫不到实例，会显示 `?` 而不是 `0`，表示「分母未知」而非「没有实例」 |
| `dormant` | int | 休眠实例数，不进 `perInstMs` 分母 |
| `perInstMs` | number | 每活跃实例均耗时 |
| `conf` | number | 归因置信度。1.0 = def 索引命中；0.5 = 推断 |

### 口径约束

1. **`selfMs` 可加总，`inclMs` 不可**。跨行求和请只用 `selfMs`，其对应 `pct` 之和应 ≤ 100。
2. **pawn 不在 `building` / `item` / `other` 里出现**。`Pawn` 继承 `Thing`，探针在 thing 层显式跳过 pawn，避免双记。
3. **`cat=pawn` 的 `name` 是方面名**，不是 defName。方面取值见下表；`cat=mod` 的 `name` 是模组名。
   `cat=component` 的 `name` 是 C# 类型名（`MapComponent` 子类没有对应 def）。
4. **速度档隔离**：只有 `fast` / `superfast`（可选 `ultrafast`）会进统计，暂停与其它档位只累加 `gapMs`。
5. **每类每窗口上限 4096 个桶**，溢出合并为 `name = others` 的一行。
   summary.md 的附录与「按类合计」取同一个上限，因此不会出现「桶建了却被输出截掉」的情况。
   jsonl 另有独立的 32 行/类窗口上限，它只用于看趋势与榜首，不能用来算总量。
6. **`cat=mod` 的行由其它行按 `mod` 字段二次汇总**，与 `building` / `item` 等行共享同一分母，不能与它们相加。
7. **`cat=pawninst` 与 `cat=pawn` 覆盖同一批 pawn tick，两条线不能相加**。
   `cat=pawn` 按 tick 内的功能方面切（十四个方面，见下表），
   `cat=pawninst` 按个体切（人类与异星种族逐个，动物与机械体按 kindDef 合并）。
   两边的 `selfMs` 各自独立加总，都约等于全部 pawn 的总耗时。
   人类 / 异星种族的 `n` 恒为 1，`perInstMs` 就是该个体总的耗时；
   动物 / 机械体合并行的 `n` 是同类数量，`perInstMs` 是同类均值。
8. **`n` 的计数来源是三处合并**：`listerThings.AllThings`、`mapPawns.AllPawns`、`map.components`。
   第二源是必需的 —— 有的模组（如实测中的 TAPI_Barn）会把动物移出地图的 spawned 列表，
   只查第一源会让这些动物显示 `n = 0` 却有耗时。扫不到实例的行在 summary.md 里显示 `?`，表示分母未知。
9. **`cat=global` 与 `cat=pawn` / `building` 等不重叠**，可以相加。
   它记 self 时间：`TickList.Tick` 内部几千个 `Thing.DoTick` 已被扣走，
   剩下的是遍历与 try/catch 的容器开销。归因统一记为原版（引擎调度代码，不属任何模组）。

## `cat=pawninst` 的命名规则

| 单位类型 | 判定 | `name` 形态 | 示例 |
|---|---|---|---|
| 人类、异星种族 | `!RaceProps.Animal && !RaceProps.IsMechanoid` | `名字[kindDef]`，逐个体一行 | `小麦[Human_Baseliner]` |
| 动物 | `RaceProps.Animal` | `kindDef`，同类合并 | `Muffalo` |
| 机械体 | `RaceProps.IsMechanoid` | `kindDef`，同类合并 | `Mech_Construct` |

`RaceProps.Animal` 在 1.7 的定义是 `ToolUser == false && IsFlesh && !IsAnomalyEntity`，
因此异常实体（Anomaly）既不归动物也不归机械体，按个体列出。

## `cat=pawn` 的方面名

分三层，**外壳层之外的都只是 `pawn_body` 的内部细分**。

外壳层（inclusive 时间，来自 `DoTick` 的直接子帧）：

| 方面名 | 含义 |
|---|---|
| `pawn_body` | `Pawn.Tick` 合计（每 tick 必跑，不受任何降频 mod 影响） |
| `pawn_interval` | `Pawn.TickInterval` 合计（调用频率由 `UpdateRateTicks` 决定，视野内更频繁） |

核心组（前六，`pawn_body` 内部最粗的几个子调用）：

| 方面名 | 含义 |
|---|---|
| `pawn_think` | 思考决策（`Pawn_JobTracker.JobTrackerTick`） |
| `pawn_work` | 工作驱动（`JobDriver.DriverTick`） |
| `pawn_path` | 寻路（`Pawn_PathFollower.PatherTick`） |
| `pawn_state` | 状态效果与需求（Health / Needs / MindState / Stance / Verbs） |
| `pawn_gear` | 组件与装备（Equipment / Apparel） |
| `pawn_other` | `DoTick` 自身外壳（缓存检查、`UpdateRateTicks` 读、`directlyHeldThings.DoTick`） |

扩展组（把原本全落进 `pawn_other` 的 tracker 拆出来）：

| 方面名 | 含义 |
|---|---|
| `pawn_gene` | 基因（`Pawn_GeneTracker.GeneTrackerTick`），异星种族的主要嫌疑点 |
| `pawn_ability` | 能力（`Pawn_AbilityTracker.AbilitiesTick`） |
| `pawn_mutant` | 变异体（`Pawn_MutantTracker.MutantTrackerTick`），仅 Anomaly |
| `pawn_rare` | 低频 tick（`Pawn.TickRare`，每 250 刻一次） |
| `pawn_render` | 渲染特化（`PawnRenderer.EffectersTick`） |
| `pawn_misc` | 杂项（Inventory / Rope / Flight / NativeVerbs） |

**不变式（相加规则）**：`pawn_body + pawn_interval + pawn_other` 恒等于所有 pawn 的
`Thing.DoTick` 总时间。核心组与扩展组是 `pawn_body` 内部的细分，
**不能与外壳层相加** —— 它们的 `selfMs` 已经含在 `pawn_body` 里了。

summary.md 的「按类合计」与「合计已归因」两处已按此规则计算（只累加外壳层三项）。
v1.3.1 之前这两处会把十四个方面行全部相加，导致「合计已归因」能超过 100%（实测 119.68%），
并与同一文件里的「人物合计（外壳层三项之和）」互相矛盾。**读到旧数据时需自行按本节重算。**

## `cat=global` 的系统名

| 名称 | 对应方法 |
|---|---|
| `map_pre_tick` | `Map.MapPreTick`（逐地图） |
| `map_post_tick` | `Map.MapPostTick`（逐地图） |
| `ticklist` | `TickList.Tick`（Normal / Rare / Long 三个列表共用） |
| `date_notifier` | `DateNotifier.DateNotifierTick` |
| `scenario` | `Scenario.TickScenario` |
| `world_tick` | `World.WorldTick` |
| `world_post_tick` | `World.WorldPostTick` |
| `story_watcher` | `StoryWatcher.StoryWatcherTick` |
| `game_ender` | `GameEnder.GameEndTick` |
| `storyteller` | `Storyteller.StorytellerTick` |
| `tale_manager` | `TaleManager.TaleManagerTick` |
| `quest_manager` | `QuestManager.QuestManagerTick` |
| `history` | `History.HistoryTick` |
| `game_component` | `GameComponentUtility.GameComponentTick`（全体 GameComponent） |
| `letter_stack` | `LetterStack.LetterStackTick` |
| `autosaver` | `Autosaver.AutosaverTick` |
| `transport_ships` | `TransportShipManager.ShipObjectsTick` |

未纳入的：`FilthMonitor.FilthMonitorTick` —— 该类在 1.7 是 `internal`，外部程序集引用不到。

## `index.json` 结构

| 字段 | 含义 |
|---|---|
| `game` | 游戏版本 |
| `probe` | 探针版本 |
| `generatedAt` | 本文件生成时间（UTC ISO8601） |
| `schema` | 字段说明文件名 |
| `sessions` | 会话数组，按开始时间倒序 |
| `sessions[].file` | 对应 jsonl 文件名 |
| `sessions[].summary` | 对应 summary.md 文件名 |
| `sessions[].mode` | `passive`（旧数据可能是 `bench`） |
| `sessions[].startedAt` | 该 summary.md 的最后写入时间（UTC ISO8601） |
| `sessions[].durationSec` | 会话时长（秒） |
| `sessions[].lines` | jsonl 行数 |
| `sessions[].msPerTickAvg` | 全会话每 tick 均耗时 |
| `sessions[].msPerTickP95` | 全会话 tick 耗时 P95 |
| `sessions[].dropped` | 该会话写盘丢行数 |
| `sessions[].speeds` | 本次会话出现过的速度档数组，取值 `fast` / `superfast` / `ultrafast` |
| `retention.keep` | 保留会话数上限 |
| `retention.deletedThisRun` | 本次启动清理掉的会话文件名数组 |

`index.json` 不保存隐藏状态：每次会话结束时重新扫描目录，
从各会话的 `summary.md` 表格里反解出上述数字，因此进程重启后历史仍在。

## 保留策略

只删除严格匹配 `^(passive|bench)-\d{8}-\d{4}-s\d{2}\.(jsonl|summary\.md|brief\.md)$` 的文件，
且按会话基名**成组删除**。`index.json` 与 `schema.md` 永不删除。
正则里保留 `bench` 分支只为兼容历史文件；新会话只会生成 `passive-*`。
