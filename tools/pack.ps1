<#
.SYNOPSIS
    PerfProbe 创意工坊发布打包。源码原地不动，发布件独立复制出去。

.DESCRIPTION
    为什么不能直接上传 mod 根目录：
      Verse.Steam.Workshop.BakeAndUpload -> SetItemContent(hook.Directory.FullName)
      hook.Directory 就是 mod 根目录，Steam 端整目录打包，没有白名单、.gitignore 无效。
      而 mod 根目录里有 Source/ docs/ tools/ PLAN.md，这些都会进发布包。

    本方案：
      开发目录  Mods\PerfProbe\            源码原地保留，一个字都不动
      发布目录  Mods\PerfProbe-Publish\    只含 About + 1.7 + LoadFolders，每次重建

    两个目录 packageId 相同，游戏会 Log.Error 并忽略其中一个（Verse.ModLister.TryAddMod）。
    所以上传前把开发目录的 About 改名成 About.off——缺 About.xml 的目录
    DirectXmlLoader.ItemFromXmlFile 返回空 ModMetaDataInternal，游戏当无效目录跳过，
    不会与发布目录冲突。上传完再改回来。

    动作：
      pack      重建发布目录 + 自检（不碰开发目录）
      publish   改名 About->About.off，让游戏只认发布目录
      restore   改回 About.off->About，恢复开发状态
      check     只自检

.EXAMPLE
    .\tools\pack.ps1 pack
    .\tools\pack.ps1 publish    # 然后进游戏上传
    .\tools\pack.ps1 restore    # 上传完恢复
#>
[CmdletBinding()]
param(
    [ValidateSet('pack', 'publish', 'restore', 'check')]
    [string]$Action = 'pack'
)

$ErrorActionPreference = 'Stop'

$DevRoot = Split-Path -Parent $PSScriptRoot
$ModName = Split-Path -Leaf $DevRoot
$ModsDir = Split-Path -Parent $DevRoot
$PubRoot = Join-Path $ModsDir "$ModName-Publish"

$RuntimeEntries = @('About', '1.7', '1.6', 'LoadFolders.xml')

# docs 里只发「给 AI 看的」两份：GuideWriter 在每次开始采集时会把它们复制到
# 用户数据目录，所以发布包里必须有，否则玩家拿到的数据目录里不会有判读规则。
# BUILD / PUBLISH / MECH-PERF / PLAN 属开发资产，不进包。
$DocsEntries = @('schema.md', 'AI-ANALYSIS-GUIDE.md')

$AboutOn  = Join-Path $DevRoot 'About'
$AboutOff = Join-Path $DevRoot 'About.off'

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "    OK   $Text" -ForegroundColor Green }
function Write-Bad([string]$Text)  { Write-Host "    FAIL $Text" -ForegroundColor Red }
function Write-Note([string]$Text) { Write-Host "    注意 $Text" -ForegroundColor DarkYellow }

