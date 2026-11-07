# PerfProbe — 实施方案（定稿）

> 面向 RimWorld 1.7.4995 rev309 的**零侵入性能归因探针**。
> 1.6 提供同源代码的 `net472` 编译版（D12），仅作基础兼容、未实测。
> 目标不是"实时看曲线"，而是**把运行期开销落成机器可读的日志**，让 AI 能直接回答
> 「哪栋建筑 / 哪类物品 / 哪个人物 / 哪个事件 / 哪个模组在吃性能」。

---

## 1. 目标与非目标

### 目标

| # | 目标 | 判据 |
|---|---|---|
| G1 | 采到**每 tick 平均耗时**（毫秒），且不受帧率抖动污染 | jsonl 每行 `msPerTick` = 本窗口 tick 总耗时 / tick 数，用 QPC 计时，不用 `Time.deltaTime` |
| G2 | 归因到 **building / item / other / pawn / component / incident / mod** 七类 | 每类有独立 `cat` 值，行内带 `mod` / `asm` / `conf` |
| G3 | 输出 **AI 可直读**的结构化日志 | `jsonl`（逐窗口）+ `index.json`（跨会话）+ `schema.md`（字段自解释）+ `summary.md`（人读） |
| G4 | 采集本身**几乎不占性能** | 抽样（默认 1/8）+ 相位轮换 + 无反射热路径 + 无字符串拼接热路径 + 后台线程写盘 |
| G5 | **默认关闭**，关闭时对游戏零行为影响 | 所有 patch 前缀第一句就是 `if (!ProbeEngine.TickActive) return true;` |
| G6 | 崩溃/异常不污染存档 | patch 全部 try/catch，异常只打一次日志，从不 rethrow |

### 非目标（明确不做）

- ❌ 不做实时 UI 图表（现有模组已够用，且这是帧开销来源）
- ❌ 不做自动优化建议 / 自动禁用模组（只出数据，判断交给 AI 与用户）
- ❌ 不做内存 / GC 分配的逐对象归因（只记录三代 GC 次数）
- ❌ 不改任何原版行为、不改 def、不注入 UI（除设置面板）
- ❌ 不跨线程读游戏对象（写盘线程只碰字符串）

---

## 2. 关键设计决策

### D1 计时边界选 `Thing.DoTick`，而不是 `Pawn.Tick`

`Pawn.Tick` 是 `protected override`，`Thing.DoTick` 是 `public void`。选后者：
- 挂载简单，不用 `BindingFlags.NonPublic`
- 一个入口同时覆盖 pawn 与非 pawn
- pawn 分支转交 `PawnTickContext` 做十四方面分解（外壳层 3 + 叶子层 11）

**不变量**：`Thing.DoTick` 里 `if (thing is Pawn)` 走 pawn 链路，非 pawn 才进 `cat=building/item/other`。
否则 pawn 会被双记，`selfMs` 加总会超过 100%。

### D2 速度档隔离

RimWorld 五档速度（`TimeSpeed`：Paused / Normal=1x / Fast=3x / Superfast=6x / Ultrafast=15x）里，`Superfast` 与 `Ultrafast` 一帧跑多个 tick，把每帧固定开销（渲染、UI、GC 边界）摊薄到更多 tick 上，`msPerTick` 会被系统性拉低。

**混档平均不是小偏差，是让跨会话结论失去可比性的测量错误。**

因此：默认只采 `fast`（3x）与 `superfast`（6x），`ultrafast`（15x）默认关——它一帧塞入太多 tick，单 tick 均摊会被系统性低估；未采集档位只累加 `gapMs`，并在 `summary.md` 里显式报告覆盖缺口。

### D3 抽样 + 相位轮换

`SampleGate.ShouldSample(id)` 判 `(id + phase) % k == 0`，每 `PhaseTicks` 刻轮换 `phase`。
- 默认 `k=8`：单个对象 1/8 的 tick 被计时，开销降到 1/8
- 相位轮换让"哪些对象被采样"在时间上均匀分布，避免总采同一批
- 落账时按 `k` 外推，`summary.md` 与 jsonl 都记录 `sampleK`，AI 知道数据是外推的

