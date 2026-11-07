#Requires -Version 5.1
<#
.SYNOPSIS
    源机（视觉机）一键连接目标机的存图共享：保存凭据 + 连通性/写入/测速。

.DESCRIPTION
    目标机跑完 Setup-ImageShare.ps1 后，把“连接信息.txt”拿到源机，运行本脚本：
      1. 检测目标机 445 端口是否通（网络/防火墙问题先在这里暴露）；
      2. 用 cmdkey 把账号密码存进 Windows 凭据管理器（重启后仍有效，不用映射盘符）；
      3. 访问 UNC 共享，写一个测试文件再读回删除，顺便测写入速度；
      4. 读取共享的剩余空间，最后打印软件里该填的路径。

    本脚本不需要管理员权限（凭据存在当前用户下）。但如果视觉软件是“以管理员身份运行”，
    请也用同一个管理员窗口运行本脚本，确保凭据落在同一个用户上下文里。

.PARAMETER Server
    目标机 IP 或主机名，例如 192.168.10.50。
    也接受完整 UNC（\\192.168.10.50\VisionImages），脚本会自动拆分。

.PARAMETER Share
    SMB 共享名，默认 VisionImages。

.PARAMETER User
    目标机上的专用账户，默认 visionshare。

.PARAMETER Password
    账户密码。不传则交互式输入（不会回显）。

.PARAMETER SizeMB
    写入测速的测试文件大小（MB），默认 8。机械盘慢可改成 4，网络好的可改 32。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Connect-ImageShare.ps1 -Server 192.168.10.50 -Password 'Vs@12345678'

.EXAMPLE
    # 交互式（直接双击 Run-Connect.cmd）
    powershell -ExecutionPolicy Bypass -File .\Connect-ImageShare.ps1
#>
[CmdletBinding()]
param(
    [string]$Server = '',
    [string]$Share = 'VisionImages',
    [string]$User = 'visionshare',
    [string]$Password = '',
    [int]$SizeMB = 8
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

# 执行原生命令并返回退出码（stdout/stderr 都不进界面日志）。
#
# $ErrorActionPreference='Stop' 时，原生命令只要往 stderr 写东西（比如 cmdkey 删除
# 一个不存在的凭据），PowerShell 会把它包装成 NativeCommandError 当成终止错误，
# 后面的退出码判断根本执行不到。这里临时降回 Continue，失败与否只看退出码。
function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [string[]]$Arguments = @()
    )
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Exe @Arguments 2>$null | Out-Null
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $old
    }
}

# TCP 端口探测（不用 Test-NetConnection，兼容 Win7/PS5.1）
function Test-TcpPort {
    param([string]$HostName, [int]$Port, [int]$TimeoutMs = 3000)
    $client = New-Object Net.Sockets.TcpClient
    try {
        $iar = $client.BeginConnect($HostName, $Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne($TimeoutMs)) { return $false }
        $client.EndConnect($iar)
        return $true
    }
    catch { return $false }
    finally { try { $client.Close() } catch { } }
}

# 取 UNC 共享的剩余空间（DriveInfo 对 UNC 无效，必须走 Win32 API）
function Get-UncFreeSpace {
    param([string]$UncPath)
    if (-not ('NetDiskApi' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NetDiskApi
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceEx(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);
}
'@
    }
    [uint64]$free = 0
    [uint64]$total = 0
    [uint64]$totalFree = 0
    $ok = [NetDiskApi]::GetDiskFreeSpaceEx($UncPath, [ref]$free, [ref]$total, [ref]$totalFree)
    if (-not $ok) { return $null }
    return [pscustomobject]@{
        FreeGB  = [math]::Round([double]$free / 1GB, 1)
        TotalGB = [math]::Round([double]$total / 1GB, 1)
    }
}

# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
try {
    Write-Section '网络存图 - 源机连接测试'

    # ---- 0. 解析参数 -------------------------------------------------------
    if ($Server -match '^\\\\') {
        $parts = $Server.TrimStart('\').Split('\')
        $Server = $parts[0]
        if ($parts.Count -gt 1 -and -not [string]::IsNullOrWhiteSpace($parts[1])) {
            $Share = $parts[1]
        }
    }

    if ([string]::IsNullOrWhiteSpace($Server)) {
        $Server = (Read-Host '请输入目标机 IP（例如 192.168.10.50）').Trim()
    }
    if ([string]::IsNullOrWhiteSpace($Server)) {
        Write-Err2 '目标机 IP 不能为空。'
        exit 1
    }
    if ([string]::IsNullOrWhiteSpace($Share)) {
        $Share = 'VisionImages'
    }
    if ([string]::IsNullOrWhiteSpace($User)) {
        $User = (Read-Host '请输入共享账号').Trim()
    }
    if ([string]::IsNullOrWhiteSpace($Password)) {
        $sec = Read-Host ('请输入账户 ' + $User + ' 的密码') -AsSecureString
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
        try { $Password = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }
    if ([string]::IsNullOrWhiteSpace($Password)) {
        Write-Err2 '密码不能为空（Windows 默认禁止空密码账户网络登录）。'
        exit 1
    }
    if ($SizeMB -lt 1) { $SizeMB = 8 }
    if ($SizeMB -gt 256) { $SizeMB = 256 }

    $unc = '\\' + $Server + '\' + $Share
    Write-Host ('目标共享：' + $unc)
    Write-Host ('账户    ：' + $User)

    # ---- 1. 网络连通性（445）---------------------------------------------
    Write-Step ('检测目标机 ' + $Server + ':445')
    if (-not (Test-TcpPort -HostName $Server -Port 445 -TimeoutMs 3000)) {
        Write-Err2 ('连不上 ' + $Server + ':445。')
        Write-Warn2 '排查顺序：'
        Write-Warn2 '  1) 两台机器是否在同一局域网/网段，互相 ping 是否通；'
        Write-Warn2 '  2) 目标机防火墙是否放行“文件和打印机共享”/ TCP 445；'
        Write-Warn2 '  3) 目标机是否禁用了 Server 服务（services.msc 里看 LanmanServer）；'
        Write-Warn2 '  4) 目标机 IP 是否变了（建议设静态 IP）。'
        exit 1
    }
    Write-Ok '445 端口可达'

    # ---- 2. 保存凭据 -------------------------------------------------------
    Write-Step '保存 Windows 凭据（cmdkey，当前用户永久有效）'
    # 先删旧凭据：同一个 IP 换过密码/换过目标机时，旧凭据会让新密码一直不生效
    $null = Invoke-Native -Exe 'cmdkey.exe' -Arguments @('/delete:' + $Server)
    $credCode = Invoke-Native -Exe 'cmdkey.exe' -Arguments @(
        '/add:' + $Server, '/user:' + $User, '/pass:' + $Password)
    if ($credCode -ne 0) {
        Write-Err2 ('保存凭据失败（cmdkey 退出码 ' + $credCode + '）。')
        exit 1
    }
    Write-Ok '凭据已保存'

    # ---- 3. 访问共享 -------------------------------------------------------
    Write-Step '访问共享目录'
    if (-not (Test-Path $unc)) {
        Write-Err2 ('能连上端口，但访问 ' + $unc + ' 失败。')
        Write-Warn2 '常见原因：共享名拼错、目标机共享被删、账号密码不对、NTFS/共享权限没给。'
        Write-Warn2 '可在源机资源管理器地址栏直接输入上面的 UNC 验证，或回目标机重跑 Run-Repair.cmd。'
        exit 1
    }
    Write-Ok '共享目录可访问'

    # ---- 4. 写入/读取/测速 -------------------------------------------------
    Write-Step ('写入测试：' + $SizeMB + ' MB')
    $testFile = Join-Path $unc '.visionshare_write_test.bin'
    $bytes = New-Object byte[] -ArgumentList ([int]($SizeMB * 1MB))
    try {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        [IO.File]::WriteAllBytes($testFile, $bytes)
        $sw.Stop()

        $written = (Get-Item $testFile).Length
        if ($written -ne $bytes.Length) {
            throw ('写入长度不一致：期望 ' + $bytes.Length + '，实际 ' + $written)
        }

        $readBack = [IO.File]::ReadAllBytes($testFile)
        if ($readBack.Length -ne $bytes.Length) {
            throw '读回长度不一致'
        }
        Remove-Item $testFile -Force

        $speed = 0
        if ($sw.Elapsed.TotalSeconds -gt 0) {
            $speed = [math]::Round($SizeMB / $sw.Elapsed.TotalSeconds, 1)
        }
        Write-Ok ('写入/读取/删除正常，顺序写入约 ' + $speed + ' MB/s（' + $sw.Elapsed.TotalSeconds.ToString('0.00') + ' 秒）')
        if ($speed -lt 10) {
            Write-Warn2 '速度低于 10 MB/s：检查是否百兆网络/网线质量/机械盘状态；高节拍存图可能跟不上。'
        }
    }
    catch {
        if (Test-Path $testFile) { Remove-Item $testFile -Force -ErrorAction SilentlyContinue }
        Write-Err2 ('写入测试失败：' + $_.Exception.Message)
        Write-Warn2 '能访问目录但写不进去，通常是 NTFS 权限只给了“读取”，或目标盘满了/只读。'
        exit 1
    }

    # ---- 5. 剩余空间 -------------------------------------------------------
    Write-Step '读取共享剩余空间'
    $space = Get-UncFreeSpace -UncPath $unc
    if ($space) {
        Write-Ok ('剩余 ' + $space.FreeGB + ' GB / 总容量 ' + $space.TotalGB + ' GB')
        if ($space.FreeGB -lt 50) {
            Write-Warn2 '剩余空间不足 50 GB，建议尽快换盘或清理旧图。'
        }
    }
    else {
        Write-Warn2 '剩余空间读取失败（不影响使用，可能是权限限制）。'
    }

    # ---- 6. 结果 -----------------------------------------------------------
    Write-Section '连接成功'
    Write-Host '视觉软件里的“保存路径”请填：' -ForegroundColor Green
    Write-Host ('   ' + $unc) -ForegroundColor Yellow
    Write-Host ''
    Write-Host '提示：' -ForegroundColor Cyan
    Write-Host '  1. 软件里每个相机都要填一次（或后续版本用“应用到所有相机”）。'
    Write-Host '  2. 不要用映射盘符（Z: 之类），UNC 直连更稳，重启不掉。'
    Write-Host '  3. 换盘后目标机跑一次 Run-Repair.cmd，源机不用重新配置。'
    Write-Host ''
    exit 0
}
catch {
    Write-Host ''
    Write-Err2 ('脚本执行失败：' + $_.Exception.Message)
    Write-Host ('       位置：' + $_.InvocationInfo.PositionMessage) -ForegroundColor DarkGray
    exit 1
}