# 逐文件复制并校验 SHA256，任一不符即抛错。不用 Move-Item：跨卷时它先建目录再搬文件，
# 中途失败会留下空壳而源已被删（2026-11-02 实测过一次，36 个 .cs 全变空壳目录）。
function Copy-Tree([string]$Src, [string]$Dst) {
    $isDir = Test-Path -LiteralPath $Src -PathType Container
    $files = if ($isDir) {
        @(Get-ChildItem -LiteralPath $Src -Recurse -File -Force)
    } else {
        @(Get-Item -LiteralPath $Src -Force)
    }
    $n = 0
    foreach ($f in $files) {
        if ($isDir) {
            $dest = Join-Path $Dst $f.FullName.Substring($Src.Length + 1)
        } elseif (Test-Path -LiteralPath $Dst -PathType Container) {
            $dest = Join-Path $Dst $f.Name
        } else {
            $dest = $Dst
        }
        $parent = Split-Path -Parent $dest
        if ($parent -and -not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
        if ((Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $dest       -Algorithm SHA256).Hash) {
            throw "哈希校验失败：$($f.FullName) -> $dest"
        }
        $n++
    }
    return $n
}

function Invoke-Pack {
    Write-Step "重建发布目录 $PubRoot"
    if (Test-Path -LiteralPath $PubRoot) {
        Remove-Item -LiteralPath $PubRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $PubRoot -Force | Out-Null

    foreach ($e in $RuntimeEntries) {
        $src = Join-Path $DevRoot $e
        if ($e -eq 'About' -and -not (Test-Path -LiteralPath $src)) {
            if (Test-Path -LiteralPath $AboutOff) { $src = $AboutOff }
            else { throw "找不到 About（About 与 About.off 都不存在）" }
        }
        if (-not (Test-Path -LiteralPath $src)) { throw "缺少运行必需件：$e" }
        $n = Copy-Tree $src (Join-Path $PubRoot $e)
        Write-Ok "$e（$n 个文件）"
    }

    # docs 只挑两份，整个 docs 目录不能照搬（里面有开发文档与本机实测报告）。
    $docsDst = Join-Path $PubRoot 'docs'
    New-Item -ItemType Directory -Path $docsDst -Force | Out-Null
    foreach ($d in $DocsEntries) {
        $src = Join-Path (Join-Path $DevRoot 'docs') $d
        if (-not (Test-Path -LiteralPath $src)) { throw "缺少 AI 文档：docs\$d" }
        Copy-Tree $src $docsDst | Out-Null
    }
    Write-Ok "docs（$($DocsEntries.Count) 个文件：$($DocsEntries -join ' / ')）"

    Get-ChildItem -LiteralPath $PubRoot -Recurse -File |
        Where-Object { $_.Extension -in @('.pdb', '.xml') -and $_.DirectoryName -like '*Assemblies*' } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force; Write-Ok "剔除 $($_.Name)" }

    $size = (Get-ChildItem -LiteralPath $PubRoot -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Ok ("包体 {0:N1} KB" -f ($size / 1KB))
}

function Invoke-Publish {
    if (-not (Test-Path -LiteralPath $PubRoot)) { throw "发布目录不存在，先跑 pack" }
    if (-not (Test-Path -LiteralPath $AboutOn)) {
        if (Test-Path -LiteralPath $AboutOff) { Write-Note 'About 已经是 .off 状态，无需重复操作'; return }
        throw "开发目录里既没有 About 也没有 About.off"
    }
    Write-Step '把开发目录的 About 改名为 About.off（游戏将忽略开发目录）'
    Rename-Item -LiteralPath $AboutOn -NewName 'About.off'
    Write-Ok 'About -> About.off'

    Write-Step 'Mods\ 下两个 PerfProbe 目录的状态'
    foreach ($d in @($DevRoot, $PubRoot)) {
        $hasAbout = Test-Path -LiteralPath (Join-Path $d 'About\About.xml')
        $tag = if ($hasAbout) { '游戏会认（是 mod）' } else { '游戏忽略（非 mod）' }
        Write-Host "    $(Split-Path -Leaf $d)  ->  $tag"
    }
    Write-Host ''
    Write-Host '  现在可以进游戏：主菜单 -> Mods -> 找到 PerfProbe -> Upload to Steam Workshop' -ForegroundColor Yellow
    Write-Host '  列表里只会出现一份 PerfProbe（发布目录那份）。' -ForegroundColor Yellow
    Write-Host '  上传完成后执行恢复：.\tools\pack.ps1 restore' -ForegroundColor Yellow
}

function Invoke-Restore {
    if (-not (Test-Path -LiteralPath $AboutOff)) {
        Write-Note '没有 About.off，开发目录已是正常状态'
        return
    }
    Write-Step '把 About.off 改回 About'
    Rename-Item -LiteralPath $AboutOff -NewName 'About'
    Write-Ok 'About.off -> About'
    Write-Note "发布目录仍在 $PubRoot，不影响开发；要清掉可手动删除"
}

function Invoke-Check {
    $fail = 0

    Write-Step '1) 开发目录：源码完整'
    $cs = @(Get-ChildItem -LiteralPath (Join-Path $DevRoot 'Source') -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue)
    if ($cs.Count -gt 0) { Write-Ok "Source 下 $($cs.Count) 个 .cs" }
    else { Write-Bad 'Source 下没有 .cs'; $fail++ }
    if (Test-Path -LiteralPath (Join-Path $DevRoot 'docs')) { Write-Ok 'docs 存在' }
    else { Write-Bad 'docs 缺失'; $fail++ }
    if (Test-Path -LiteralPath (Join-Path $DevRoot 'PLAN.md')) { Write-Ok 'PLAN.md 存在' }
    else { Write-Bad 'PLAN.md 缺失'; $fail++ }

    Write-Step '2) 发布目录：只含运行必需件 + 两份 AI 文档'
    $allowTop = $RuntimeEntries + @('docs')
    if (Test-Path -LiteralPath $PubRoot) {
        $extra = @(Get-ChildItem -LiteralPath $PubRoot -Force |
            Where-Object { $_.Name -notin $allowTop })
        if ($extra.Count -gt 0) {
            $extra | ForEach-Object { Write-Bad "多出 $($_.Name)"; $fail++ }
        } else { Write-Ok ($allowTop -join ' / ') }

        # docs 里必须恰好是这两份，多了说明混进了开发文档
        $pubDocs = Join-Path $PubRoot 'docs'
        if (Test-Path -LiteralPath $pubDocs) {
            $got = @(Get-ChildItem -LiteralPath $pubDocs -File | Select-Object -ExpandProperty Name)
            $bad = @($got | Where-Object { $_ -notin $DocsEntries })
            $missing = @($DocsEntries | Where-Object { $_ -notin $got })
            if ($bad.Count -gt 0) { $bad | ForEach-Object { Write-Bad "docs 里不该有 $_"; $fail++ } }
            if ($missing.Count -gt 0) { $missing | ForEach-Object { Write-Bad "docs 缺 $_"; $fail++ } }
            if ($bad.Count -eq 0 -and $missing.Count -eq 0) { Write-Ok "docs 内容正确（$($got.Count) 个）" }
        } else { Write-Bad '缺 docs（AI 拿不到判读规则）'; $fail++ }
    } else { Write-Bad '发布目录不存在'; $fail++ }

    Write-Step '3) 发布目录 XML 可解析'
    Get-ChildItem -LiteralPath $PubRoot -Recurse -Filter *.xml -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                [xml](Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8) | Out-Null
                Write-Ok $_.FullName.Substring($PubRoot.Length + 1)
            } catch {
                Write-Bad "$($_.FullName.Substring($PubRoot.Length + 1)) :: $($_.Exception.Message)"
                $fail++
            }
        }

    Write-Step '4) 程序集目录只有 dll + deps.json（1.7 与 1.6 各一份）'
    # 1.6 那份是 net472 产物，不该带 deps.json：老版本读不懂 .NETCoreApp 的依赖清单。
    foreach ($ver in @('1.7', '1.6')) {
        $asm = Join-Path $PubRoot "$ver\Assemblies"
        if (-not (Test-Path -LiteralPath $asm)) { Write-Bad "缺 $ver\Assemblies"; $fail++; continue }
        if (-not (Test-Path -LiteralPath (Join-Path $asm 'PerfProbe.dll'))) {
            Write-Bad "缺 $ver\Assemblies\PerfProbe.dll"; $fail++
        }
        Get-ChildItem -LiteralPath $asm -File | ForEach-Object {
            if ($_.Extension -in @('.dll', '.json')) { Write-Ok "$ver/$($_.Name)" }
            else { Write-Bad "$ver 里不该出现 $($_.Name)"; $fail++ }
        }
        if ($ver -eq '1.6' -and (Test-Path -LiteralPath (Join-Path $asm 'PerfProbe.deps.json'))) {
            Write-Bad '1.6 侧不该有 deps.json'; $fail++
        }
    }

    Write-Step '5) dll 内不含本机盘符路径'
    foreach ($ver in @('1.7', '1.6')) {
        $dll = Join-Path $PubRoot "$ver\Assemblies\PerfProbe.dll"
        if (-not (Test-Path -LiteralPath $dll)) { continue }
        $ascii = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($dll))
        $hits = [regex]::Matches($ascii, '[A-Za-z]:\\\\').Count
        if ($hits -eq 0) { Write-Ok "$ver 无盘符路径" } else { Write-Bad "$ver 命中 $hits 处"; $fail++ }
    }

    Write-Step '5b) 两个目标框架的 BCL 引用不可互换'
    # 判据：1.6 那份必须引 mscorlib（.NET Framework），1.7 那份必须不引。
    # 反了就是编译引用串了版本，进游戏会 System.BadImageFormatException。
    $dll16 = Join-Path $PubRoot '1.6\Assemblies\PerfProbe.dll'
    $dll17 = Join-Path $PubRoot '1.7\Assemblies\PerfProbe.dll'
    if ((Test-Path -LiteralPath $dll16) -and (Test-Path -LiteralPath $dll17)) {
        $a16 = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($dll16))
        $a17 = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($dll17))
        if ($a16.Contains('mscorlib')) { Write-Ok '1.6 引 mscorlib' }
        else { Write-Bad '1.6 未引 mscorlib（可能编成了 net10）'; $fail++ }
        if (-not $a17.Contains('mscorlib')) { Write-Ok '1.7 未引 mscorlib' }
        else { Write-Bad '1.7 引了 mscorlib（可能编成了 net472）'; $fail++ }
    }

    Write-Step '6) 发布件里不含本机路径'
    # 只查「具体到本机」的痕迹：真实用户名、具体安装目录、具体盘符。
    # 不查裸 'AppData' 与 'C:\Users\'：前者会命中 %USERPROFILE%\AppData 这类通用写法，
    # 后者会命中 C:\Users\<用户名> 这类占位符示例，两者都该出现在文档里。
    #
    # $env:DSH_HOME 可能为空（例如从 bash 里转调 PowerShell 时没继承该变量），
    # 直接 Join-Path 会抛 "参数 Path 为空"。这里先判空，缺 rg 时退到 PowerShell 自带匹配。
    $rg = if ($env:DSH_HOME) { Join-Path $env:DSH_HOME '.desktop-bin\rg.exe' } else { '' }
    $patterns = @('Users\ASUS', 'Program Files', 'RimModManager', 'E:\', 'D:\')
    if ($rg -and (Test-Path -LiteralPath $rg) -and (Test-Path -LiteralPath $PubRoot)) {
        $args2 = @('-l', '-F') + ($patterns | ForEach-Object { @('-e', $_) }) + @($PubRoot, '--max-filesize', '512K', '--no-messages')
        $found = & $rg @args2 2>$null
        if ($found) { $found | ForEach-Object { Write-Bad $_ }; $fail++ }
        else { Write-Ok '无本机路径' }
    } elseif (Test-Path -LiteralPath $PubRoot) {
        # 没有 rg 时的降级路径：逐个文件读文本再匹配。发布件只有 6 个文件，成本可忽略。
        $hits = @()
        foreach ($f in @(Get-ChildItem -LiteralPath $PubRoot -Recurse -File)) {
            $txt = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction SilentlyContinue
            if (-not $txt) { continue }
            foreach ($p in $patterns) {
                if ($txt.Contains($p)) { $hits += "$($f.FullName) :: $p" }
            }
        }
        if ($hits.Count -gt 0) { $hits | ForEach-Object { Write-Bad $_ }; $fail++ }
        else { Write-Ok '无本机路径（未用 rg）' }
    } else { Write-Note '跳过（发布目录不存在）' }

    Write-Step '7) packageId 冲突状态'
    $devHasAbout = Test-Path -LiteralPath (Join-Path $DevRoot 'About\About.xml')
    $pubHasAbout = Test-Path -LiteralPath (Join-Path $PubRoot 'About\About.xml')
    if ($devHasAbout -and $pubHasAbout) {
        Write-Note '两份都有 About.xml，游戏会 Log.Error 并忽略其中一份'
        Write-Note '上传前请先跑 publish'
    } elseif ($pubHasAbout) {
        Write-Ok '只有发布目录可见，可以上传'
    } elseif ($devHasAbout) {
        Write-Ok '只有开发目录可见（未上传状态）'
    } else { Write-Bad '两份都不可见'; $fail++ }

    Write-Host ''
    if ($fail -eq 0) { Write-Host '自检：全部通过' -ForegroundColor Green }
    else { Write-Host "自检：$fail 项未通过" -ForegroundColor Red }
    return $fail
}

switch ($Action) {
    'pack'    { Invoke-Pack; Write-Host ''; Invoke-Check | Out-Null }
    'publish' { Invoke-Pack; Write-Host ''; Invoke-Publish }
    'restore' { Invoke-Restore }
    'check'   { Invoke-Check | Out-Null }
}