### D4 归因三层回退 + 置信度

`AttribIndex.Build()` 建三张表：`asm→mod`、`defName→mod`、`Type→mod`。

查询顺序：`ModOfDef(defName)` 命中 → `conf=1.0`；否则 `(unknown)` + `conf=0.5`。
MapComponent 没有 def，退到 `ModOfType(Type)`（含基类兜底最多 8 层）→ `conf=1.0`，再退 `ModOfAssembly` → `conf=0.8`，最后 `conf=0.5`。

**`conf` 是给 AI 的诚实标记**，避免把推断当成事实写进结论。

### D5 双字典：窗口态 + 会话态

`CostTable` 里 `_slots`（窗口）与 `_session`（会话）并行累加。
- 窗口态每 `WindowSec` 秒落一行 jsonl 后 `EndWindow()` 清零
- 会话态整场不清，`summary.md` 用 `SessionSnapshot`

避免"会话汇总靠累加 jsonl 行"这种会把抽样外推误差放大的做法。

### D6 有界队列 + 后台线程写盘

`JsonlWriter` 用 `MaxQueue=8192` 的有界队列，主线程只 `Enqueue(string)`，后台线程 `FlushEveryLines=16` 批量写。
队列满时丢行并计数到 `Dropped`，**不阻塞主线程**。`dropLines` 字段进 jsonl，AI 能看出数据是否可信。

### D7 输出目录三级回退

`ProbePaths`：配置目录 → `GenFilePaths.SaveDataFolderPath/PerfProbe` → `Path.GetTempPath()/PerfProbe` → 空串（不落盘）。
每级都做真实写删探针测试（`.perfprobe-write-test`），回退原因写进 `summary.md` 的 `目录回退原因` 行。

### D8 保留策略：成对删除 + 严格文件名

`Retention.NameRx = ^(passive|bench)-\d{8}-\d{4}-s\d{2}\.(jsonl|summary\.md|brief\.md)$`
只删匹配这个正则的文件，且按会话基名成组删除。`index.json` 与 `schema.md` 永不删除。
**这是防止误删用户文件的安全边界**——不匹配的文件一个都不动。
`bench` 分支保留只为兼容历史文件；Bench 采集模式已删除（见 D11）。

### D9 patch 目标只选采集窗口内的

已反编译核实：
- `MapComponentUtility.MapComponentTick(this)` 在 `Verse.Map.cs:1134`，**位于 `MapPostTick` 内部**（`MapPostTick` 在 `Verse.Map.cs:966`，由 `TickManager.DoSingleTick` 末尾 `Verse.TickManager.cs:454` 调用）→ **可以 patch**
- `MapComponentUtility.MapComponentUpdate(this)` 在 `Verse.Map.cs:1202`，位于 `Map.MapUpdate()`（`Verse.Map.cs:1148`）内部，由 `Game.UpdatePlay()` 调用 → **不在采集窗口，不 patch**

`MapComponent.MapComponentTick()` 是基类 `public virtual` 空实现，子类 override 才有效 → 用 `GenTypes.AllSubclasses(typeof(MapComponent))` 枚举，对每个自己声明了该方法的子类单独挂 patch，跳过 abstract。

### D10 嵌套安全

pawn 会 tick 被搬运的另一个 pawn（`directlyHeldThings.DoTick()` 在 `Verse.Thing.cs:767`）。
- `DepthTracker` 用 `[ThreadStatic]` 栈 + `MaxDepth=64`
- `PawnTickContext` 用栈式上下文 `MaxNesting=8`，超限返回 `false`，调用方退掉刚进的深度层
- `DepthTracker.Exit(bool entered)` 修复"Enter 栈满但 Exit 仍弹栈"的错位 bug

**不变量**：pawn 的外壳层三项（`pawn_body` + `pawn_interval` + `pawn_other`）之和 == 该 pawn 的 `DoTick` 总时间。`pawn_other` 是 `Thing.DoTick` 自身外壳，不再是残差兜底。

### D11 删除 Bench（A/B 基准）模式

