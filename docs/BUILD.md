# PerfProbe 构建与部署

## 环境事实（本机实测，2026-xx）

| 项 | 值 |
|---|---|
| 游戏版本 | `1.7.4995 rev309`（`RimWorld\Version.txt`） |
| 目标框架 | **双目标**：1.7 侧 `net10.0`（`RimWorld.runtimeconfig.json` 的 `tfm`）；1.6 侧 `net472` |
| 游戏 dll 位置 | **游戏根目录平铺**，不在 `RimWorldWin64_Data\Managed` |
| 可用的 dotnet | **必须 10.0.x**（`dotnet --list-sdks` 里有 `10.0.*`）。开发机上用的是随 RimModManager 附带的便携版 `…\RimModManager\.upgrade\dotnet10\dotnet.exe`（10.0.402） |
| 系统 dotnet | 8.x / 9.x —— **不能编 `net10.0`，别用** |
| Harmony | 1.7 侧 `…\2009463077\1.7\Assemblies\0Harmony.dll`（3.0.1.0）；1.6 侧 `…\2009463077\1.6\Assemblies\0Harmony.dll`（2.4.3.0） |
| 1.6 参考程序集 | `Source\refs\1.6\`（三个 dll，来自 NuGet `Krafs.Rimworld.Ref 1.6.4871` 的 `ref/net472/`） |

> `Mods\2009463077\Current\Assemblies\0Harmony.dll` 是 2.4.1.0，与 1.7 不匹配，不要引用它。

## 双目标框架

`PerfProbe.csproj` 用 `<TargetFrameworks>net10.0;net472</TargetFrameworks>` 一次编出两份：

| TargetFramework | OutputPath | 引用 | deps.json |
|---|---|---|---|
| `net10.0` | `..\..\1.7\Assemblies\` | `0Harmony`(1.7) / `Assembly-CSharp` / `RimWorld` / `UnityStubs` / `Waylith` | 生成（421 B） |
| `net472` | `..\..\1.6\Assemblies\` | `0Harmony`(1.6) / `Assembly-CSharp` / `UnityEngine.CoreModule` / `System.IO.Compression` | **不生成**（`GenerateDependencyFile=false`） |

三个必须守住的约束，`pack.ps1 -Action check` 的第 5b 项会替你看住：

1. **1.6 那份必须引 `mscorlib`，1.7 那份必须不引。** 反了就是编译引用串了版本，进游戏 `System.BadImageFormatException`。
2. **1.6 侧不能有 `deps.json`。** 那是 `.NETCoreApp` 的依赖清单，老运行时读不懂。
3. **1.6 参考程序集里没有 `RimWorld.dll`。** 1.6 时代所有类型都在 `Assembly-CSharp.dll`，所以 `net472` 组不能引用 `RimWorld`。

`AppendTargetFrameworkToOutputPath=false` 是必须的，否则产物会落到 `bin\Release\net10.0\` 这类子目录，与 `OutputPath` 叠加成两层。

### 1.6 侧的三个已知坑

| 现象 | 原因 | 修法 |
|---|---|---|
| `CS1069 未能找到类型 ZipArchive` | 1.6 参考里 `System.IO.Compression` 是 façade，类型转发到 4.0.0.0 | csproj 里对该 dll 显式 `<Reference>` 带 `HintPath` |
| `CS1705 System.Runtime 版本冲突` | NuGet `Lib.Harmony` 默认解析到 net10.0 版 | 直接用 workshop 里 `2009463077\1.6\Assemblies\0Harmony.dll`（纯 net472） |
| `CS1503 无法将 MethodInfo 转换为 Delegate` | Harmony 3 的 `HarmonyMethod` 重载更严 | 同上，用 1.6 的 0Harmony 后消失 |

`Output\FeedbackPack.cs` 里 `CompressionLevel` 必须全限定成 `System.IO.Compression.CompressionLevel.Optimal` —— `UnityEngine.CompressionLevel` 同名，`net10.0` 组会二义性报错。

## 编译

```powershell
cd <仓库>\Source\PerfProbe
$env:DOTNET_CLI_HOME="$env:TEMP\pp-dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1"
$env:DOTNET_NOLOGO="1"
dotnet build -c Release -v q --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false -nodeReuse:false
```

产物由 csproj 的 `OutputPath` 决定：`net10.0` 落到 `1.7\Assemblies\PerfProbe.dll`，
`net472` 落到 `1.6\Assemblies\PerfProbe.dll`，**一次 build 两份都出**。

> **后面那串开关不是可选项。** 在受限沙箱里 `VBCSCompiler` 与 MSBuild 节点的命名管道
> 连不上，编译器进程会被杀，日志只留一行 `MSB5021: 终止任务可执行文件"csc"`，
> 而错误计数是 `0 个错误` —— 看起来像成功，其实 `PerfProbe.dll` 根本没生成。
> `--disable-build-servers` 关掉编译服务器，`-m:1 -nodeReuse:false` 关掉 MSBuild 多节点与常驻节点，
> 三者缺一就会间歇性假失败。判断真伪看落地时间戳，不看日志里的错误计数。

### csproj 里的四个路径属性

```xml
<RimWorldDir Condition="'$(RimWorldDir)' == ''">E:\Program Files\steam\steamapps\common\RimWorld</RimWorldDir>
<WorkshopRoot Condition="'$(WorkshopRoot)' == ''">$([System.IO.Path]::GetFullPath('$(RimWorldDir)\..\..\workshop\content\294100'))</WorkshopRoot>
<Harmony17Dir Condition="'$(Harmony17Dir)' == ''">$(WorkshopRoot)\2009463077\1.7\Assemblies</Harmony17Dir>
<Harmony16Dir Condition="'$(Harmony16Dir)' == ''">$(WorkshopRoot)\2009463077\1.6\Assemblies</Harmony16Dir>
<Ref16Dir Condition="'$(Ref16Dir)' == ''">$(MSBuildThisFileDirectory)..\refs\1.6</Ref16Dir>
```

**四项都可覆盖**，`Condition="'$(X)' == ''"` 表示「有外部值就用外部值」。三种覆盖方式任选：

```powershell
# 1) 环境变量（最省事）
$env:RimWorldDir = "D:\SteamLibrary\steamapps\common\RimWorld"

