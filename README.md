# PerfProbe

面向 RimWorld 1.7（Waylith）的**零侵入运行期性能探针**。

它回答两个问题：这个 tick 的耗时花在谁身上，以及帧率为什么上不去。
输出的是**给 AI 读的数据集**——采一轮，把文件丢给任何 AI，让它告诉你要动哪个 mod。

> 不是实时曲线工具。目标是**把运行期开销落成机器可读的日志**，
> 让 AI 能直接回答「哪栋建筑 / 哪类物品 / 哪个人物 / 哪个事件 / 哪个模组在吃性能」。

---

## 反馈问题

**优先走 [Issues](../../issues/new/choose)。**

出问题时先在设置面板点「导出反馈包」，会生成
`PerfProbe-feedback-<日期>-<时间>.zip`，把它拖进 Issue 即可。包里已经收齐排查需要的全部信息：

| 文件 | 作用 |
|---|---|
| `brief.md` | 约 10 KB，给 AI 的精简版 |
| `summary.md` | 约 40-170 KB，完整报告 |
| `index.json` | 所有会话的汇总索引 |
| `schema.md` | 字段口径 |
| `AI-ANALYSIS-GUIDE.md` | 判读指南（哪些数不能相加、常见误判） |
| `probe.log` | 探针自己的日志：挂了多少 patch、有没有失败 |
| `Player.log` | 已裁剪，只留加载期与前 400 行及所有报错行（4.7 MB → 约 400 KB） |
| `ModsConfig.xml` | 完整 mod 列表与加载顺序 |

**包里不含硬件信息**，路径里的用户名在导出时已替换成 `<用户名>`。

> 提交前建议先点「停止并输出汇总」再点导出，否则包里只有 jsonl 没有 summary。
> jsonl（1-3 MB）不必上传，除非问题明确只出现在逐窗口趋势上。

---

## 安装

从 [Releases](../../releases/latest) 下载 `PerfProbe-<版本>.zip`，解压到 `RimWorld/Mods/`，
确保目录结构是 `Mods/PerfProbe/About/About.xml`。