原设计里 `ProbeMode.Bench` 用「前 90 秒只记帧级、之后逐对象计时」的对比估算探针自身开销。删除理由三条：

1. **探针开销已有两条更准的度量**：`ProbeEngine.CalibrateOverhead()` 在会话开始时用真实热路径（`DepthTracker`）跑 1000 次 Enter/Exit 取均值标定单次成本；`Patch_Thing_DoTick.FrameSampleMask = 63` 每 64 个对象抽 1 个、在 Prefix/Postfix 各自入口出口取时间戳后 ×64 外推。这两条在被动采集下一直有效，A/B 是第三条且最粗。
2. **A/B 法两段不是同一时段**：开局的地图初始化、pawn 生成、寻路缓存冷启动全落在浅段，测出的差异主要是游戏本身前后不同，不是探针开销。
3. **该模式的「浅段不采集明细」从未实现**：全仓没有任何 patch 读 `InDeepPhase()`，浅段也在全量计时，两段本质相同，对比失去意义。

删除范围：`ProbeMode.Bench` 枚举值、`ProbeSession` 的 `AbQuietSec`/`AbQuietMs`/`AbQuietTicks`/`AbDeepMs`/`AbDeepTicks` 五个字段与 `InDeepPhase()`、`ProbeEngine.EndTick` 里的 A/B 累加分枝、`SummaryWriter.AppendAb()`、`ProbeSettings.StartBenchRequested`、UI 的基准测试按钮。

**保留**：`ProbeMode` 枚举本身、`ToTag()`、jsonl 的 `mode` 字段、`index.json` 的 `sessions[].mode`、`Retention.NameRx` 里的 `bench` 分支——这些都是文件格式的一部分，删掉会破坏对旧数据的兼容。`ToTag()` 现在恒返回 `"passive"`。

### D12 1.6 兼容做成「双目标框架 + 基础兼容声明」，不做功能降级

**背景**：1.7 用 Waylith 引擎 + `net10.0`，1.6 是 Unity + `net472`，两者不是同一套运行时。官方声明「99% 的 1.6 mod 能在 1.7 直接跑」，但反向不成立 —— 1.7 产物在 1.6 里连字节码都读不动（`deps.json` 写死 `.NETCoreApp,Version=v10.0`）。

**决策**：`PerfProbe.csproj` 用 `<TargetFrameworks>net10.0;net472</TargetFrameworks>` 一次编两份，1.6 那份对着 `Source\refs\1.6\` 的官方参考程序集（NuGet `Krafs.Rimworld.Ref 1.6.4871`）编。不做 `#if` 条件编译、不裁功能 —— 全部 41 个源文件在两边都完整编入。

**能这么做的依据（逐项核实过，不是推测）**：
1. 39 个 `[HarmonyPatch]` 目标类型在 1.6 里全部存在，签名一致
2. 十个帧阶段挂载点（`MapMeshDrawerUpdate_First` / `DrawMapMesh` / `DrawDynamicThings` / `DrawAllOverlays` / `FleckManagerDraw` / `TemporaryThingDrawer.Draw` / `TickManagerUpdate` / `UpdatePlay` / `WorldUpdate` / `MapUpdate`）全部存在
3. `Root.AverageFrameTime` / `TickManager.TicksThisFrame` / `MeanTickTime` / `TickRateMultiplier` / `WorstAllowedFPS` 全部存在
4. 代码侧零 `Waylith` / `UnityStubs` / `UnityRuntime` 类型引用、零 C# 8+ 语法、零 .NET Core 专属 API；`using UnityEngine` 只有两个文件用三个成员（`Vector2.zero` / `Application.consoleLogPath` / `Application.OpenURL`），这三个在 `UnityEngine.CoreModule` 里都有

**为什么只承诺「基础兼容」而不是「支持」**：本机只有 1.7，无法在 1.6 里实跑。编译通过 ≠ 运行行为一致。两个真实风险面：① Harmony 1.6 是 2.x / 1.7 是 3.x，对 `__instance` / `__result` 的类型检查严格度不同；② 1.6 是 Unity 渲染、1.7 是 Waylith 引擎，帧阶段钩子的触发时机与次数可能不同。**在 `About.xml` 里明确写「1.6 基础兼容，不保证功能可用」，并请玩家回传 1.6 的 `Player.log`。**

