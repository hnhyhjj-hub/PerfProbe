# PerfProbe 机械体性能分析报告

> 数据来源：`passive-20261030-2104-s01.jsonl`（80 个采样窗口 / 470.7 s / 54322 tick / dropLines=0）
> 会话配置：GPP 并行全关、Slower Pawn Tick Rate 20x、SampleK=1 全量、superfast
> 探针开销已单列，本报告所有 ms/tick 均为扣除探针后的游戏真实成本

## 一、口径说明（读前必看）

本报告有两套口径，**不能混算**：

| 口径 | 含义 | 用途 |
|---|---|---|
| `pawninst` 口径 | 单个 pawn 的 `Thing.DoTick` 总时间（= selfMs + 外壳层），单位为 ms | 横向比较"哪只贵" |
| 真实口径 | `pawninst` 合计 × **1.469** = `pawn_body + pawn_interval + pawn_other` 三项和 | 回答"总共吃掉多少 tick 时间" |

**换算系数 1.469 的来源**：`pawninst` 合计 103316.4 ms，外壳三项和 151751.8 ms，比值 151751.8 / 103316.4 = 1.469。
差额 48435.4 ms（31.9%）来自嵌套 pawn（被搬运/装在容器里的 pawn 走父级 DoTick，不单独记 pawninst）。

**每 tick 成本换算**：`μs/tick/只` = 该实例在该窗口的 `perInstMs` ÷ 该窗口 tick 数，逐窗口累加后取均值。

## 二、机械体总量

| 指标 | 数值 |
|---|---|
| 机械体条目（不同 defName） | **13** |
| 平均同时在场 | **26.6 只** |
| 累计耗时 | 29512 ms / 470.7 s |
| `pawninst` 口径 | **0.5433 ms/tick** |
| 真实口径 | **0.798 ms/tick** |
| 占 pawn 层（2.7936 ms/tick） | **28.6%** |
| 占全部已归因（4.9632 ms/tick） | **16.1%** |
| 平均单只成本 | **12.6 μs/tick** |

### 对照：机械体 vs 其它生物

| 类别 | 平均在场 | 真实 ms/tick | 占比 | 单只 μs/tick |
|---|---|---|---|---|
| 人形（殖民者+访客+囚犯） | 22.4 只 | 1.1737（pawninst 口径） | 60.0% | 31.8 |
| 动物 | 28.1 只 | 0.2170（pawninst 口径） | 11.4% | 4.8 |
| **机械体** | **26.6 只** | **0.5433（pawninst 口径）** | **26.9%** | **12.0** |

## 三、逐条明细（按真实 ms/tick 降序）

| defName | mod | 只数 | 平均在场 | μs/tick/只 | pawninst ms/tick | **真实 ms/tick** |
|---|---|---|---|---|---|---|
| `叶小羊[MUM_UniqueRobotKind]` | 【PLUS】Prototype Mecha "Yexiaoyang"仙狐机兵：叶小羊 | 1 | 1.0 | 66.5 | 0.1085 | **0.1593** |
| `Milian_Mechanoid_BishopIII` | Milira Race | 2 | 2.0 | 22.6 | 0.0736 | **0.1081** |
| `MAP_Mech_Moon` | [MAP]机械族机械师 Mechanoid Mechanitor (1.6/1.7) | 7 | 7.0 | 6.1 | 0.0695 | **0.1021** |
| `MAP_Mech_Lover` | [MAP]机械族机械师 Mechanoid Mechanitor (1.6/1.7) | 3 | 2.5 | 13.9 | 0.0530 | **0.0779** |
| `Milian_Mechanoid_PawnIV` | Milira Race | 1 | 0.9 | 33.4 | 0.0530 | **0.0779** |
| `Wolfein_Mechanoid_MilitaryGiant` | Wolfein Race | 4 | 4.0 | 5.5 | 0.0357 | **0.0524** |
| `Mech_CentipedeGunner` | Core | 4 | 3.9 | 5.5 | 0.0351 | **0.0516** |
| `Wolfein_Mechanoid_MilitaryD` | Wolfein Race | 4 | 3.9 | 5.4 | 0.0341 | **0.0502** |
| `Raven_Mech_Aegis` | Raven Race - Embers in Shadow | 1 | 0.2 | 98.3 | 0.0321 | **0.0472** |
| `Milian_Mechanoid_KnightIII` | Milira Race | 1 | 0.5 | 32.2 | 0.0221 | **0.0325** |
| `MAP_Mech_Justice` | [MAP]机械族机械师 Mechanoid Mechanitor (1.6/1.7) | 1 | 0.3 | 24.2 | 0.0135 | **0.0198** |
| `Milian_Mechanoid_RookII` | Milira Race | 1 | 0.2 | 31.8 | 0.0097 | **0.0142** |
| `MAP_Mech_Hermit` | [MAP]机械族机械师 Mechanoid Mechanitor (1.6/1.7) | 1 | 0.1 | 11.8 | 0.0033 | **0.0048** |
| **合计** | | **26** | **26.6** | **12.6** | **0.5433** | **0.7980** |