# 2) 命令行
dotnet build -c Release -p:RimWorldDir="D:\SteamLibrary\steamapps\common\RimWorld"

# 3) 同级新建 Directory.Build.props（不进版本库，本地生效）
```

`WorkshopRoot` 从 `RimWorldDir` 派生：`steamapps\common\RimWorld` 往上两级是 `steamapps`，
工坊内容在 `steamapps\workshop\content\294100\`，**不在 `common` 下面**——少写一级会找不到 `0Harmony.dll`。

`Ref16Dir` 的 `..\` 不能少：`refs` 实际在 `Source\refs\1.6`，而 csproj 在 `Source\PerfProbe\` 下。
写错会在 `net472` 编译时报 161 个 `CS0246 未能找到类型或命名空间名"Verse"`。

> `Source\refs\1.6\` 里的三个 dll 是 Ludeon 的游戏程序集，**不随本仓库分发**（.gitignore 已排除）。
> 只编 1.7 的话可以 `dotnet build -f net10.0`，不需要它们。

### 引用的程序集

`net10.0` 组（5 个）：

| 程序集 | 来源 | Private |
|---|---|---|
| `0Harmony` | `$(Harmony17Dir)` | false |
| `Assembly-CSharp` | `$(RimWorldDir)` | false |
| `RimWorld` | `$(RimWorldDir)` | false |
| `UnityStubs` | `$(RimWorldDir)` | false |
| `Waylith` | `$(RimWorldDir)` | false |

`net472` 组（4 个）：

| 程序集 | 来源 | Private |
|---|---|---|
| `0Harmony` | `$(Harmony16Dir)` | false |
| `Assembly-CSharp` | `$(Ref16Dir)` | false |
| `UnityEngine.CoreModule` | `$(Ref16Dir)` | false |
| `System.IO.Compression` | `$(Ref16Dir)` | false |

全部 `Private=false`：游戏自带这些 dll，不能随模组再复制一份。

`Waylith` 是 1.7 的 CLR 重定向层，`RimWorld.runtimeconfig.json` 里
`WaylithClr.AssemblyNameRedirects` 把 `UnityEngine*` 重定向到 `UnityStubs`。
引用它才能拿到 `UnityEngine.Rect` / `UnityEngine.Vector2` 等类型。
1.6 没有这一层，`UnityEngine.CoreModule` 是 Unity 自己的 BCL 相邻程序集。

## 部署

csproj 的 `OutputPath` 已经指向 `1.7\Assemblies\` 与 `1.6\Assemblies\`，
**编译即部署**，不需要手工拷 dll。

模组目录结构：

```
PerfProbe/
├── About/About.xml          （发布态时改名 About.off）
├── LoadFolders.xml
├── 1.7/Assemblies/PerfProbe.dll + PerfProbe.deps.json
├── 1.6/Assemblies/PerfProbe.dll
├── docs/{schema.md,AI-ANALYSIS-GUIDE.md,BUILD.md,…}
├── refs/1.6/{Assembly-CSharp,UnityEngine.CoreModule,System.IO.Compression}.dll
├── PLAN.md
└── Source/PerfProbe/…
```

`LoadFolders.xml`：

```xml
<loadFolders>
  <v1.7><li>/</li><li>1.7</li></v1.7>
  <v1.6><li>1.6</li></v1.6>