**不变量（会破坏数据的硬约束）**：
1. 1.6 那份必须引 `mscorlib`、1.7 那份必须不引 —— 反了进游戏就是 `System.BadImageFormatException`
2. 1.6 侧不能生成 `deps.json`
3. 1.6 参考程序集里**没有 `RimWorld.dll`**（1.6 时代所有类型都在 `Assembly-CSharp.dll`），所以 `net472` 组的引用清单与 `net10.0` 组不同
4. `LoadFolders.xml` 的 `<v1.6>` 分支**不含 `<li>/</li>`** —— 开发根目录有 `Source` / `refs` / `tools`，1.6 不该把它们扫进 Def 库
5. 这三条由 `tools\pack.ps1 -Action check` 的第 2 / 4 / 5b 项机械校验，不靠人记

**为什么不选另外两条路**：
- *只发 1.7，让 1.6 玩家自己找旧版*：1.6 玩家占比仍不小，而这个探针本来就是给「卡得受不了才来找工具」的人用的，门一关就永远拿不到数据。
- *维护两套源码分支*：41 个文件、9373 行，分叉后每个 bug 要修两遍，且本机无 1.6 环境，第二套分支必然腐烂。

---

## 3. 模块划分

```
PerfProbe/
├── About/About.xml              模组元数据（packageId=perfprobe.runtime）
├── LoadFolders.xml              v1.7 与 v1.6 两套加载规则
├── 1.7/Assemblies/PerfProbe.dll net10.0 编译产物（+ deps.json）
├── 1.6/Assemblies/PerfProbe.dll net472 编译产物（无 deps.json）
├── refs/1.6/                    1.6 官方参考程序集（Assembly-CSharp / UnityEngine.CoreModule / System.IO.Compression）
├── docs/schema.md               与运行时写入的 schema.md 同源文本
├── docs/AI-ANALYSIS-GUIDE.md    AI 性能分析方法论（读法、限制、误判清单）
├── docs/BUILD.md                构建与部署
├── PLAN.md                      本文件
└── Source/PerfProbe/
    ├── PerfProbeMod.cs          入口：构造函数只存设置；挂载在 PerfProbeBootstrap
    │                            （[StaticConstructorOnStartup]）
    ├── Core/
    │   ├── PLog.cs              日志（前缀 [PerfProbe]，ErrOnce 防刷屏）
    │   ├── ProbePaths.cs        输出目录三级回退 + 可写性探针
    │   ├── ProbeMode.cs         Passive / Bench
    │   ├── SpeedBucket.cs       速度档映射与"是否采集"判定
    │   ├── ProbeSettings.cs     ModSettings（含 ClampAll 与范围常量）
    │   └── Retention.cs         文件名正则、会话基名、成对清理
    ├── Runtime/
    │   ├── Qpc.cs               Stopwatch 时间戳 + 毫秒换算
    │   ├── FrameProbe.cs        双环（窗口/会话）统计：均值、P95、中位数、TPS、缺口
    │   ├── SampleGate.cs        抽样门 + 相位轮换
    │   ├── DepthTracker.cs      [ThreadStatic] 嵌套深度栈
    │   ├── PawnTickContext.cs   pawn 十四方面栈式上下文 + 个体/种族行落账
    │   ├── AliveCounter.cs      活跃/休眠实例计数（每窗口 Rebuild 一次）
    │   ├── Hashing.cs           稳定字符串哈希（跨进程一致，不用 GetHashCode）
    │   └── ProbeEngine.cs       总控：会话生命周期、tick 编排、窗口冲刷、jsonl 组装
    ├── Attrib/
    │   ├── AttribIndex.cs       asm/def/type → mod 三张表
    │   ├── PawnIdentity.cs      pawn 个体/种族二级归因 + 命名（人类逐个、动物机械合并）
    │   ├── ThingCost.cs         分类与键构造
    │   ├── PawnCost.cs          十四方面常量与中文标签（外壳层 / 核心组 / 扩展组三档）
    │   └── CostTable.cs         双字典累加 + 溢出合并 + 按 mod 二次汇总
    ├── Patches/
    │   ├── Patch_TickManager.cs      DoSingleTick 前缀/后缀/终结器
    │   ├── Patch_Thing_DoTick.cs     thing 计时入口（pawn 分流）
    │   ├── Patch_PawnAspects.cs      9 个 pawn 子方面 patch
    │   └── Patch_MapComponents.cs    动态枚举并挂 MapComponentTick
    ├── Output/
    │   ├── Json.cs                  手写 JSON 转义与数字格式（InvariantCulture）
    │   ├── JsonlWriter.cs           有界队列 + 后台写盘线程
    │   ├── ProbeSession.cs          单会话文件句柄与元数据
    │   ├── SummaryWriter.cs         人读汇总（结论段 + 附录段）
    │   ├── IndexWriter.cs           扫描 summary.md 反解数字 → index.json
    │   └── SchemaWriter.cs          写 schema.md（内容一致则跳过）
    └── UI/ProbeSettingsUI.cs        设置面板（开关、目录、窗口、抽样、保留、开始/停止两个按钮）
```