### 分组：贵 vs 便宜

| 组 | 条目 | 只数 | 平均 μs/只 | 真实 ms/tick |
|---|---|---|---|---|
| 贵（≥10 μs/只） | 9 | 12 | 32.1 | 0.542 |
| 便宜（<10 μs/只） | 4 | 19 | 5.7 | 0.256 |

**关键洞察：贵的 12 只占 68% 成本，便宜的 19 只只占 32%。** 要砍就砍贵的。

### 按 mod 汇总

| mod | 条目 | 只数 | pawninst ms/tick | 真实 ms/tick |
|---|---|---|---|---|
| Milira Race | 4 | 5 | 0.1584 | 0.2327 |
| [MAP]机械族机械师 Mechanoid Mechanitor (1.6/1.7) | 4 | 12 | 0.1393 | 0.2047 |
| 【PLUS】Prototype Mecha "Yexiaoyang"仙狐机兵：叶小羊 | 1 | 1 | 0.1085 | 0.1593 |
| Wolfein Race | 2 | 8 | 0.0698 | 0.1025 |
| Core | 1 | 4 | 0.0351 | 0.0516 |
| Raven Race - Embers in Shadow | 1 | 1 | 0.0321 | 0.0472 |

## 四、机械体的成本构成（关键：哪些能省，哪些不能）

把 pawn 层的各桶按机械体占比（28.6%）分摊：

| pawn 层桶 | 全局 ms/tick | 机械体分摊 ms/tick | 停机/休眠能否省 | 原因 |
|---|---|---|---|---|
| `pawn_work`（job tickAction） | 0.5877 | 0.1679 | ✅ 能 | `JobDriver.DriverTick()` 里 `if (CurToil.tickAction != null)` 才跑，SelfShutdown 的 toil 没有 tickAction |
| `pawn_path`（寻路） | 0.3197 | 0.0913 | ✅ 能 | 停机后不移动，`pather.PatherTick()` 内部因无路径而早退 |
| `pawn_state`（Hediff/Verb/Stance） | 0.2631 | 0.0752 | ❌ 不能 | `health.HealthTick()` 无条件遍历所有 Hediff；`VerbTracker.VerbsTick()` 无条件 |
| `pawn_interval`（已降频） | 0.5500 | 0.1571 | ❌ 已榨干 | Slower Pawn Tick Rate 已把 `MaxTickIntervalRate` 顶到 15 上限 |
| `pawn_other`（DoTick 容器） | 0.6634 | 0.1895 | ❌ 不能 | 只要 `Spawned == true`，`Thing.DoTick` 就必然调用 `Tick()` |
| `base.Tick()` → `comps[i].CompTick()` | 0.1937（body 残差） | 0.0553 | ❌ 不能 | `ThingWithComps.Tick()` 无条件遍历 comps；PerfProbe 目前未打点，藏在 body 残差里 |
| 其它叶子（ability/gear/misc/gene/render/rare/think） | 0.2161 | 0.0617 | 部分 | — |
| **合计** | **2.7936** | **0.798** | | |

### 结论：机械体的 0.798 ms/tick 里，只有约 0.15 是可省的

- **可省**：`pawn_work` 的 60% + `pawn_path` 的 50% ≈ **0.146 ms/tick**
- **不可省**：`pawn_other` 0.1895 + `pawn_state` 0.0752 + `comps` 0.0553 + `pawn_interval` 0.1571 ≈ **0.477 ms/tick**
- 剩下 0.175 ms/tick 是部分可省的其它叶子

