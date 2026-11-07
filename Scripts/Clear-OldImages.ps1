#Requires -Version 5.1
<#
.SYNOPSIS
    按天数清理存图目录（支持 UNC 网络共享，弥补当前软件版本 UNC 下不自动清理的问题）。

.DESCRIPTION
    只删除“名字严格是 8 位日期（yyyyMMdd）”且早于保留天数的目录，其它目录和文件一律不动。
    兼容两种目录结构：
      root\20261106\Cam1\OK\...          （当前默认模板：日期在第一层）
      root\料号\20261106\Cam1\OK\...    （旧模板：日期在第二层）
    也支持 root 直接是日期目录的父级（即上面两种都能识别）。

    默认是“预览模式”：只列出要删的目录，不真删。确认无误后加 -Execute 才执行删除。

.PARAMETER Root
    存图根目录，UNC 或本地路径，例如 \\192.168.10.50\VisionImages 或 D:\VisionImages。

.PARAMETER Days
    保留天数，默认 30。日期早于“今天 - Days”的目录会被清理。

.PARAMETER Execute
    真删开关。不加只预览；加了才执行删除。

.PARAMETER ShowSize
    预览时统计每个目录占用大小（文件多时较慢，默认关闭）。

.EXAMPLE
    # 预览：看看 30 天前有哪些目录会被删
    powershell -ExecutionPolicy Bypass -File .\Clear-OldImages.ps1 -Root \\192.168.10.50\VisionImages -Days 30

.EXAMPLE
    # 真删
    powershell -ExecutionPolicy Bypass -File .\Clear-OldImages.ps1 -Root \\192.168.10.50\VisionImages -Days 30 -Execute

.EXAMPLE
    # 目标机本地跑，顺便看每个目录多大
    powershell -ExecutionPolicy Bypass -File .\Clear-OldImages.ps1 -Root D:\VisionImages -Days 30 -ShowSize
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Root,

    [int]$Days = 30,

    [switch]$Execute,

    [switch]$ShowSize
)

$ErrorActionPreference = 'Stop'

function Write-Section([string]$Text) {
    Write-Host ''
    Write-Host ('=' * 64) -ForegroundColor DarkCyan
    Write-Host ('  ' + $Text) -ForegroundColor Cyan
    Write-Host ('=' * 64) -ForegroundColor DarkCyan
}
function Write-Step([string]$Text) { Write-Host ('[步骤] ' + $Text) -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host ('[ OK ] ' + $Text) -ForegroundColor Green }
function Write-Warn2([string]$Text) { Write-Host ('[警告] ' + $Text) -ForegroundColor Yellow }
function Write-Err2([string]$Text)  { Write-Host ('[错误] ' + $Text) -ForegroundColor Red }

function Format-SizeGB {
    param([double]$Bytes)
    return [math]::Round($Bytes / 1GB, 2)
}