---

## 4. 数据流

```
TickManager.DoSingleTick
  ├─ Prefix  → ProbeEngine.BeginTick()
  │             ├─ SpeedBucket.ShouldCapture(settings)? 否 → 只记缺口
  │             ├─ SampleGate.Tick() 每 PhaseTicks 刻轮换相位
  │             └─ TickActive = true
  │
  ├─ 原版执行（MapPreTick / ticksGame++ / tickList*.Tick() / MapPostTick / …）
  │   ├─ Thing.DoTick ──Prefix──> SampleGate.ShouldSample(id)?
  │   │                            ├─ 是 + 非 pawn → DepthTracker.Enter, state=Thing
  │   │                            ├─ 是 + pawn    → DepthTracker.Enter + PawnTickContext.Begin
  │   │                            └─ 否 → 直接放行
  │   │   └──Postfix──> DepthTracker.Exit() → selfMs
  │   │                 ├─ 非 pawn → ProbeEngine.AddThing(t, selfMs, selfMs)
  │   │                 └─ pawn    → PawnTickContext.End(selfMs) → 外壳层 + 叶子层分组落账
  │   │
  │   ├─ Pawn_JobTracker.JobTrackerTick ──> PawnTickContext.AddAspect(IdThink, selfMs)
  │   ├─ JobDriver.DriverTick ────────────> AddAspect(IdWork)
  │   ├─ Pawn_PathFollower.PatherTick ────> AddAspect(IdPath)
  │   ├─ HealthTick / NeedsTrackerTickInterval / MindStateTickInterval
  │   │   / StanceTrackerTick / VerbsTick ─> AddAspect(IdState)
  │   ├─ EquipmentTrackerTick / ApparelTrackerTickInterval ─> AddAspect(IdGear)
  │   └─ MapComponent 子类.MapComponentTick ─> ProbeEngine.AddComponent(typeName, mod, asm, conf, …)
  │
  └─ Postfix / Finalizer → ProbeEngine.FinishTick(captured)
        ├─ FrameProbe.AddTick(ms, captured)   ← 环形缓冲
        ├─ 累计 _windowMs / _windowTicks
        └─ 到 WindowSec 秒 → FlushWindow()
              ├─ AliveCounter.Rebuild()       ← 每窗口一次，不是每 tick
              ├─ CostTable.Snapshot(32, windowTotalMs)
              ├─ CostTable.AggregateByMod(...)
              ├─ ProbeEngine.BuildJsonLine(...) → JsonlWriter.Enqueue(line)  ← 后台线程写
              └─ CostTable.EndWindow()        ← 窗口态清零，会话态保留

会话结束（设置关闭 / 游戏切换 / 手动停止）
  ├─ FlushWindow(force)      ← 必须先冲刷再清 _session，否则最后一窗口丢失
  ├─ CostTable.SessionSnapshot(200) + AggregateByMod
  ├─ ProbeSession.CloseFiles()
  ├─ Retention.Enforce(dir, KeepSessions)   ← 成对删除旧会话
  ├─ SummaryWriter.Write(...) → <base>.summary.md
  └─ IndexWriter.Scan(dir) + Write(dir, ...) → index.json
```