## 五、为什么"休眠"在原版无效（源码级证据）

### 5.1 `Thing.DoTick()` 无条件调用 `Tick()`

```csharp
// Verse/Thing.cs:700
public void DoTick()
{
    if (Destroyed) return;
    ...
    if (def.tickerType == TickerType.Normal)
    {
        Tick();                                    // ← 无条件，没有任何开关能跳过
        if (Destroyed) return;
        tickDelta++;
        int num = Mathf.Min(Mathf.Max(UpdateRateTicks, MinTickIntervalRate), MaxTickIntervalRate);
        if (tickDelta >= num || GenTicks.IsTickInterval(UpdateRateTickOffset, num))
            TickInterval(tickDelta);               // ← 只有这里能被降频 mod 干预
    }
    ...
}
```

**`MaxTickIntervalRate` 默认 15**（`Thing.cs:606`），`UpdateRateTicks => GenTicks.GetCameraUpdateRate(this)`（`Thing.cs:610`）在视野外也返回 15 → `Mathf.Min(..., 15)` 夹死上限。
这就是为什么 Slower Pawn Tick Rate 设 20x 只能拿到约一半收益（`pawn_interval` 实测 -46.5%）。

### 5.2 `Thing.Suspended` 对 spawned pawn 恒为 false

```csharp
// Verse/Thing.cs:552
public virtual bool Suspended
{
    get
    {
        if (Spawned) return false;                       // ← spawned 直接 false
        if (ParentHolder != null) return ThingOwnerUtility.ContentsSuspended(ParentHolder);
        return false;
    }
}
```

`Pawn.Suspended`（`Pawn.cs:1112`）override 后只多加一个 `WorldPawnSituation.ReservedByQuest` 判断，**仍然要求先 `!Spawned`**。

### 5.3 `CompCanBeDormant.Awake` 恒 false

```csharp
// RimWorld/CompCanBeDormant.cs
public virtual bool Awake
{
    get
    {
        if (wokeUpTick == int.MinValue || wokeUpTick > Find.TickManager.TicksGame)
        {
            if (parent.Spawned && Props.jobDormancy && parent is Pawn pawn)
                return pawn.CurJobDef != SleepJob;           // ← jobDormancy 默认 false
            return false;
        }
        return true;
    }
}
```

`CompProperties_CanBeDormant.jobDormancy` **默认 false**。米莉拉机械体基类 `Mods\3256974620\1.6\Defs\ThingDefs_Races\Race_MilianMechanoid_Base.xml:44` 只写 `<li Class="CompProperties_CanBeDormant" />`，未开 `jobDormancy`。

### 5.4 `Pawn.Tick()` 一个休眠标志都不读

全 `Pawn.cs` 搜 `IsDeactivated` / `IsSelfShutdown` / `canBeDormant`，只出现在：
- `Pawn.cs:3213-3221`（InspectString 电量显示）
- `Pawn.cs:3907`（`HearClamor` 早退）
- `Pawn.cs:4141`（CompActivity）

**`Pawn.Tick()` 里零引用。**

## 六、WVC - Work Modes 的 SelfShutdown 为什么只省一点

### 6.1 停机链路

模组路径：`steamapps\workshop\content\294100\2888380373`（WVC - Work Modes，packageId `wvc.sergkart.biotech.MoreMechanoidsWorkModes`，modVersion 1.1.8）

`1.7\Patches\WorkModes\WVC_WaitEnemy.xml` 与 `WVC_WorkAndRecharge.xml` 里：

```xml
<li Class="WVC_WorkModes.JobGiver_GoToShutdownZone">
  <workModeType>Combat</workModeType>
  <possibleRooms><li>WVC_MechHangar</li></possibleRooms>
</li>
<li Class="WVC_WorkModes.JobGiver_GetEnergy_SelfShutdown"><forced>true</forced></li>
```

链路：`JobGiver_GetEnergy_SelfShutdown.TryGiveJob` → `JobDefOf.SelfShutdown` → `JobDriver_SelfShutdown` → `Toils_LayDown.SelfShutdown()`

```csharp
// Toils_LayDown.cs:273
Toil layDown = ToilMaker.MakeToil("SelfShutdown");
layDown.initAction = delegate { actor.pather?.StopDead(); ... curDriver.asleep = true; };
layDown.defaultCompleteMode = ToilCompleteMode.Never;
// 注意：没有 tickAction
```