try {
    Write-Section '存图清理（按日期目录）'

    if ($Days -lt 1) {
        Write-Err2 '保留天数必须 >= 1。'
        exit 1
    }
    if (-not (Test-Path -LiteralPath $Root)) {
        Write-Err2 ('存图根目录不存在或不可访问：' + $Root)
        Write-Warn2 'UNC 路径请先在资源管理器确认能打开；断网/共享没开时本脚本无法工作。'
        exit 1
    }

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $cutoff = (Get-Date).Date.AddDays(-$Days)

    Write-Host ('存图根目录 : ' + $rootFull)
    Write-Host ('保留天数   : ' + $Days + ' 天（删除早于 ' + $cutoff.ToString('yyyy-MM-dd') + ' 的日期目录）')
    Write-Host ('执行模式   : ' + $(if ($Execute) { '真删（-Execute）' } else { '预览（不删除，加 -Execute 才执行）' })) -ForegroundColor $(if ($Execute) { 'Yellow' } else { 'Cyan' })

    # ---- 1. 收集日期目录（第一层 + 第二层，兼容有无料号层）----------------
    Write-Step '扫描日期目录'
    $dateDirs = @()
    $firstLevel = @(Get-ChildItem -LiteralPath $rootFull -Directory -ErrorAction SilentlyContinue)
    if ($firstLevel.Count -eq 0) {
        Write-Warn2 '根目录下没有任何子目录，无需清理。'
        exit 0
    }

    foreach ($d in $firstLevel) {
        if ($d.Name -match '^\d{8}$') {
            $dateDirs += $d
        }
        else {
            # 旧模板：第一层是料号，第二层才是日期
            $sub = @(Get-ChildItem -LiteralPath $d.FullName -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d{8}$' })
            if ($sub.Count -gt 0) { $dateDirs += $sub }
        }
    }

    if ($dateDirs.Count -eq 0) {
        Write-Warn2 '没有找到 8 位日期（yyyyMMdd）目录。'
        Write-Warn2 '请确认存图子目录模板里保留了 {日期}，且根目录填的是存图根（不是相机目录/OK 目录）。'
        exit 0
    }

    # ---- 2. 逐目录判断是否过期 --------------------------------------------
    $toDelete = @()
    foreach ($d in $dateDirs) {
        $parsed = [datetime]::MinValue
        $okParse = [datetime]::TryParseExact(
            $d.Name, 'yyyyMMdd',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None,
            [ref]$parsed)
        if (-not $okParse) { continue }
        if ($parsed.Date -ge $cutoff) { continue }

        # 路径安全校验：只允许删 rootFull 下面的目录（防手滑传错根）
        $full = [IO.Path]::GetFullPath($d.FullName)
        if (-not $full.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
            Write-Warn2 ('跳过越界路径：' + $full)
            continue
        }

        $item = [pscustomobject]@{
            Date    = $parsed
            Name    = $d.Name
            Path    = $d.FullName
            SizeGB  = 0
        }
        if ($ShowSize) {
            $sum = (Get-ChildItem -LiteralPath $d.FullName -Recurse -File -ErrorAction SilentlyContinue |
                Measure-Object -Property Length -Sum).Sum
            if ($null -eq $sum) { $sum = 0 }
            $item.SizeGB = Format-SizeGB -Bytes ([double]$sum)
        }
        $toDelete += $item
    }

    if ($toDelete.Count -eq 0) {
        Write-Ok ('没有超过 ' + $Days + ' 天的目录，无需清理。')
        exit 0
    }

    # ---- 3. 列出 / 删除 ----------------------------------------------------
    $toDelete = $toDelete | Sort-Object Date
    Write-Host ''
    Write-Host ('将清理 ' + $toDelete.Count + ' 个日期目录：') -ForegroundColor Yellow
    foreach ($item in $toDelete) {
        $suffix = ''
        if ($ShowSize) { $suffix = '    约 ' + $item.SizeGB + ' GB' }
        Write-Host ('   ' + $item.Name + '    ' + $item.Path + $suffix)
    }

    if (-not $Execute) {
        Write-Host ''
        Write-Host '以上为预览，未删除任何文件。' -ForegroundColor Cyan
        Write-Host '确认无误后加 -Execute 重新运行，例如：' -ForegroundColor Cyan
        Write-Host ('   powershell -ExecutionPolicy Bypass -File .\Clear-OldImages.ps1 -Root "' + $rootFull + '" -Days ' + $Days + ' -Execute') -ForegroundColor Yellow
        exit 0
    }

    # ---- 4. 执行删除 -------------------------------------------------------
    Write-Step '执行删除'
    $okCount = 0
    $failCount = 0
    foreach ($item in $toDelete) {
        try {
            Remove-Item -LiteralPath $item.Path -Recurse -Force -ErrorAction Stop
            $okCount++
            Write-Ok ('已删除 ' + $item.Name)
        }
        catch {
            $failCount++
            Write-Warn2 ('删除失败 ' + $item.Name + '：' + $_.Exception.Message)
        }
    }

    Write-Section '清理完成'
    Write-Host ('成功：' + $okCount + ' 个')
    Write-Host ('失败：' + $failCount + ' 个') -ForegroundColor $(if ($failCount -gt 0) { 'Yellow' } else { 'Green' })
    if ($failCount -gt 0) {
        Write-Warn2 '失败通常是文件被占用/权限不足/网络中断；稍后重跑即可。'
        exit 2
    }
    exit 0
}
catch {
    Write-Host ''
    Write-Err2 ('脚本执行失败：' + $_.Exception.Message)
    Write-Host ('       位置：' + $_.InvocationInfo.PositionMessage) -ForegroundColor DarkGray
    exit 1
}