---

## 5. 输出物

| 文件 | 谁读 | 内容 |
|---|---|---|
| `index.json` | **AI 入口** | 全会话列表：时长、`msPerTickAvg`、P95、行数、速度档、丢行数 |
| `schema.md` | **AI 入口** | 字段自解释 + 七条口径约束（防 AI 误用 `inclMs` 求和、误把 `pawn` 与 `pawninst` 相加） |
| `*.jsonl` | AI 深挖 | 每窗口一行：`msPerTick` + `rows` Top-N（`cat`/`name`/`mod`/`selfMs`/`pct`/`n`/`perInstMs`/`conf`） |
| `*.summary.md` | 人 + AI | 结论段（最高耗时模组/对象、pawn 方面分布）+ 附录段（完整表格、速度档、缺口、回退原因） |

**`summary.md` 的数字用固定表格标签**，`IndexWriter` 用 5 条正则反解回去，因此进程重启后 `index.json` 仍能重建历史。

模组文件夹内另有三份面向人的文档：

| 文件 | 内容 |
|---|---|
| `docs/AI-ANALYSIS-GUIDE.md` | **AI 性能分析方法论**：按 AI 形态选文件（brief / summary / jsonl）、七类 `cat` 的层级关系（哪些能相加、哪些不能）、各表用途与限制、抽样与截断对精度的影响、从数字定位到代码的流程、8 条常见误判 |
| `docs/MECH-PERF-REPORT.md` | **机械体性能专项报告**（10 章）：26.6 只机械体的真实成本 0.798 ms/tick、13 条个体明细、原版休眠为何无效、WVC SelfShutdown 只省 tickAction、三条优化方案对比、给模组开发者的落地清单 |
| `docs/PUBLISH.md` | 创意工坊打包流程：`tools/pack.ps1` 四动作、7 项自检、版本变更记录 |
| `docs/BUILD.md` | 构建环境、编译命令、部署结构、启用步骤、排错表 |

`AI-ANALYSIS-GUIDE.md` 的关键约定：**查具体模组方法前，先给出 AI 认为的模组包名与程序集名，等玩家回报路径后再定向反编译**，不要全目录检索（300+ 模组、数万文件时会超时）。

历史实测数据（2026-10-29 / 10-30 两轮，共 3 个会话）的关键发现，供快速索引：

- 最新干净基线（`passive-20261030-2104-s01`，关并行 + 装 Slower Pawn Tick Rate）：`msPerTick` 中位 **5.16**、TPS 中位 **149**、`frameMs` 中位 62.92
- 去重后总账 **4.9632 ms/tick**：pawn 外壳三项 2.7936（56%）/ global 0.9101（18%）/ building 0.7076（14%）/ component 0.4476（9%）/ item 0.0744 / other 0.0300
- 建筑层 Core 只占 3.3%、组件层 Core 占 **0%** —— 这两层几乎全是 mod 内容
- `pawn_other` 已改义：从「未 patch 残差」变为「`Thing.DoTick` 自身外壳」。真实的 pawn 时间分布看 `pawn_body` / `pawn_interval` 两项
- **各类采样倍率不同**：`component` 不受抽样，其余 ×8。折算后覆盖率 70.4%
- 优化优先级：减 pawn 数量 > 停 Raven 建筑/传送带线路 > 减 IRBM/KT_Tesla/载具 > 物品/Mote 不动

---

## 6. 验收判据