### 6.2 为什么只省 tickAction

```csharp
// JobDriver.cs:342
public void DriverTick()
{
    try {
        ticksLeftThisToil--;  debugTicksSpentThisToil++;        // ← 照跑
        ... CheckCurrentToilEndOrFail(); ...                     // ← 照跑
        if (CurToil.preTickActions != null) { ... }
        if (CurToil.tickAction != null) { CurToil.tickAction(); ... }   // ← SelfShutdown 无 tickAction，跳过
        if (job.mote != null) job.mote.Maintain();
    } catch (Exception exception) { JobUtility.TryStartErrorRecoverJob(...); }
}
```

**`DriverTick()` 完全不读 `asleep`**，`ticksLeftThisToil--` 与结束判定照跑。省掉的只有 `tickAction()` 那一次调用。

### 6.3 `JobGiver_GetEnergy_SelfShutdown` 的实现

```csharp
// RimWorld/JobGiver_GetEnergy_SelfShutdown.cs
public class JobGiver_GetEnergy_SelfShutdown : JobGiver_GetEnergy
{
    protected override Job TryGiveJob(Pawn pawn)
    {
        if (!ShouldAutoRecharge(pawn)) return null;                    // ← 电量够就返回 null
        if (RCellFinder.TryFindRandomMechSelfShutdownSpot(pawn.Position, pawn, pawn.Map, out var result))
        {
            Job job = JobMaker.MakeJob(JobDefOf.SelfShutdown, result);
            job.checkOverrideOnExpire = true;
            job.expiryInterval = 500;
            return job;
        }
        return null;
    }
}
```

WVC 的 XML 里设了 `<forced>true</forced>`，所以 `ShouldAutoRecharge` 会走 `if (forced) return true;` 分支，确实绕过了电量 15% 门槛。

**但 `JobGiver_GoToShutdownZone` 只是让机械体走到 `WVC_MechHangar` 房间待机，待机本身是个 `JobDefOf.Wait` job，照样每 tick 跑 `DriverTick()`。**

### 6.4 实测验证：`pawn_work` 每 pawn 完全没变

| 会话 | `pawn_work` ms/tick | pawn 数 | 每 pawn |
|---|---|---|---|
| 旧 `passive-20261030-1926-s01` | 0.5549 | 125 | **4.44 μs** |
| 新 `passive-20261030-2104-s01` | 0.5876 | 132 | **4.45 μs** |

两个会话精确到小数点后两位一致 → **shutdown 没有真正生效，或者生效了但对 `pawn_work` 没有可测影响**。

## 七、可行的优化路径（按收益排序）

### 方案 ①：让 WVC shutdown 真正生效 — 预期 -0.146 ms/tick

**前提**：确认机械体确实进入 `JobDefOf.SelfShutdown`（而不是 `Wait`）。
检查方法：读存档里机械体的 `CurJobDef`，或用 DevMode 看 `Pawn.jobs.curJob`。

**预期收益**：
| 项 | 可省 | 真实 ms/tick |
|---|---|---|
| `pawn_work`（省 60%） | 0.1679 × 0.6 | 0.1007 |
| `pawn_path`（省 50%） | 0.0913 × 0.5 | 0.0457 |
| **合计** | | **-0.146** |

### 方案 ②：Harmony 硬跳 `Pawn.Tick` 主体 — 预期 -0.391 ms/tick

```csharp
[HarmonyPatch(typeof(Pawn), nameof(Pawn.Tick))]
static class Patch_Pawn_TickSkip
{
    static bool Prefix(Pawn __instance)
    {
        if (!ShouldSkip(__instance)) return true;

        // 必须保留：否则电量 comp 不跑、机械体永远充不上电
        if (__instance.comps != null)
            for (int i = 0; i < __instance.comps.Count; i++)
                __instance.comps[i].CompTick();

        return false;   // 跳过整个 Pawn.Tick
    }
}
```

**`ShouldSkip` 的推荐判据**：
- 是机械体（`pawn.RaceProps.IsMechanoid`）
- 当前 `CurJobDef == JobDefOf.SelfShutdown`（或 WVC 的自定义停机 job）
- `pawn.needs.energy?.CurLevel > 阈值`
- 无敌对目标（`pawn.mindState.enemyTarget == null`）
- 不在征召状态（`!pawn.Drafted`）

