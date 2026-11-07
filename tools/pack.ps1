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

# 版本段的 key 归一化与解析规则都对齐 Verse.ModLoadFolders / RimWorld.VersionControl：
#   key = childNode.Name.ToLower()，去掉前导 'v'（LoadDataFromXmlCustom）
#   可比较当且仅当 split('.') 至少两段且前两段是整数（VersionControl.TryParseVersionString）
function ConvertTo-ComparableVersion([string]$Str) {
    if ([string]::IsNullOrEmpty($Str)) { return $null }
    $parts = $Str.Split('.')
    if ($parts.Length -lt 2) { return $null }
    $a = 0; $b = 0
    if (-not [int]::TryParse($parts[0], [ref]$a)) { return $null }
    if (-not [int]::TryParse($parts[1], [ref]$b)) { return $null }
    return [version]::new($a, $b)
}

# VersionControl.IsWellFormattedVersionString：恰好两段，且两段都是整数。
function Test-WellFormattedVersion([string]$Str) {
    if ([string]::IsNullOrEmpty($Str)) { return $false }
    $parts = $Str.Split('.')
    if ($parts.Length -ne 2) { return $false }
    $a = 0; $b = 0
    return ([int]::TryParse($parts[0], [ref]$a) -and [int]::TryParse($parts[1], [ref]$b))
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

    Write-Step '3b) LoadFolders.xml 对齐 Verse.ModLoadFolders 的六条上传前校验'
    # 游戏在「上传到创意工坊」前会跑 ModLoadFolders.GetIssueList，任一条命中就弹框拦截。
    # 这里逐条复现，让 pack 阶段就报出来，不用等游戏弹窗。
    $lfPath = Join-Path $PubRoot 'LoadFolders.xml'
    if (-not (Test-Path -LiteralPath $lfPath)) { $lfPath = Join-Path $DevRoot 'LoadFolders.xml' }
    if (Test-Path -LiteralPath $lfPath) {
        $lfFailBefore = $fail
        try {
            $lfXml = [xml](Get-Content -LiteralPath $lfPath -Raw -Encoding UTF8)
        } catch {
            Write-Bad "LoadFolders.xml 无法解析：$($_.Exception.Message)"; $fail++; $lfXml = $null
        }
        if ($lfXml) {
            # supportedVersions 用于最后一条校验；缺该字段时游戏侧的集合为空，导致每段都报错。
            $aboutXml = Join-Path $PubRoot 'About\About.xml'
            if (-not (Test-Path -LiteralPath $aboutXml)) { $aboutXml = Join-Path $AboutOff 'About.xml' }
            $supported = @()
            if (Test-Path -LiteralPath $aboutXml) {
                $am = [xml](Get-Content -LiteralPath $aboutXml -Raw -Encoding UTF8)
                $supported = @($am.ModMetaData.supportedVersions.li | Where-Object { $_ } | ForEach-Object { "$_".Trim() })
            }

            $sections = @()
            foreach ($node in $lfXml.loadFolders.ChildNodes) {
                if ($node.NodeType -ne 'Element') { continue }
                $key = $node.Name.ToLower()
                if ($key.StartsWith('v')) { $key = $key.Substring(1) }
                $sections += , @{ Key = $key; Node = $node }
            }

            if ($sections.Count -eq 0) {
                Write-Bad 'LoadFolders.xml 里没有任何版本段'; $fail++
            }

            $prevKey = $null
            $prevVer = $null
            for ($si = 0; $si -lt $sections.Count; $si++) {
                $key = $sections[$si].Key
                $node = $sections[$si].Node

                # 1) ModLoadFolderListEmpty
                $folders = @($node.ChildNodes | Where-Object { $_.NodeType -eq 'Element' })
                if ($folders.Count -eq 0) { Write-Bad "v$key 没列任何文件夹（ModLoadFolderListEmpty）"; $fail++ }

                # 2) ModLoadFolderRepeatingFolder —— 游戏按 LoadFolder 整体判重，含三个 IfMod* 属性
                $seen = @{}
                foreach ($f in $folders) {
                    $sig = @(
                        $(if ($f.InnerText -in @('/', '\')) { '' } else { $f.InnerText }),
                        $f.GetAttribute('IfModActive'),
                        $f.GetAttribute('IfModActiveAll'),
                        $f.GetAttribute('IfModNotActive')
                    ) -join '|'
                    if ($seen.ContainsKey($sig)) { Write-Bad "v$key 里重复列了同一文件夹 '$($f.InnerText)'（ModLoadFolderRepeatingFolder）"; $fail++ }
                    else { $seen[$sig] = $true }
                }

                # 3) ModLoadFolderMalformedVersion + 4) ModLoadFolderDefaultDeprecated
                if ($key -eq 'default') {
                    Write-Bad "用了 default 段，该写法已弃用（ModLoadFolderDefaultDeprecated）"; $fail++
                } elseif (-not (Test-WellFormattedVersion $key)) {
                    Write-Bad "v$key 版本名格式不合法，须为两段整数如 v1.7（ModLoadFolderMalformedVersion）"; $fail++
                }

                # 5) ModLoadFolderOutOfOrder —— 游戏按书写顺序比较相邻两段
                $ver = ConvertTo-ComparableVersion $key
                if ($null -ne $prevVer -and $null -ne $ver -and $ver -lt $prevVer) {
                    Write-Bad "v$key 写在 v$prevKey 后面，版本段必须按升序排列（ModLoadFolderOutOfOrder）"; $fail++
                }

                # 6) ModLoadFolderDoesntExist —— '/' 在游戏里被换成空串，指 mod 根目录
                foreach ($f in $folders) {
                    $name = if ($f.InnerText -in @('/', '\')) { '' } else { $f.InnerText }
                    if (-not (Test-Path -LiteralPath (Join-Path $PubRoot $name) -PathType Container)) {
                        Write-Bad "v$key 列的文件夹 '$($f.InnerText)' 在发布目录里不存在（ModLoadFolderDoesntExist）"; $fail++
                    }
                }

                # 7) ModLoadFolderDefinesUnsupportedGameVersion
                if ($null -ne $ver -and $supported.Count -gt 0) {
                    $matched = $false
                    foreach ($s in $supported) {
                        $sv = ConvertTo-ComparableVersion $s
                        if ($null -ne $sv -and $sv.Major -eq $ver.Major -and $sv.Minor -eq $ver.Minor) { $matched = $true; break }
                    }
                    if (-not $matched) { Write-Bad "v$key 不在 About.xml 的 supportedVersions 里（ModLoadFolderDefinesUnsupportedGameVersion）"; $fail++ }
                }

                $prevKey = $key
                $prevVer = if ($null -ne $ver) { $ver } else { $prevVer }
            }

            if ($fail -eq $lfFailBefore) {
                Write-Ok "版本段 $(($sections | ForEach-Object { 'v' + $_.Key }) -join ' -> ') 全部合规"
            }
        }
    } else { Write-Note '跳过（找不到 LoadFolders.xml）' }

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