| # | 判据 | 验证方式 |
|---|---|---|
| A1 | 编译通过 | `dotnet build -c Release` 无 error |
| A2 | DLL 落到 `1.7/Assemblies/PerfProbe.dll` | 文件存在 |
| A3 | 关闭时零影响 | 设置面板默认 `Enabled=false`，patch 前缀第一句即返回 |
| A4 | `msPerTick` 用 QPC 而非帧时间 | 代码审查 `Qpc.Now()` |
| A5 | 速度档隔离生效 | `SpeedBucket.ShouldCapture` 只认 Fast/Superfast（Ultrafast 需手动开启） |
| A6 | pawn 不双记 | `Patch_Thing_DoTick` 里 `is Pawn` 分流；`cat=pawn` 的 name 是方面名 |
| A7 | 外壳层三项之和 == pawn 的 DoTick 总时间 | `PawnTickContext.End` 的 shellSum 判定 |
| A8 | 只 patch 采集窗口内的方法 | 不 patch `MapComponentUpdate` |
| A9 | 热路径无反射 / 无 `DateTime.Now` / 无 `GC.Collect` | 代码审查 |
| A10 | 写盘不阻塞主线程 | `JsonlWriter` 有界队列 + 后台线程 |
| A11 | 队列满只丢行不崩 | `Dropped` 计数进 `dropLines` |
| A12 | 清理只删自家文件 | `Retention.NameRx` 严格匹配 + 成对删除 |
| A13 | 输出目录不可写时能回退 | `ProbePaths` 三级回退 + `FallbackReason` |
| A14 | 最后一窗口不丢 | `EndSession` 先 `FlushWindow` 再清 `_session` |
| A15 | 异常不崩游戏 | 全部 patch try/catch + `PLog.ErrOnce` |
| A16 | 嵌套 pawn 不互相覆盖 | `PawnTickContext` 栈式 + `MaxNesting=8` |
| A17 | `index.json` 可跨进程重建 | `IndexWriter.Scan` 从 summary.md 反解 |
| A18 | `schema.md` 与代码字段一致 | 字段名逐一比对 |
| A19 | XML 可解析 | `About.xml` / `LoadFolders.xml` 解析通过 |
| A20 | 双目标都出产物 | `1.7/Assemblies/PerfProbe.dll` 与 `1.6/Assemblies/PerfProbe.dll` 同时存在 |
| A21 | BCL 引用不串版 | 1.6 那份引 `mscorlib`、1.7 那份不引（`pack.ps1 -Action check` 第 5b 项） |
| A22 | 1.6 侧不带 `deps.json` | `1.6/Assemblies/` 下只有 dll |

---

## 7. 假设与风险

| ID | 假设 | 若不成立的影响 | 应对 |
|---|---|---|---|
| H1 | `TickManager.DoSingleTick` 是 tick 总入口 | 采集不到完整 tick | 已反编译核实（`Verse.TickManager.cs:361`） |
| H2 | `Thing.DoTick` 是所有 tick 中 thing 的公共入口 | 部分对象漏采 | 已核实（`Verse.Thing.cs:700`），Pawn 走此入口 |
| H3 | `MapComponentTick` 在采集窗口内 | component 类无数据 | 已核实（`Verse.Map.cs:1134`） |
| H4 | Harmony 3.0.0.0 与 1.7 兼容 | 挂载失败 | 选 workshop 版 `0Harmony.dll` 3.0.0.0（非 Mods 目录的 2.4.1.0） |
| H8 | 1.6 侧编译能过约等于 1.6 能跑 | 运行期行为差异（Harmony 2.x 类型检查更宽、Unity 渲染管线触发时机不同） | **无法消除，已在 `About.xml` 声明「基础兼容，不保证功能可用」**；靠玩家回传 1.6 的 `Player.log` 逐步确认 |
| H5 | `def.defNameHash` 预缓存，读取零分配 | 热路径有分配 | 已核实（`Verse.Def.cs:51`，`ResolveDefNameHash` 内 `defName.GetHashCode()`） |
| H6 | dotnet 10 SDK 能编 net10.0 | 编不出 | 需要 `dotnet --list-sdks` 里有 `10.0.*`；开发机用随 RimModManager 附带的便携版 |
| H7 | 单线程 tick | 需要加锁 | 游戏 tick 在主线程，`DepthTracker` 用 `[ThreadStatic]` 已足够 |

---

## 8. 构建与部署

见 `docs/BUILD.md`。