</loadFolders>
```

`<v1.7>` 声明 `/` 与 `1.7` 两层，因此 `About` 在根、dll 在 `1.7/Assemblies`。
`<v1.6>` **刻意不含 `<li>/</li>`** —— 开发根目录里有 `Source` / `docs` / `tools` / `refs`，
1.6 不该把根目录整个扫进 Def 库。

注意 `LoadFolders` 只影响 Def / Patch / Assemblies 的扫描路径，
`ModContentPack.RootDir` 在任何版本下都恒为模组根目录。
所以 `GuideWriter`（读 `ProbePaths.ModRootDir` + `docs/AI-ANALYSIS-GUIDE.md`）
在 1.6 下同样能找到根目录的 `docs/`，两份版本的指南复制行为一致。

## 启用

1. 启动游戏，主菜单 → 选项 → 模组
2. 勾选 **PerfProbe**（`packageId = perfprobe.runtime`）
3. 依赖 `brrainz.harmony` 会自动排在前面（`About.xml` 的 `modDependencies` 已声明）
4. 进游戏后：选项 → 模组设置 → PerfProbe
5. 勾「启用采集」→ 选速度档 → 点「开始被动采集」

## 日志位置

- 模组自身运行日志：`%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log`（**注意有 `Logs\` 子目录**），搜 `[PerfProbe]`
- 上一轮的日志被轮转为同目录的 `Player-prev.log`，排查「上一轮为什么没输出」时两个都要看
- 采集数据：默认 `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\PerfProbe\`（`GenFilePaths.SaveDataFolderPath` 之下，**与该层的 `Saves\` 平级**，不在 `Saves\` 里面），可在设置面板改

### 启动后应看到这 5 行（缺哪行就说明卡在哪一步）

```
[PerfProbe] 构造函数进入，版本 1.0.0
[PerfProbe] 归因索引: asm=N def=N type=N
[PerfProbe] Harmony patch 挂载完成：成功 N 个类，失败 0 个
[PerfProbe] 输出目录: <路径>
[PerfProbe] 已加载。采集默认关闭，在「选项 - 模组设置 - PerfProbe」中开启。
```

- 一条都没有 → 该版本目录下 `Assemblies\PerfProbe.dll` 不存在，或 `LoadFolders.xml` 没生效
- 只有第一行 → `PerfProbeBootstrap` 的静态构造没跑：`[StaticConstructorOnStartup]` 丢了
- 有「归因索引」但 `def=0` → 挂载时机太早，Def 库还没加载完
- 「成功 0 个类」→ **所有 patch 都没挂上，采集不可能工作**，按下面的「挂载失败」查
- 「失败 M 个」且 M > 0 → 单个类失败不影响其余，按日志里的 `挂载 patch 类失败 <类名>` 逐条看
- 1.6 下出现 `System.BadImageFormatException` 或 `Could not load file or assembly` →
  游戏加载了错版本的那份 dll。用 `pack.ps1 -Action check` 的第 5b 项验证 BCL 引用是否串版


> **挂载时机踩坑记录**：早期版本用 `LongEventHandler.QueueLongEvent(InitPatch, "PerfProbe.Init", doAsynchronously: true, null)`
> 延后挂载，实测 `InitPatch` 一次都没执行（Player.log 里 `[PerfProbe]` 零命中，但设置文件已生成说明 dll 确实加载了）。
> 现改为 `[StaticConstructorOnStartup]`，由 `StaticConstructorOnStartupUtility.CallAll()` 调用，此时 Def 已全部加载。
> 另注意 `Mod` 构造函数运行在 `LoadedModManager.CreateModClasses()` 阶段，**早于 `LoadModXML()`**，那时 Def 库是空的，
> 不能在构造函数里建 def 索引。

## 排错

| 现象 | 查什么 |
|---|---|
| 日志里没有 `[PerfProbe]` | 看 `Logs\Player.log`（有子目录）；再确认 dll 存在、Harmony 已启用 |
| 只有「构造函数进入」没有后续 | `PerfProbeBootstrap` 没被调用，检查 `[StaticConstructorOnStartup]` |
| 点了「开始被动采集」但界面「未采集」/「0 落盘」 | 设置里「启用采集」是否勾上。关闭面板会触发 `WriteSettings → ApplySettings`，`!Enabled` 时它会把刚开的会话立刻 `EndSession("设置中关闭采集")`。现在点按钮会自动勾上 Enabled，日志里应有「已顺带打开」字样 |
| 界面显示「patch 全部挂载失败」 | 日志搜 `挂载 patch 类失败`，逐条看类名与异常。常见根因见下表 |
| 「成功 0 个类」但没有任何失败日志 | `[HarmonyPatch]` 特性丢了，或 `PatchEachClass` 没被调用 |
| 编译报 `MSB3021/MSB3027 无法将 obj\Release\PerfProbe.dll 复制到 …` | 游戏还开着占用了 dll。关游戏后重编；仍报错就把旧 dll 改名再编（`.old` 扩展名不会被当成 dll 加载） |
| `挂载 Harmony patch 失败` | Harmony 版本不对（1.7 要 3.0.1.0，1.6 要 2.4.3.0），或引用了 `Mods\2009463077\Current` 那份旧的 |
| `No parameter at requested index __xxx` | patch 方法里写了 `__instance`/`__state`/`__result` 之外的 `__` 参数。Harmony 只认这几个固定名，其余一律当「取原方法同名参数」，不存在就抛异常 |
| `输出目录不可用` | 设置里的路径无写权限；模组会回退到默认目录或临时目录，看 `summary.md` 的「目录回退原因」 |
| `已挂 patch 的组件类型数` 为 0 | 没有任何模组 override `MapComponentTick`，属正常（原版也很少） |
| jsonl 里 `dropLines` > 0 | 写盘跟不上，加大 `WindowSec` 或减小 `SampleK` 的开销 |
| `msPerTick` 与实测帧率对不上 | 确认 `speed` 字段只有一档；混档平均不可比 |


## 编译期已知坑

| 报错 | 原因 | 修法 |
|---|---|---|
| `CS0103: 当前上下文中不存在名称"PerfProbeMod"` | `EnableDefaultCompileItems=false` 后根目录 `.cs` 未被 Include | csproj 加 `<Compile Include="*.cs" />` |
| `CS0246: 未能找到类型 VersionControl` | 它属于 `RimWorld` 命名空间 | 文件加 `using RimWorld;` |
| `CS0246: 未能找到类型 MapComponent` | `MapComponent` 在 `Verse`，`Pawn_StanceTracker`/`VerbTracker`/`Pawn_HealthTracker`/`Pawn_EquipmentTracker` 也都在 `Verse`（不是 `Verse.AI`） | 只 `using Verse;`，别乱加 `Verse.AI` |
| `未能找到类型或命名空间名"Rect"` | 没引用 `UnityStubs`/`Waylith` | 检查 csproj Reference |
| `NETSDK1045: 当前 .NET SDK 不支持 net10.0` | 用了系统 dotnet | 换便携 dotnet10 |
| `CS1069 未能找到类型 ZipArchive` | `net472` 侧 `System.IO.Compression` 是 façade | csproj 显式 `<Reference>` + `HintPath` 指向 `$(Ref16Dir)` |
| `CS1705 System.Runtime 版本冲突` | Harmony 解析到了 net10.0 版 | 换 `$(Harmony16Dir)` 的纯 net472 版 0Harmony |
| `CS1503 无法将 MethodInfo 转换为 Delegate` | Harmony 3 的 `HarmonyMethod` 重载更严 | 同上 |
| `CS0246 未能找到类型或命名空间名"Verse"`（161 个） | `Ref16Dir` 少了一层 `..`，指向了不存在的 `Source\PerfProbe\refs\1.6` | 改成 `$(MSBuildThisFileDirectory)..\refs\1.6` |
| `CS0104 CompressionLevel 二义性` | `UnityEngine.CompressionLevel` 与 `System.IO.Compression.CompressionLevel` 同名 | 全限定 `System.IO.Compression.CompressionLevel.Optimal` |
