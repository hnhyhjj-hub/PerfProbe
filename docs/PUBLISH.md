# PerfProbe 创意工坊发布流程

## 设计：源码原地不动

游戏上传读的是 **mod 根目录本身**：

```csharp
// Verse.Steam.Workshop.BakeAndUpload -> SetItemContent
SteamUGC.SetItemContent(updateHandle, hook.Directory.FullName);
```

Steam 端整目录打包，**没有白名单，`.gitignore` 无效**。PerfProbe 没有 `Textures`/`Sounds`
资产包，走短路径 `Upload(mod)` 直接传原目录，连 `BuildStagingDirectory` 的过滤都没有。

所以：**发布件必须是一个独立目录，不能是开发目录。**

| 目录 | 内容 | 用途 |
|---|---|---|
| `Mods\PerfProbe\` | `Source\` `refs\` `docs\` `tools\` `PLAN.md` `About\` `1.7\` `1.6\` | 开发，源码原地不动 |
| `Mods\PerfProbe-Publish\` | 只有 `About\` + `1.7\` + `1.6\` + `LoadFolders.xml` + `docs\` | 上传用，每次重建 |

## packageId 冲突怎么解决

两份 `packageId` 都是 `perfprobe.runtime`。`Verse.ModLister.TryAddMod` 的处理：

```csharp
ModMetaData modWithIdentifier = GetModWithIdentifier(mod.PackageId);
if (modWithIdentifier != null && mod.RootDir.FullName != modWithIdentifier.RootDir.FullName) {
    ...
    Log.Error("Tried loading mod with the same packageId multiple times: ... Ignoring the duplicates.");
    return false;
}
```

后加载的那份被报错忽略。而 `ModLister` 用 `new DirectoryInfo(ModsFolderPath).GetDirectories()`
枚举——**顺序不保证是字母序**（实测本机就不一致），所以哪份赢是不确定的。

解法：上传前把开发目录的 `About` 改名成 `About.off`。

依据（`Verse.DirectXmlLoader.ItemFromXmlFile`）：

```csharp
if (!new FileInfo(filePath).Exists) { return new T(); }
```

缺 `About.xml` 时返回空 `ModMetaDataInternal`，`packageId` 为空串，
于是这个目录根本进不了 `modsByPackageId`，游戏当无效目录跳过——不产生冲突、不报错。
实测 `Mods\.dsh-project-memory` 就是这样被跳过的。

## 操作流程

```powershell
cd <仓库根目录>

# 1) 编译（双目标：产物落 1.7\Assemblies\ 与 1.6\Assemblies\）
cd Source\PerfProbe
$env:DOTNET_CLI_HOME="$env:TEMP\pp-dotnet-home"
dotnet build -c Release -v q --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false -nodeReuse:false
cd ..\..

# 2) 重建发布目录 + 自检
powershell -ExecutionPolicy Bypass -File .\tools\pack.ps1 pack

# 3) 进入发布状态（开发目录 About -> About.off）
powershell -ExecutionPolicy Bypass -File .\tools\pack.ps1 publish

# 4) 进游戏上传
#    主菜单 -> Mods -> 列表里只有一份 PerfProbe（来自 PerfProbe-Publish）
#    -> 底部 Upload to Steam Workshop
#    首次上传同意 Steam 工坊协议，游戏自动写 About\PublishedFileId.txt