**风险清单**：
| 风险 | 后果 | 缓解 |
|---|---|---|
| 跳过 `health.HealthTick()` | 中毒/流血/感染不结算 | 单独保留 `health.HealthTick()` |
| 跳过 `comps[i].CompTick()` | 电量 comp 不跑、充不上电 | 手动补跑 comps |
| 跳过 `IsHashIntervalTick(250) → TickRare()` | 长周期逻辑停摆 | 单独保留 |
| 与其它改 `Pawn.Tick` 的 mod 冲突 | patch 顺序不确定 | 用 `[HarmonyPriority]` 或 `[HarmonyBefore/After]` |

**省下的构成**：`pawn_work` 0.1679 + `pawn_path` 0.0913 + `pawn_state` 0.0752 + `comps` 0.0553 = **0.3897 ms/tick**

### 方案 ③：把机械体移出地图（DeSpawn 到 world pawn）— 预期 -0.798 ms/tick

```csharp
Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);
```

**效果**：`Spawned == false` → `Pawn.Suspended` 返回 true → `Pawn.Tick()` 里 `if (!suspended)` 的两个大块全部跳过；同时不再进 `TickList`，`Thing.DoTick` 都不跑了。

**但**：机械体不在图上，无法执行任何工作/战斗。这是"存档保管"而非"待命"。

## 八、给模组开发者的建议

如果目标是"让 40+ 机械体基本不消耗性能，但能随时唤醒战斗"，**原版机制做不到**，需要 mod 层干预。三个可行方向：

### 方向 A：改 `Thing.DoTick` 的 tickerType（最小侵入，但有坑）

把待命机械体的 `def.tickerType` 从 `Normal` 临时改成 `Rare`（250 tick 一次）或 `Long`（2000 tick 一次）：

```csharp
// 进入待命时
pawn.def.tickerType = TickerType.Rare;
// 唤醒时改回
pawn.def.tickerType = TickerType.Normal;
```

**致命问题**：`def` 是共享的 `ThingDef`，改它会影响**所有**同类机械体。同一个 `Milian_Mechanoid_KnightIII` def 下有 3 只，改一只全改。

**补救方案**：给每只机械体单独 clone 一个 `ThingDef`（`def.Copy()`），代价是 def 索引会膨胀，且 `defName` 重复可能引发其它 mod 的查找问题。

### 方向 B：Harmony Prefix 跳过 `Pawn.Tick`（推荐）

即第七章方案 ②。这是唯一能做到"spawned 但不算"的方式，代价是要小心处理 comps/health。

### 方向 C：让机械体进容器（利用原版 `ISuspendableThingHolder`）—— 最干净

```csharp
// Verse/ThingOwnerUtility.cs:166
public static bool ContentsSuspended(IThingHolder holder)
{
    while (holder != null) {
        if (holder is Building_CryptosleepCasket || holder is ISuspendableThingHolder { IsContentsSuspended: not false })
            return true;
        holder = holder.ParentHolder;
    }
    return false;
}
```

**这是原版自带的"零成本收纳"机制**：只要容器实现 `ISuspendableThingHolder` 且 `IsContentsSuspended == true`，里面的 pawn 的 `Suspended` 就返回 true，`Pawn.Tick()` 的 `if (!suspended)` 块全部跳过。

`Building_Enterable`（`Building_Enterable.cs:9`）已经同时实现 `IThingHolder, ISuspendableThingHolder, ISearchableContents`，且 `Building_Enterable.cs:19` 有：

```csharp
public virtual bool IsContentsSuspended => true;
```

**所以任何继承 `Building_Enterable` 的建筑，装进去的 pawn 天然零 tick 成本。**

**完整链路验证**：
1. `Building_Enterable.SelectPawn`（`Building_Enterable.cs:80`）→ `pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.EnterBuilding, this), JobTag.Misc)`
2. `EnterBuilding` job → `TryAcceptPawn` → `pawn.DeSpawnOrDeselect()` + `innerContainer.TryAddOrTransfer(pawn)`
3. pawn 的 `ParentHolder` 变成建筑 → `Thing.Suspended` 里 `if (Spawned) return false;` 先拦住……

