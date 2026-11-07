# PerfProbe

本 mod 由 DeepSeek V4.1 Flash 辅助制作，在作者本人游戏环境中经过多轮实测。

面向 RimWorld 1.7（Waylith）的**零侵入运行期性能探针**。

它回答两个问题：这个 tick 的耗时花在谁身上，以及帧率为什么上不去。
输出的是**给 AI 读的数据集**——采一轮，把文件丢给任何 AI，让它告诉你要动哪个 mod。

> 不是实时曲线工具。目标是**把运行期开销落成机器可读的日志**，
> 让 AI 能直接回答「哪栋建筑 / 哪类物品 / 哪个人物 / 哪个事件 / 哪个模组在吃性能」。

仓库：<https://github.com/hnhyhjj-hub/PerfProbe>

---

## 反馈问题

**优先走 [Issues](https://github.com/hnhyhjj-hub/PerfProbe/issues/new/choose)。**

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

## 版本支持

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

## 文档

| 文件 | 内容 |
|---|---|
| [`PLAN.md`](PLAN.md) | 实施方案与关键设计决策（计时边界、速度档隔离、抽样、归因回退） |
| [`docs/schema.md`](docs/schema.md) | 产物字段口径 |
| [`docs/AI-ANALYSIS-GUIDE.md`](docs/AI-ANALYSIS-GUIDE.md) | 判读指南：怎么读、哪些数不能相加、常见误判 |
| [`docs/MECH-PERF-REPORT.md`](docs/MECH-PERF-REPORT.md) | 实测性能报告样例 |

---

## 许可

见 [`LICENSE`](LICENSE)。