# 5) 恢复开发状态
powershell -ExecutionPolicy Bypass -File .\tools\pack.ps1 restore
```

`publish` 会先跑一遍 `pack`，所以第 2 步可以省略。

## 四个动作

| 动作 | 做什么 | 碰开发目录吗 |
|---|---|---|
| `pack` | 重建 `PerfProbe-Publish` 并跑 8 项自检 | 只读 |
| `publish` | `pack` + 把 `About` 改名 `About.off` | 只改一个目录名 |
| `restore` | `About.off` 改回 `About` | 只改一个目录名 |
| `check` | 只跑自检 | 只读 |

`publish` / `restore` 都只做**目录改名**，不移动、不删除、不复制源码。

## 自检项

| 检查项 | 判据 |
|---|---|
| 开发目录源码完整 | `Source\` 下有 `.cs`，`docs\` 与 `PLAN.md` 存在 |
| 发布目录只含必需件 | 顶层只有 `About` / `1.7` / `1.6` / `LoadFolders.xml` / `docs`；`docs` 里恰好是 `schema.md` 与 `AI-ANALYSIS-GUIDE.md` |
| XML 可解析 | 发布目录所有 `.xml` 用 UTF-8 解析通过 |
| 程序集目录干净 | `1.7\Assemblies\` 与 `1.6\Assemblies\` 都只有 `.dll` + `.json`；且 1.6 侧**不能有** `deps.json` |
| dll 无本机路径 | 两份 dll 的 ASCII 串都匹配不到 `X:\\` |
| **BCL 引用不串版** | 1.6 那份必须含 `mscorlib`，1.7 那份必须不含。反了进游戏就是 `System.BadImageFormatException` |
| 发布件无本机路径 | 匹配不到 `Users\ASUS` / `Program Files` / `RimModManager` / `E:\` / `D:\`（不查 `AppData` 与 `C:\Users\` 这类平台通用写法，它们本就该出现在文档里） |
| packageId 冲突状态 | 两份都有 About 时提示先跑 `publish` |

## 发布包里为什么带 `docs\`

`docs\` 有 5 个文件，但只有 2 个进包：

| 文件 | 进包 | 原因 |
|---|---|---|
| `schema.md` | ✅ | 字段口径，AI 读数据时必须对照 |
| `AI-ANALYSIS-GUIDE.md` | ✅ | 判读规则，决定 AI 会不会得出错误结论 |
| `BUILD.md` | ❌ | 开发环境与本机路径，对玩家无用 |
| `PUBLISH.md` | ❌ | 发布流程，开发资产 |
| `MECH-PERF-REPORT.md` | ❌ | 特定存档的实测结论，换环境不能照搬 |

**光带进包还不够。** 玩家把数据拿去问 AI 时，AI 只看得到数据目录里的文件；指南若只躺在
workshop 深层目录里，AI 根本不知道它存在，于是只能凭字段名猜口径——这正是最容易出错的场景
（例如把 `cat=mod` 的汇总值当明细相加，得出 117% 这种结论）。

所以 `GuideWriter` 在**每次开始采集**时把这两份文档复制到输出目录：

```
<输出目录>/
├── schema.md              ← SchemaWriter 从代码内嵌文本写
├── AI-ANALYSIS-GUIDE.md   ← GuideWriter 从 mod 目录 docs/ 复制
├── index.json
└── passive-*.jsonl / .summary.md / .brief.md
```

实现见 `Source\PerfProbe\Output\GuideWriter.cs`：源路径取 `PerfProbeMod.Instance.Content.RootDir`
（`ModContentPack.RootDir`），内容一致则跳过不重写，取不到模组目录时静默跳过、不影响采集。
用复制的而非内嵌字符串，是为了让 `docs/AI-ANALYSIS-GUIDE.md` 始终是唯一真实来源——
内嵌一份、文档一份必然分叉，改了文档却忘了改代码里的副本这种事故很难发现。

## 发布后

- `About\PublishedFileId.txt` 由游戏写进**发布目录**，不是开发目录。它的存在意味着后续是「更新」而非「新建」。
- 更新流程：改代码 → 编译 → `publish` → 游戏内重新上传 → `restore`。
- 版本号两处同步改：`About\About.xml` 的 `<modVersion>` 与 `Source\PerfProbe\PerfProbeMod.cs:25` 的 `public const string Version`（后者写进 `index.json`）。
- 工坊 tag 由 `<supportedVersions>` 自动生成，现在写 `1.7` 与 `1.6` 两个。

## 已知遗留

- `About\Preview.png` 暂缺。缺它只 `Log.Warning`、不阻断上传，工坊页显示默认图。
  补图：放 `About\Preview.png`（本机工坊 mod 实测多为 640×360）后重新上传。
- 探针开销约 0.6 ms/tick（占被测时间 12%），已在 `About.xml` 描述里告知玩家。
- 旧的 `dist\` 目录方案已废弃并删除；`stage` / `unstage` 也一并移除——
  它们会把源码搬去 `%TEMP%`，而 `Move-Item` 跨卷时可能只建空壳目录就删掉源。

## 1.1 版本新增：brief.md（面向网页 AI）

同一会话现在产出**三个**文件，保留策略同步改成三件套成组删除：

| 文件 | 体积 | 给谁 |
|---|---|---|
| `.brief.md` | 约 10 KB / 2500 token | 网页版 AI（上传或整份粘贴）、纯对话 AI |
| `.summary.md` | 约 140 KB / 4 万 token（附录占 88%） | 能读本地文件的 agent |
| `.jsonl` | 3 MB 级 | 只在需要逐窗口趋势时读 |

改动点：

- `Output\ProbeSession.cs` 新增 `BriefPath` 字段
- `Output\SummaryWriter.cs` 新增 `WriteBrief` / `BuildBrief` 及 6 个 `AppendBrief*` 分段方法
- `Runtime\ProbeEngine.cs` 在 summary 之后调用 `WriteBrief`，失败不影响 summary
- `Core\Retention.cs` 的 `NameRx` 加上 `brief\.md`，否则 brief 既不被计数也不被删除
- `Output\SchemaWriter.cs` 与 `docs\schema.md` 的文件清单、保留策略同步

配套文档改动：`docs\AI-ANALYSIS-GUIDE.md` 开头新增「按你的 AI 形态选文件」一节，
第 7 节（反编译查根因）标注为「仅 Agent 可用」。

## 1.2 版本删除：Bench（A/B 基准）模式

`ProbeMode.Bench` 及其 A/B 自检已整体移除。原设计想用「前 90 秒只记帧级、之后逐对象计时」
的对比估算探针自身开销，但三条理由都不成立：

1. 探针开销已有两条更准的度量 —— `CalibrateOverhead()` 用真实热路径标定单次 Enter/Exit 成本，
   `Patch_Thing_DoTick` 用 1/64 抽样实测框架成本。这两条被动采集下一直有效。
2. 两段不是同一时段，开局初始化、pawn 生成、寻路冷启动全落在浅段，测出的差异主要是
   游戏本身前后不同。
3. 「浅段不采集明细」从未实现 —— 没有任何 patch 读 `InDeepPhase()`，浅段也在全量计时。

删除清单：

| 文件 | 删除内容 |
|---|---|
| `Core\ProbeMode.cs` | `Bench = 1` 枚举值；`ToTag()` 恒返回 `"passive"` |
| `Output\ProbeSession.cs` | `AbQuietSec` / `AbQuietMs` / `AbQuietTicks` / `AbDeepMs` / `AbDeepTicks`、`InDeepPhase()` |
| `Runtime\ProbeEngine.cs` | `EndTick` 里的 A/B 累加分枝、`ConsumeRequests` 里的 bench 启动分支 |
| `Output\SummaryWriter.cs` | `AppendAb()` 及其调用 |
| `Core\ProbeSettings.cs` | `StartBenchRequested` |
| `UI\ProbeSettingsUI.cs` | 「开始基准测试」按钮；`DoStart(ProbeMode)` 改为无参 `DoStart()` |

**刻意保留**（属于文件格式，删掉会破坏旧数据兼容）：`ProbeMode` 枚举本身、`ToTag()`、
jsonl 的 `mode` 字段、`index.json` 的 `sessions[].mode`、`Retention.NameRx` 里的 `bench` 分支。
旧的 `bench-*` 文件仍能被保留策略正确计数与删除，字段口径与 passive 完全一致。

## 1.3 版本新增：帧率与帧阶段检测

之前的探针只测 tick 耗时，回答不了「为什么帧率掉」。1.7 引擎里这两件事是耦合的：
每帧最多跑 `TickRateMultiplier × 2` 个 tick，累加器余量**每帧清零不跨帧补偿**，
两式相除得**临界帧率恒为 30 FPS，与速度档无关**。帧率 ≥30 时对 TPS 毫无影响；
一旦 <30，掉的每一帧都直接折算成 TPS 损失且不可逆。

### 新增十个帧阶段桶（`Attrib\FrameCost.cs`）

| 桶 | 中文 | 挂载点 |
|---|---|---|
| `f_engine` | 引擎每帧 | `Root.Update` |
| `f_play` | 游戏每帧 | `Game.UpdatePlay` |
| `f_tick` | 全部 tick | `TickManager.TickManagerUpdate` |
| `f_world` | 世界地图 | `World.WorldUpdate` |
| `f_map` | 地图每帧 | `Map.MapUpdate` |
| `f_draw_prep` | 绘制准备 | shader / 水面 / 落叶 |
| `f_draw_mesh` | 地图网格 | `MapDrawer.MapMeshDrawerUpdate_First` + `DrawMapMesh` |
| `f_draw_dyn` | 动态物件 | `DynamicDrawManager.DrawDynamicThings` |
| `f_draw_ovl` | 叠加层 | `DrawAllOverlays` + `TemporaryThingDrawer.Draw` + `FleckManagerDraw` |
| `f_gui` | 界面 | `Root.OnGUI` |

### 三个关键设计

1. **`Runtime\FrameDepth.cs` 与 `DepthTracker` 刻意分开**。DepthTracker 记的是 tick 内部
   几十层嵌套，帧阶段是另一棵树（UpdatePlay → MapUpdate → DrawMesh）。共用一套栈时
   `f_tick` 会把整帧跑的所有 tick 都记成自己的子段、self 变成 0，反过来 tick 内部的
   残留深度也会让绘制段错位。
2. **`Runtime\FrameStats.cs` 与 `CostTable` 刻意分开**。两者分母不同：CostTable 的分母是
   「本窗口所有 tick 的耗时和」，FrameStats 是「本窗口的帧数」。混在一起两边占比全失真。
3. **FPS 的分母用帧长之和，不是 `winActual`**。暂停时帧照跑但不产生 tick、窗口不 flush，
   用墙钟做分母会让 FPS 虚高。

### 最有信息量的一个数：`frameUnaccMs`

= 帧长之和 − 各阶段 selfMs 之和。**引擎渲染、Present、显存换页等待全在这里**，
它是判断「GPU 卡还是 CPU 卡」的唯一依据。summary 与 brief 都单列一行并给三条判读分支。

### 输出改动

- jsonl 新增 8 个字段：`fps` / `frameP50Ms` / `frameP95Ms` / `frames` / `frameAvgMs` /
  `frameUnaccMs` / `ticksPerFrame` / `fstages[]`
- `summary.md` 会话头加 6 行帧率指标，新增整节 `## 帧阶段分解`
- `brief.md` 会话头加 3 行，新增精简版 `## 帧阶段分解`（前 5 段 + 未测部分）
- 设置面板状态行显示实时 FPS，低于 30 时追加提示
- `SchemaWriter.cs` 与 `docs\schema.md` 同步新增字段说明与 `## fstages 元素字段` 整节