**等等，这里有个关键点**：`DeSpawnOrDeselect()` 会把 pawn 从地图移除，`Spawned` 变 false，然后 `ParentHolder != null` → `ThingOwnerUtility.ContentsSuspended(ParentHolder)` → 返回 true。

所以 `Suspended` 走的是 `!Spawned` + `ContentsSuspended` 这条路，**pawn 完全不进 `TickList`**（`TickList.RegisterThing` 只在 `SpawnSetup` 里调），`Thing.DoTick` 根本不跑。

**这是收益最大的方案：-0.798 ms/tick，即机械体成本归零。**

**推荐做法**：做一个"机械体停机坪"建筑，继承 `Building_Enterable`，机械体走进去 = 零 tick 成本，一键弹出 = 立即战斗。这正是 `[YH]Misc - Time Freezing Spot` 用的方案（见附录）。

### 三方案对比

| 方案 | 收益 | 侵入性 | 副作用 | 适用场景 |
|---|---|---|---|---|
| ① 修 WVC shutdown | -0.146 | 无（纯配置） | 无 | 想先试低成本方案 |
| ② Harmony 跳 Pawn.Tick | -0.391 | 高（改核心方法） | comps/health 需手动补 | 想保留 spawned 状态 |
| ③ 装进 `Building_Enterable` 容器 | **-0.798** | 低（新增建筑，不改原版） | 无（原版机制） | **推荐** |

## 九、`[YH]Misc - Time Freezing Spot` 的可行性评估

模组路径：`steamapps\workshop\content\294100\3356597921`

### 9.1 它做了什么

```csharp
// Source/Building_TimeFreezingSpot.cs:12
[StaticConstructorOnStartup]
public class Building_TimeFreezingSpot : Building_Enterable, IThingHolderWithDrawnPawn, IThingHolder
{
    // ...
    public override void TryAcceptPawn(Pawn pawn)   // :143
    {
        if ((bool)CanAcceptPawn(pawn))
        {
            selectedPawn = pawn;
            bool flag = pawn.DeSpawnOrDeselect();          // ← 从地图移除
            if (innerContainer.TryAddOrTransfer(pawn))     // ← 装进建筑
                startTick = Find.TickManager.TicksGame;
            if (flag) Find.Selector.Select(pawn, playSound: false, forceDesignatorDeselect: false);
        }
    }
}
```

**它继承 `Building_Enterable`，所以白拿了 `IsContentsSuspended => true`。** 站上去的 pawn 被 `DeSpawnOrDeselect()` + `TryAddOrTransfer()` 装进 `innerContainer`，`Spawned == false`，**完全脱离 TickList**。

配套的 `HediffComp_TimeFreezingSpot` 提供"快捷进入"按钮（`bindSpot.QuickEnter()`），绑定后一键进入。

### 9.2 成本为什么真的归零（两道闸门，缺一不可）

#### 第一道闸门（决定性）：`DeSpawn()` 把 pawn 从 TickList 注销

```csharp
// Verse/Thing.cs:997（DeSpawn 内部）
Find.TickManager.DeRegisterAllTickabilityFor(this);
mapIndexOrState = -1;
```

```csharp
// Verse/TickManager.cs:301
public void DeRegisterAllTickabilityFor(Thing t)
{
    TickListFor(t)?.DeregisterThing(t);
}
```

**pawn 被 `DeSpawnOrDeselect()` 之后就不在 `thingLists[]` 里了**，`TickList.Tick()` 遍历不到它，`Thing.DoTick()` 根本不会被调用。

对应地，`SpawnSetup` 里（`Thing.cs:846`）：
```csharp
if (Find.TickManager != null)
{
    Find.TickManager.RegisterAllTickabilityFor(this);
}
```

#### 第二道闸门（防御性）：`ContentsSuspended` 让容器的 DoTick 不再递归进内容物

即使容器建筑自己还在 TickList 里（`Building_Enterable` 实现 `IThingHolder`，`TickListFor` 会把它归到 `tickListNormal`），它往下递归时会被拦住：