依赖 [Harmony](https://github.com/pardeike/HarmonyRimWorld)（工坊 packageId `brrainz.harmony`）。

> **直接 clone 本仓库的话**，元数据目录叫 `About.off` 而不是 `About` ——
> 这是发布流程的需要（见[发布](#发布)：开发目录与发布目录 packageId 相同会互相冲突，
> 开发侧必须让 `About` 缺位）。想直接放进 `Mods/` 玩，把 `About.off` 改名成 `About` 即可。

### 版本支持

| 版本 | 状态 |
|---|---|
| 1.7（Waylith） | **完整支持**，全部功能实测过，含帧率与帧阶段检测 |
| 1.6 | **基础兼容，不保证功能可用** |

1.6 那份是对着 .NET Framework 4.7.2 与 1.6 参考程序集**单独编译**的，不是同一份二进制。
所有 patch 目标与方法签名都已逐个核对、编译零错误，但作者只有 1.7，没在 1.6 里实跑过。
已知风险面：Harmony 版本差异（1.6 是 2.x，1.7 是 3.x）与渲染管线差异（1.6 是 Unity，1.7 是 Waylith）。
若 1.6 下报错，请停用本 mod 并把日志贴到 Issues——每一份 1.6 日志都能把「基础兼容」推向「确认可用」。

---

## 三步开始

1. 选项 → 模组设置 → PerfProbe，勾选「启用采集」。采样率 K 保持 1。
2. 点「开始被动采集」，切回游戏正常游玩（建议极快 / 6 倍速）。
3. 玩 3-10 分钟后点「停止并输出汇总」，去数据目录取文件。

首次建议先只开本 mod 跑一轮摸清原版基线，再开其他 mod 对比。

数据目录（与 `Saves` 平级，**不在** `Saves` 里面）：

| 平台 | 路径 |
|---|---|
| Windows | `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\PerfProbe\` |
| macOS | `~/Library/Application Support/Ludeon Studios/RimWorld/PerfProbe/` |
| Linux | `~/.local/share/Ludeon Studios/RimWorld by Ludeon Studios/PerfProbe/` |

启动参数 `-savedatafolder X` 会整体改变这个根目录。

---

## 它能告诉你什么

- **五类归因**：建筑 / 物品 / 人物 / 地图组件 / 全局系统（另有 other 兜底），再按模组与具体 def 细分。
- **人物层三层不混算**：`Pawn.Tick` 主体 / `Pawn.TickInterval` / 外层其它；主体内再分工作、寻路、状态、能力、装备、基因、渲染、思考、稀有等十一个方面。
- **全局系统拆 17 项**，其中 TickList 容器开销与 MapComponent 分开统计。
- **帧率与帧阶段**：TPS 低有两种完全不同的原因——每 tick 太慢，或帧率太低导致每帧能跑的 tick 被砍。只看 `msPerTick` 会把后者误判成前者。本 mod 同时把每帧拆成十个阶段，剩下的「未测部分」就是引擎渲染、Present、显存换页等待，是判断「GPU 卡还是 CPU 卡」的唯一依据。
  - 判读规则：帧率 ≥30 时对 TPS 无影响，可专心优化 `msPerTick`；低于 30 说明每帧 tick 数已被上限夹住，TPS 正随帧率一起下降，且不可补偿。

**它不做**：只读观测。不改游戏行为、不写存档、不改任何 def、不替换贴图。卸掉后存档完全不受影响。

**开销**：探针本身约 0.6 ms/tick，约占被测时间 12%。它是一次性诊断工具，不是常驻优化 mod——定位到瓶颈后请关掉或停用。报告里会单独列出探针自身占比，方便扣除。

---

## 从源码构建

见 [`docs/BUILD.md`](docs/BUILD.md)。要点：

- 需要 **dotnet 10**（1.7 目标 `net10.0`）与 **.NET Framework 4.7.2 参考程序集**（1.6 目标 `net472`）。
- `Source/PerfProbe/PerfProbe.csproj` 的路径属性可被环境变量或命令行覆盖：
  `RimWorldDir` / `WorkshopRoot` / `Harmony17Dir` / `Harmony16Dir` / `Ref16Dir`。换机器通常只改 `RimWorldDir`，
  其余四项从它派生（工坊根是 `steamapps\workshop\content\294100`，**不在** `common` 下面）。

```powershell
cd Source/PerfProbe
dotnet build -c Release -v q --nologo
```

一次 build 出两份：`net10.0` → `1.7/Assemblies/PerfProbe.dll`，`net472` → `1.6/Assemblies/PerfProbe.dll`。

> 若日志写着 `0 个警告 0 个错误` 但 `PerfProbe.dll` 没更新，是编译服务器进程被环境限制杀掉了
> （日志里会有 `MSB5021: 终止任务可执行文件"csc"`）。补上
> `--disable-build-servers -m:1 -p:UseSharedCompilation=false -nodeReuse:false` 即可，
> 判断成败看产物时间戳，不看日志里的错误计数。

> `Source/refs/`（1.6 参考程序集）**不随仓库分发**，需自备；只编 1.7 时可跳过 `net472` 目标。

---

## 发布

工坊上传读的是 **mod 根目录本身**（`Verse.Steam.Workshop.BakeAndUpload` → `SetItemContent(hook.Directory.FullName)`），
Steam 端整目录打包，**没有白名单，`.gitignore` 无效**。所以开发目录与发布目录必须分离：

```powershell
.\tools\pack.ps1 pack      # 重建 Mods\PerfProbe-Publish\ + 8 项自检
.\tools\pack.ps1 publish   # 再把开发目录 About 改名 About.off，让游戏只认发布目录
.\tools\pack.ps1 restore   # 上传完恢复
```

详见 [`docs/PUBLISH.md`](docs/PUBLISH.md)。

---

## 文档

| 文件 | 内容 |
|---|---|
| [`PLAN.md`](PLAN.md) | 实施方案与关键设计决策（计时边界、速度档隔离、抽样、归因回退） |
| [`docs/BUILD.md`](docs/BUILD.md) | 编译环境、引用程序集、1.6/1.7 差异与踩过的坑 |
| [`docs/PUBLISH.md`](docs/PUBLISH.md) | 工坊发布流程、目录分离、packageId 冲突处理 |
| [`docs/schema.md`](docs/schema.md) | 产物字段口径 |
| [`docs/AI-ANALYSIS-GUIDE.md`](docs/AI-ANALYSIS-GUIDE.md) | 判读指南：怎么读、哪些数不能相加、常见误判 |
| [`docs/MECH-PERF-REPORT.md`](docs/MECH-PERF-REPORT.md) | 实测性能报告样例 |

---

## 许可

见 [`LICENSE`](LICENSE)。