### 主线程认主

`PerfProbeBootstrap.Run()` 在挂任何 patch 之前调用 `FrameDepth.MarkMainThread()`。
不能靠「首次调用即主线程」的惰性判定 —— 并行 mod 可能让 worker 线程先走到被 patch 的
绘制方法，主线程 ID 会被记错，之后所有帧阶段数据都被静默丢弃。

## 1.4 版本新增：1.6 基础兼容（双目标框架）

### 为什么能做

逐项核对 1.6 参考程序集（NuGet `Krafs.Rimworld.Ref 1.6.4871` 的 `ref/net472/`）后确认
**全部 API 都存在**：39 个 patch 目标类型、十个帧阶段挂载点、`Root.AverageFrameTime`、
`TickManager.TicksThisFrame` / `MeanTickTime` / `TickRateMultiplier`、`Pawn.DoTick` 全在。

代码侧本来就没有绑死 1.7：零 `Waylith` / `UnityStubs` / `UnityRuntime` 类型引用、
零 C# 8+ 语法、零 .NET Core 专属 API，`using UnityEngine` 只有两个文件用三个成员。

### 怎么做的

`PerfProbe.csproj` 改成 `<TargetFrameworks>net10.0;net472</TargetFrameworks>`，
按条件分组引用与 `OutputPath`，一次 `dotnet build` 出两份 dll。
1.6 参考程序集放在 `Source\refs\1.6\`（三个 dll：`Assembly-CSharp` /
`UnityEngine.CoreModule` / `System.IO.Compression`）。
`LoadFolders.xml` 加 `<v1.6><li>1.6</li></v1.6>`，**不含 `<li>/</li>`**。
`About.xml` 的 `<supportedVersions>` 加 `<li>1.6</li>`。

细节与三个已知坑见 `docs\BUILD.md` 的「双目标框架」节。

### 为什么只承诺「基础兼容」

作者本机只有 1.7，**没有任何办法在 1.6 里实跑验证**。所以：

- 编译能过不等于能跑。`pack.ps1 -Action check` 只能守住「BCL 引用不串版」这条硬错，
  守不住运行期行为差异。
- 两个真实风险面：① Harmony 1.6 是 2.x / 1.7 是 3.x，对注入参数的类型检查严格度不同；
  ② 1.6 是 Unity 渲染、1.7 是 Waylith 引擎，帧阶段那组钩子的行为可能不同。
- 因此 `About.xml` 里明确写「1.6 基础兼容，不保证功能可用」，并请玩家把 1.6 的
  `Player.log` 报错贴到工坊讨论区 —— 这是把「基础兼容」变成「确认可用」的唯一途径。

### 发布包变化

顶层从 5 项变 6 项（多了 `1.6`），体积从 211 KB 涨到 372 KB（多一份 164 KB 的 dll）。