```csharp
// Verse/Thing.cs:757（DoTick 尾部）
if (Destroyed || !cachedIsHolder || (cachedTickable != null && !cachedTickable.ShouldTickContents) || ThingOwnerUtility.ContentsSuspended(cachedHolder))
{
    return;                                    // ← ContentsSuspended 为 true 直接返回
}
// 只有过了这道门才会 tmpHolders.Add(cachedHolder) 然后 GetDirectlyHeldThings().DoTick()
```

```csharp
// Verse/ThingOwnerUtility.cs:166
public static bool ContentsSuspended(IThingHolder holder)
{
    while (holder != null)
    {
        if (holder is Building_CryptosleepCasket || holder is ISuspendableThingHolder { IsContentsSuspended: not false })
            return true;
        holder = holder.ParentHolder;
    }
    return false;
}
```

`Building_Enterable` 实现了 `ISuspendableThingHolder` 且 `IsContentsSuspended => true`，**所以容器里的 pawn 的 `DoTick()` 永远不被调用。**

> 注：`ThingOwner.DoTick()`（`ThingOwner.cs:106`）本身只有 `dontTickContents` 一个开关（默认 false），所以**第一道闸门才是真正的保证**；第二道是防御性的双保险，防止容器被别的路径递归 tick 到。

#### 第三重：`Suspended` 也返回 true

```csharp
// Verse/Thing.cs:552
public virtual bool Suspended
{
    get
    {
        if (Spawned) return false;
        if (ParentHolder != null) return ThingOwnerUtility.ContentsSuspended(ParentHolder);   // ← 这里命中
        return false;
    }
}
```

pawn 被装进容器后 `Spawned == false` + `ParentHolder != null` + `ContentsSuspended == true` → `Suspended == true`。

即使某条路径意外让 `Pawn.Tick()` 跑起来，`if (!suspended)` 的两个大块也全跳过。

**三重保险，成本确实归零。**

### 9.3 但它是 1.6 模组，能在 1.7 跑吗

| 检查项 | 结果 |
|---|---|
| `About.xml` supportedVersions | 只到 **1.6**，没有 1.7 |
| 目录结构 | `1.4/` `1.5/` `1.6/`，**没有 `1.7/`** |
| `LoadFolders.xml` | **不存在** |
| dll 目标框架 | **.NETFramework 4.8**（1.7 需要 .NETCoreApp 10.0） |
| 1.6 目录内容 | 只有 `Assemblies/TimeFreezingSpot.dll`，Defs 在根目录 |
| 1.7 API 兼容性 | `IThingHolderWithDrawnPawn` / `ISuspendableThingHolder` / `ISearchableContents` 在 1.7 全都在 |
| 是否已启用 | **否**（`ModsConfig.xml` 里搜不到） |

**目录加载会怎样**：`ModContentPack.InitLoadFolders()`（`ModContentPack.cs:257`）在没有 `LoadFolders.xml` 时会走兜底：

```csharp
string text2 = Path.Combine(RootDir, VersionControl.CurrentVersionStringWithoutBuild);  // "1.7"
if (Directory.Exists(text2)) foldersToLoadDescendingOrder.Add(text2);
else {
    // 找版本号目录里 <= CurrentVersion 的最大者 → 会选中 "1.6"
    ...
}
string text3 = Path.Combine(RootDir, CommonFolderName);   // "Common"
if (Directory.Exists(text3)) foldersToLoadDescendingOrder.Add(text3);
foldersToLoadDescendingOrder.Add(RootDir);                 // 根目录永远加载
```

**所以 1.7 下会加载 `1.6/Assemblies/TimeFreezingSpot.dll` + 根目录的 `Defs/`** —— 结构上能跑起来。

**但 dll 是 .NETFramework 4.8 编译的，而 1.7 的 `Assembly-CSharp.dll` 是 .NETCoreApp 10.0。** 实测证据：

| mod | 1.7 dll 框架 |
|---|---|
| Raven Race `ZuoYao_RavenRace.dll` | NETCoreApp 10.0 |
| GPP `GUIPerformancePatch.dll` | NETCoreApp 10.0 |
| WVC `WVC_MoreMechanoidsWorkModes_Main.dll` | NETCoreApp 10.0 |
| Slower Pawn `SlowerPawnTickRate.dll` | **NETFramework 4.7.2**（仍能跑） |

**.NETFramework 4.8 的 dll 在 .NETCoreApp 10.0 宿主上通常能加载**（.NET 的向后兼容），只要它没用到 .NET Framework 独有的 API。这个模组只用了 `Verse` / `RimWorld` / `UnityEngine.CoreModule` / `mscorlib`，全部是基础类型，**大概率能跑**。

### 9.4 结论

**这个模组的思路是对的，而且是最优方案。** 但它有两个限制：

**限制 1：它是给人形 pawn 设计的。** `CanAcceptPawn`（`Building_TimeFreezingSpot.cs:85`）只检查：
- `pawn.Faction == Faction.OfPlayer || pawn.IsPrisonerOfColony`
- 没有被别的 pawn 占用 / 绑定
- `!pawn.Downed`
- `!pawn.IsQuestLodger()`

**没有种族限制**，所以机械体也能站上去。但 `CanAcceptPawn_FloatMenu`（:113）多了一道 `if (!pawn.IsColonist && !pawn.IsSlave) return false;` —— **右键菜单里机械体不显示选项**，只能通过 `bindPawn` 绑定 + `QuickEnter()` 按钮进入。

**限制 2：进去之后无法自动响应袭击。** `QuickEnter` 是手动按钮，没有"检测到敌人自动弹出"的逻辑。你需要自己加一个 Harmony patch 监听袭击事件，或者在袭击来临时手动一键弹出。

**推荐做法**：如果要用这个模组，把它当参考实现，自己写一个专门给机械体的版本：
- 继承 `Building_Enterable`
- 支持多 pawn（一个停机坪装 20 只）
- 支持"检测到袭击自动弹出"
- 支持"电量低于 X% 自动弹出充电"

## 十、落地检查清单（给开发者）

### 10.1 必做

- [ ] 建筑 `thingClass` 继承 `Building_Enterable`（自动获得 `IsContentsSuspended => true`）
- [ ] `ThingDef` 里设 `<tickerType>Never</tickerType>`（容器自身不必每 tick 跑，省一次虚调用）
- [ ] `TryAcceptPawn` 里必须 `pawn.DeSpawnOrDeselect()` **之后**才 `innerContainer.TryAddOrTransfer(pawn)`，顺序不能反
- [ ] `ExposeData` 里 `Scribe_Deep.Look(ref innerContainer, "innerContainer", this)`（照抄 `Building_Enterable.cs:114`）
- [ ] 建筑被拆除/摧毁时 `innerContainer.TryDropAll(Position, Map, ThingPlaceMode.Near)`，否则 pawn 会卡在容器里永久不 tick

### 10.2 多 pawn 版要额外处理

`Building_Enterable` 的 `innerContainer` 是 `ThingOwner<Thing>`，本身支持多元素，但基类只用了 `FirstOrDefault()`：

```csharp
// Building_TimeFreezingSpot.cs:17
public Pawn Occupant => innerContainer.FirstOrDefault() as Pawn;
```

多 pawn 版要自己维护 `List<Pawn>` 索引，并且注意：
- `CanAcceptPawn` 基类逻辑会因 `Occupied => innerContainer.Any` 而拒绝第二个 pawn，必须 override
- 弹出时要逐个 `DeSpawn` → `GenSpawn.Spawn(pawn, cell, map)`，注意找空位
- 存档兼容：`innerContainer` 的 `Scribe_Deep` 能存多元素，不需要额外字段

### 10.3 自动弹出的触发点

| 场景 | 挂载点 |
|---|---|
| 袭击到来 | `[HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]` 或监听 `Find.LetterStack` 收到 Raid 信件 |
| 电量低 | 每 250 tick 检查 `pawn.needs.energy.CurLevel < threshold` |
| 玩家手动 | `Gizmo` 按钮（照抄 `Building_TimeFreezingSpot.GetGizmos`） |
| 需要工作 | `[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryFindAndStartJob))]` 前置检查 |

### 10.4 不要做的事

- ❌ 不要改共享的 `ThingDef.tickerType`（会影响所有同类机械体）
- ❌ 不要在容器里放 `Spawned == true` 的 pawn（`Suspended` 会返回 false，白干）
- ❌ 不要依赖 `ThingOwner.DoTick` 的 `dontTickContents`（那是另一套机制，`Building_Enterable` 没设它）
- ❌ 不要在 `Building_Enterable` 子类里 override `IsContentsSuspended` 为 false（会直接废掉整个方案）

