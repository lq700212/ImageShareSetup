#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    目标机（存图机）一键配置 SMB 存图共享；换盘后加 -Repair 一键修复。

.DESCRIPTION
    本脚本只做“让另一台工控机能把图片写进本机机械盘”这一件事，现场不需要懂共享/权限：
      1. 自动找数据盘（优先按卷标 VISION_DATA，其次按盘符 D:，只有一块数据盘时直接用它）；
      2. 把盘符固定成目标盘符（默认 D:），源机路径才不会因为盘符漂移而失效；
      3. 建存图目录、建专用本地账户、设 NTFS 权限、建 SMB 共享、放行 445 端口、关睡眠；
      4. 输出“连接信息.txt”（含共享路径/账号/密码），拷到源机即可用。

    换盘后（客户把机械盘拔走换新盘）只需右键管理员运行 Run-Repair.cmd：
    盘符会自动纠回 D:、目录和权限会重建、共享会校正，账号密码不动。

.PARAMETER ShareName
    SMB 共享名，默认 VisionImages。源机路径形如 \\192.168.10.50\VisionImages。

.PARAMETER VolumeLabel
    数据盘卷标，默认 VISION_DATA。换盘时按卷标认盘最稳，建议每块数据盘都设成这个卷标。

.PARAMETER DriveLetter
    数据盘要固定成的盘符，默认 D。不能与系统盘相同。

.PARAMETER FolderName
    存图根目录名，默认 VisionImages。最终目录 = D:\VisionImages。

.PARAMETER User
    源机访问用的专用本地账户，默认 visionshare。脚本会创建该账户并只给它存图目录的修改权限。

.PARAMETER Password
    账户密码。首次配置不传则自动生成 14 位随机密码并写进“连接信息.txt”。
    账户已存在且不传密码时沿用原密码，不会重置。

.PARAMETER DataDrive
    直接指定存图盘“当前”的盘符（例如 D 或 E），跳过“按卷标找盘”的自动判断。
    界面（ImageShareSetup.exe）走这个参数：界面上选了哪块盘，脚本就用哪块盘。
    不传则按“卷标 > 目标盘符 > 唯一数据盘”的优先级自动找。

.PARAMETER BindIp
    本机有多网卡时，手工指定源机该用的那个 IP（写进连接信息.txt），不传则列出全部网卡 IP。

.PARAMETER InfoFile
    “连接信息.txt”的完整输出路径（含共享路径/账号/密码）。
    界面会传 exe 所在目录，保证现场能直接找到；不传则写在脚本自己所在目录。

.PARAMETER Repair
    换盘修复模式：不创建账户、不改密码、不改防火墙/电源，只修盘符/目录/权限/共享。

.PARAMETER RecursiveAcl
    递归重设已有文件的 NTFS 权限（目录里已有大量历史图时慎用，会很慢）。
    首次配置新盘不需要；如果新盘是从别的机器拿来的、旧文件写不进去，再加这个开关。

.PARAMETER DisableHibernate
    同时关闭系统休眠（powercfg /h off，会删除 hiberfil.sys，释放几个 GB）。

.EXAMPLE
    # 首次配置（右键“以管理员身份运行”，或直接双击 Run-Setup.cmd）
    powershell -ExecutionPolicy Bypass -File .\Setup-ImageShare.ps1

.EXAMPLE
    # 指定盘符/卷标/密码
    powershell -ExecutionPolicy Bypass -File .\Setup-ImageShare.ps1 -DriveLetter E -VolumeLabel VISION_DATA -Password 'Vs@12345678'

.EXAMPLE
    # 换盘后修复（等价于双击 Run-Repair.cmd）
    powershell -ExecutionPolicy Bypass -File .\Setup-ImageShare.ps1 -Repair
#>
[CmdletBinding()]
param(
    [string]$ShareName   = 'VisionImages',
    [string]$VolumeLabel = 'VISION_DATA',
    [string]$DriveLetter = 'D',
    [string]$FolderName  = 'VisionImages',
    [string]$User        = 'visionshare',
    [string]$Password    = '',
    [string]$InfoFile    = '',
    [string]$BindIp      = '',
    [string]$DataDrive   = '',
    [switch]$Repair,
    [switch]$RecursiveAcl,
    [switch]$DisableHibernate
)

$ErrorActionPreference = 'Stop'
$script:PasswordWasGenerated = $false
$script:PasswordWasProvided  = -not [string]::IsNullOrWhiteSpace($Password)

# ---------------------------------------------------------------------------
# 输出小工具（统一前缀，现场看日志一眼能分清步骤/成功/警告/错误）
# ---------------------------------------------------------------------------
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

# ---------------------------------------------------------------------------
# 工具函数
# ---------------------------------------------------------------------------

# 枚举本机固定 NTFS 盘（排除 U 盘/光驱/未格式化盘）
function Get-FixedVolumes {
    $list = @()
    if (Get-Command Get-Volume -ErrorAction SilentlyContinue) {
        Get-Volume -ErrorAction SilentlyContinue | Where-Object {
            $_.DriveType -eq 'Fixed' -and $_.DriveLetter -and $_.FileSystem -eq 'NTFS'
        } | ForEach-Object {
            $list += [pscustomobject]@{
                Letter = ([string]$_.DriveLetter).ToUpper()
                Label  = [string]$_.FileSystemLabel
                SizeGB = [math]::Round([double]$_.Size / 1GB, 1)
            }
        }
    }
    else {
        # Win7/老系统兜底：WMI 查固定盘
        Get-WmiObject Win32_LogicalDisk -Filter 'DriveType=3' -ErrorAction SilentlyContinue |
            Where-Object { $_.FileSystem -eq 'NTFS' } | ForEach-Object {
                $list += [pscustomobject]@{
                    Letter = $_.DeviceID.Substring(0, 1).ToUpper()
                    Label  = [string]$_.VolumeName
                    SizeGB = [math]::Round([double]$_.Size / 1GB, 1)
                }
            }
    }
    return $list
}

# 数据盘如果处于“脱机/只读”（新插的盘很常见），先联机再操作
function Enable-DiskOnline {
    param([string]$Letter)
    if (-not (Get-Command Get-Partition -ErrorAction SilentlyContinue)) { return }
    try {
        $part = Get-Partition -DriveLetter $Letter -ErrorAction Stop
        $disk = Get-Disk -Number $part.DiskNumber -ErrorAction Stop
        if ($disk.IsOffline) {
            Set-Disk -Number $disk.Number -IsOffline $false -ErrorAction Stop
            Write-Ok '数据盘原为脱机状态，已联机'
        }
        if ($disk.IsReadOnly) {
            Set-Disk -Number $disk.Number -IsReadOnly $false -ErrorAction Stop
            Write-Ok '数据盘原为只读状态，已取消只读'
        }
    }
    catch {
        Write-Warn2 ('检查磁盘联机状态失败（可忽略）：' + $_.Exception.Message)
    }
}

# 把数据盘盘符固定成目标盘符；成功返回“实际使用的盘符”（单字母），失败返回 $null。
#
# 为什么不再“目标盘符被占就退出”：源机保存路径是 \\IP\共享名，跟盘符无关，盘符只决定
# 本机目录长什么样。现场典型场景是“先拿出厂盘 D: 顶着，以后再插真正的机械盘”——新盘
# 插上后被 Windows 分到 E:，此时 D: 还占用着，硬报错等于换盘必须手工改磁盘管理。
# 现在的口径：目标盘符空闲就改过去（目录整齐）；被占用就保留当前盘符继续，只把共享
# 重新指向新盘，源机（视觉机）一行配置都不用改。
function Ensure-DriveLetter {
    param([string]$Current, [string]$Target)
    $cur = $Current.ToUpper()
    $tgt = $Target.ToUpper()
    if ($cur -eq $tgt) { return $cur }

    if (Test-Path ($tgt + ':\')) {
        Write-Warn2 ('目标盘符 ' + $tgt + ': 已被其它磁盘占用，保留数据盘当前盘符 ' + $cur + ':。')
        Write-Warn2 ('共享名不变，源机（视觉机）的保存路径不受影响；本机目录会变成 ' + $cur + ':\' + $FolderName + '。')
        return $cur
    }

    Write-Step ('调整盘符：' + $cur + ': -> ' + $tgt + ':（目录更整齐，源机路径不受影响）')
    try {
        if (Get-Command Set-Partition -ErrorAction SilentlyContinue) {
            # 必须 Out-Null：Set-Partition 成功时会往管道吐分区对象，污染函数返回值
            Set-Partition -DriveLetter $cur -NewDriveLetter $tgt -ErrorAction Stop | Out-Null
        }
        else {
            $scriptFile = [IO.Path]::GetTempFileName()
            $cmds = @("select volume $cur", "assign letter=$tgt", "exit")
            [IO.File]::WriteAllLines($scriptFile, $cmds, [Text.Encoding]::ASCII)
            & diskpart.exe /s $scriptFile | Out-Null
            Remove-Item $scriptFile -Force -ErrorAction SilentlyContinue
            if (-not (Test-Path ($tgt + ':\'))) { throw 'diskpart 改盘符失败' }
        }
        Start-Sleep -Milliseconds 800
        Write-Ok ('盘符已固定为 ' + $tgt + ':')
        return $tgt
    }
    catch {
        # 改盘符失败不算致命：当前盘符照用，共享指向它即可（源机路径不变）
        Write-Warn2 ('改盘符失败（' + $_.Exception.Message + '），保留当前盘符 ' + $cur + ': 继续。')
        return $cur
    }
}

# 执行原生命令并返回退出码（stdout/stderr 都不进界面日志）。
#
# 为什么不用 `& net.exe ... 2>$null`：
#   $ErrorActionPreference='Stop' 时，原生命令只要往 stderr 写东西（最典型的就是
#   `net user 不存在的账户` 输出 "The user name could not be found."），
#   PowerShell 会把它包装成 NativeCommandError 当成终止错误，直接把脚本打断，
#   连后面的 if ($LASTEXITCODE) 都执行不到。2>$null 只改了显示，不改这个行为。
#   这里临时把 ErrorActionPreference 降回 Continue，命令失败与否一律交给退出码判断。
#
# 参数用数组显式传入（-Arguments @(...)），避免密码等值以 '-' 开头时被当成参数名。
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

# 本地账户是否存在（用 net user 兼容 Win7/10/11）
function Test-LocalUser {
    param([string]$Name)
    $code = Invoke-Native -Exe 'net.exe' -Arguments @('user', $Name)
    return ($code -eq 0)
}

# 生成满足 Windows 密码复杂度的随机密码（去掉 0/O/1/l/I 等易混字符；@ 保证有符号）
function New-RandomPassword {
    $chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = New-Object byte[] 10
    $rng.GetBytes($bytes)
    $sb = New-Object Text.StringBuilder
    foreach ($b in $bytes) { [void]$sb.Append($chars[$b % $chars.Length]) }
    $rng.Dispose()
    return 'Vs@' + $sb.ToString()
}

# 取本机所有可用 IPv4（源机要填与它同网段的那个）
function Get-IPv4List {
    $ips = @()
    if (Get-Command Get-NetIPAddress -ErrorAction SilentlyContinue) {
        Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
            $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*'
        } | ForEach-Object {
            $adapter = ''
            try {
                $a = Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue
                if ($a) { $adapter = $a.Name }
            }
            catch { }
            $ips += [pscustomobject]@{ IP = $_.IPAddress; Adapter = $adapter }
        }
    }
    else {
        Get-WmiObject Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True' -ErrorAction SilentlyContinue | ForEach-Object {
            foreach ($ip in $_.IPAddress) {
                if ($ip -notlike '*:*' -and $ip -notlike '127.*' -and $ip -notlike '169.254.*') {
                    $ips += [pscustomobject]@{ IP = $ip; Adapter = [string]$_.Description }
                }
            }
        }
    }
    return $ips
}

# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
try {
    Write-Section ('网络存图 - 目标机' + $(if ($Repair) { '换盘修复' } else { '首次配置' }))

    # ---- 0. 参数校验 -------------------------------------------------------
    if ($ShareName -notmatch '^[A-Za-z0-9._-]{1,80}$') {
        Write-Err2 '共享名只能包含字母/数字/点/下划线/横线（1~80 字符）。'
        exit 1
    }
    if ($User -notmatch '^[A-Za-z0-9._-]{1,20}$') {
        Write-Err2 '账户名只能包含字母/数字/点/下划线/横线（1~20 字符）。'
        exit 1
    }
    $DriveLetter = $DriveLetter.TrimEnd(':').ToUpper()
    if ($DriveLetter -notmatch '^[A-Z]$') {
        Write-Err2 '盘符必须是单个字母，例如 D。'
        exit 1
    }
    $sysLetter = $env:SystemDrive.Substring(0, 1).ToUpper()
    if ($DriveLetter -eq $sysLetter) {
        Write-Err2 '数据盘盘符不能与系统盘相同（系统盘是 ' + $sysLetter + ':）。'
        exit 1
    }

    # ---- 1. 找数据盘 -------------------------------------------------------
    if ([string]::IsNullOrWhiteSpace($DataDrive)) {
        Write-Step '查找数据盘（优先按卷标，其次按盘符）'
    }
    else {
        Write-Step ('查找数据盘（界面指定：' + $DataDrive.TrimEnd(':').ToUpper() + ':）')
    }
    $vols = @(Get-FixedVolumes | Where-Object { $_.Letter -ne $sysLetter })
    if ($vols.Count -eq 0) {
        Write-Err2 '没有找到可用的非系统 NTFS 数据盘。'
        Write-Warn2 '请先插好机械盘，在“磁盘管理”里初始化并格式化 NTFS 后再运行。'
        exit 1
    }

    $vol = $null
    if (-not [string]::IsNullOrWhiteSpace($DataDrive)) {
        # 界面已经让用户明确选了哪块盘，直接用，不再猜（猜错会把图写到别的盘上）
        $want = $DataDrive.TrimEnd(':').ToUpper()
        $vol = $vols | Where-Object { $_.Letter -eq $want } | Select-Object -First 1
        if (-not $vol) {
            Write-Err2 ('指定的存图盘 ' + $want + ': 不可用（必须是已格式化为 NTFS 的固定盘，且不能是系统盘）。')
            exit 1
        }
        $labelText = $vol.Label
        if ([string]::IsNullOrWhiteSpace($labelText)) { $labelText = '无卷标' }
        Write-Ok ('使用指定的存图盘：' + $vol.Letter + ':（卷标 ' + $labelText + '，' + $vol.SizeGB + ' GB）')
    }
    if (-not $vol) {
        $vol = $vols | Where-Object { $_.Label -eq $VolumeLabel } | Select-Object -First 1
        if ($vol) {
            Write-Ok ('按卷标找到数据盘：' + $vol.Letter + ':（卷标 ' + $vol.Label + '，' + $vol.SizeGB + ' GB）')
        }
    }
    if (-not $vol) {
        $vol = $vols | Where-Object { $_.Letter -eq $DriveLetter } | Select-Object -First 1
        if ($vol) {
            Write-Warn2 ('未找到卷标 [' + $VolumeLabel + ']，但盘符 ' + $vol.Letter + ': 存在，按盘符继续。')
            Write-Warn2 ('建议把该盘卷标改成 ' + $VolumeLabel + '，以后换盘脚本才能自动认盘。')
        }
    }
    if (-not $vol) {
        if ($vols.Count -eq 1) {
            $vol = $vols[0]
            Write-Warn2 ('未找到卷标 [' + $VolumeLabel + ']，自动使用唯一的数据盘 ' + $vol.Letter + ':（' + $vol.SizeGB + ' GB）。')
            Write-Warn2 ('建议把该盘卷标改成 ' + $VolumeLabel + '。')
        }
        else {
            Write-Warn2 '检测到多块数据盘，请选择存图盘：'
            for ($i = 0; $i -lt $vols.Count; $i++) {
                Write-Host ('   [' + ($i + 1) + '] ' + $vols[$i].Letter + ':  卷标=' + $vols[$i].Label + '  容量=' + $vols[$i].SizeGB + ' GB')
            }
            $sel = Read-Host ('请输入序号（默认 1；建议给存图盘设置卷标 ' + $VolumeLabel + '）')
            if ($null -eq $sel) { $sel = '1' }
            $sel = $sel.Trim()
            if ([string]::IsNullOrWhiteSpace($sel)) { $sel = '1' }
            $idx = 0
            if (-not [int]::TryParse($sel, [ref]$idx) -or $idx -lt 1 -or $idx -gt $vols.Count) {
                Write-Err2 '序号无效。'
                exit 1
            }
            $vol = $vols[$idx - 1]
        }
    }

    # ---- 2. 联机 + 固定盘符 ------------------------------------------------
    Enable-DiskOnline -Letter $vol.Letter
    $actualLetter = Ensure-DriveLetter -Current $vol.Letter -Target $DriveLetter
    if ([string]::IsNullOrEmpty($actualLetter)) { exit 1 }

    $dir = $actualLetter + ':\' + $FolderName

    # ---- 3. 建目录 ---------------------------------------------------------
    Write-Step ('创建存图目录：' + $dir)
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    if (-not (Test-Path $dir)) {
        Write-Err2 ('目录创建失败：' + $dir + '（检查磁盘是否只读/损坏）')
        exit 1
    }
    Write-Ok '目录就绪'

    # ---- 4. 账户 -----------------------------------------------------------
    Write-Step ('检查本地账户：' + $User)
    $userExists = Test-LocalUser -Name $User

    if ($Repair) {
        if (-not $userExists) {
            Write-Err2 ('账户 ' + $User + ' 不存在，请先运行一次首次配置（不带 -Repair）。')
            exit 1
        }
        Write-Ok '账户存在（换盘修复不修改密码）'
    }
    else {
        if (-not $userExists) {
            if ([string]::IsNullOrWhiteSpace($Password)) {
                $Password = New-RandomPassword
                $script:PasswordWasGenerated = $true
            }
            $addCode = Invoke-Native -Exe 'net.exe' -Arguments @(
                'user', $User, $Password, '/add',
                '/comment:Image share service account', '/expires:never', '/passwordchg:no')
            if ($addCode -ne 0) {
                Write-Err2 ('创建账户失败（net user 退出码 ' + $addCode + '）。密码必须满足本机密码策略。')
                exit 1
            }
            Write-Ok ('已创建专用账户：' + $User)
        }
        else {
            if ([string]::IsNullOrWhiteSpace($Password)) {
                Write-Warn2 ('账户 ' + $User + ' 已存在，沿用原密码（不重置）。')
                Write-Warn2 ('如果忘记原密码，加 -Password 新密码 重新运行即可重置。')
            }
            else {
                $pwdCode = Invoke-Native -Exe 'net.exe' -Arguments @('user', $User, $Password)
                if ($pwdCode -ne 0) {
                    Write-Err2 '重置账户密码失败（密码必须满足本机密码策略）。'
                    exit 1
                }
                Write-Ok '账户密码已重置'
            }
        }
    }

    # 账户密码永不过期：本地策略默认可能 42 天强制改密，工控机没人会去改，到期后网络登录直接失败
    $null = Invoke-Native -Exe 'net.exe' -Arguments @('user', $User, '/maxpwage:unlimited')

    # ---- 5. NTFS 权限 ------------------------------------------------------
    # 管理员/系统给完全控制（防止磁盘来自别的机器时 ACL 缺失）；
    # 共享账户只给“修改”（能读写删，不能改权限）。
    Write-Step '设置目录权限'
    $null = Invoke-Native -Exe 'icacls.exe' -Arguments @($dir, '/grant', '*S-1-5-32-544:(OI)(CI)F', '/C')
    $null = Invoke-Native -Exe 'icacls.exe' -Arguments @($dir, '/grant', '*S-1-5-18:(OI)(CI)F', '/C')
    $aclArgs = @($dir, '/grant', ($User + ':(OI)(CI)M'), '/C')
    if ($RecursiveAcl) { $aclArgs += '/T' }
    $aclCode = Invoke-Native -Exe 'icacls.exe' -Arguments $aclArgs
    if ($aclCode -ne 0) {
        Write-Warn2 '设置目录权限返回非零，请检查目录是否可用/账户是否存在。'
    }
    else {
        Write-Ok '目录权限就绪'
    }

    # ---- 6. SMB 共享 -------------------------------------------------------
    Write-Step ('创建/校正 SMB 共享：' + $ShareName)

    # SMB 服务端（LanmanServer）被停用/禁用时共享建了也访问不了，先确保它在跑
    $lanman = Get-Service -Name LanmanServer -ErrorAction SilentlyContinue
    if ($lanman -and $lanman.Status -ne 'Running') {
        try {
            Start-Service -Name LanmanServer -ErrorAction Stop
            Write-Ok 'Server 服务（LanmanServer）已启动'
        }
        catch {
            Write-Warn2 ('Server 服务启动失败：' + $_.Exception.Message + '（共享可能无法访问）')
        }
    }

    if (Get-Command Get-SmbShare -ErrorAction SilentlyContinue) {
        $shareObj = Get-SmbShare -Name $ShareName -ErrorAction SilentlyContinue
        if ($shareObj -and $shareObj.Path.TrimEnd('\') -ieq $dir.TrimEnd('\')) {
            Write-Ok ('共享已存在且指向 ' + $dir + '，无需改动')
        }
        else {
            if ($shareObj) {
                Write-Warn2 ('旧共享指向 ' + $shareObj.Path + '（盘符可能变过），删除后按新路径重建。')
                Remove-SmbShare -Name $ShareName -Force -ErrorAction Stop
            }
            New-SmbShare -Name $ShareName -Path $dir -FullAccess $User -Description 'Image share storage' -ErrorAction Stop | Out-Null
            Write-Ok ('共享已创建：' + $ShareName + ' -> ' + $dir)
        }
    }
    else {
        # Win7/家庭版兜底：net share
        $null = Invoke-Native -Exe 'net.exe' -Arguments @('share', $ShareName, '/delete', '/y')
        $shareCode = Invoke-Native -Exe 'net.exe' -Arguments @(
            'share', ($ShareName + '=' + $dir),
            ('/grant:' + $User + ',FULL'), '/remark:Image share storage')
        if ($shareCode -ne 0) {
            Write-Err2 '创建共享失败（net share 返回非零）。'
            exit 1
        }
        Write-Ok ('共享已创建：' + $ShareName + ' -> ' + $dir)
    }

    # ---- 7. 防火墙 / 电源（换盘修复不需要重复做）--------------------------
    if (-not $Repair) {
        Write-Step '放行防火墙 TCP 445（SMB 访问入口）'
        $null = Invoke-Native -Exe 'netsh.exe' -Arguments @('advfirewall', 'firewall', 'delete', 'rule', 'name=ImageShare-SMB445')
        $fwCode = Invoke-Native -Exe 'netsh.exe' -Arguments @(
            'advfirewall', 'firewall', 'add', 'rule', 'name=ImageShare-SMB445',
            'dir=in', 'action=allow', 'protocol=TCP', 'localport=445', 'profile=any')
        if ($fwCode -eq 0) {
            Write-Ok '防火墙规则已添加（TCP 445 入站，所有网络配置）'
        }
        else {
            Write-Warn2 '防火墙规则添加失败，请手工放行“文件和打印机共享”。'
        }

        Write-Step '电源设置：关闭睡眠/硬盘休眠（避免网络存储掉线）'
        $null = Invoke-Native -Exe 'powercfg.exe' -Arguments @('/change', 'standby-timeout-ac', '0')
        $null = Invoke-Native -Exe 'powercfg.exe' -Arguments @('/change', 'disk-timeout-ac', '0')
        $null = Invoke-Native -Exe 'powercfg.exe' -Arguments @('/change', 'hibernate-timeout-ac', '0')
        if ($DisableHibernate) {
            $null = Invoke-Native -Exe 'powercfg.exe' -Arguments @('/h', 'off')
            Write-Ok '已关闭系统休眠'
        }
        Write-Ok '电源设置完成'
    }

    # ---- 8. 本地写读自检 ---------------------------------------------------
    Write-Step '本地写入自检'
    $testFile = Join-Path $dir '.visionshare_selftest.tmp'
    try {
        [IO.File]::WriteAllText($testFile, 'ok ' + (Get-Date).ToString('s'))
        $content = [IO.File]::ReadAllText($testFile)
        Remove-Item $testFile -Force -ErrorAction SilentlyContinue
        if ([string]::IsNullOrEmpty($content)) { throw '读回内容为空' }
        Write-Ok '写入/读取/删除正常'
    }
    catch {
        Write-Err2 ('本地写入失败：' + $_.Exception.Message)
        exit 1
    }

    # ---- 9. 网络信息 + 连接信息文件 ---------------------------------------
    Write-Step '收集网络信息'
    $ips = @(Get-IPv4List)
    if (-not [string]::IsNullOrWhiteSpace($BindIp)) {
        $ips = @([pscustomobject]@{ IP = $BindIp; Adapter = '（手工指定）' })
    }
    if ($ips.Count -eq 0) {
        Write-Warn2 '没有取到本机 IPv4 地址，请手工用 ipconfig 查看。'
    }

    if (-not $Repair) {
        # 内嵌脚本运行在 %TEMP%，写到 $PSScriptRoot 现场找不到；
        # 界面会通过 -InfoFile 指定成 exe 所在目录，直接跑脚本时仍回退到脚本目录。
        $outFile = if ([string]::IsNullOrWhiteSpace($InfoFile)) {
            Join-Path $PSScriptRoot '连接信息.txt'
        }
        else {
            $InfoFile
        }
        [string[]]$lines = @()
        $lines += '===== 网络存图连接信息 ====='
        $lines += '生成时间：' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        $lines += '目标机  ：' + $env:COMPUTERNAME
        $lines += ''
        $lines += '共享路径（软件里填这个）：'
        if ($ips.Count -gt 0) {
            foreach ($ip in $ips) {
                $lines += '  \\' + $ip.IP + '\' + $ShareName + '    （网卡：' + $ip.Adapter + '）'
            }
        }
        else {
            $lines += '  \\<本机IP>\' + $ShareName
        }
        $lines += ''
        $lines += '账户：' + $User
        if ($script:PasswordWasGenerated -or $script:PasswordWasProvided) {
            $lines += '密码：' + $Password
        }
        else {
            $lines += '密码：（沿用原有密码，未修改）'
        }
        $lines += ''
        $lines += '使用方法：'
        $lines += '  1. 把本文件拷到视觉机（源机）。'
        $lines += '  2. 在源机运行 Connect-ImageShare.ps1，按提示输入上面的 IP / 账号 / 密码；'
        $lines += '  3. 或者在视觉软件“图像保存设定”里，把保存路径填成上面的共享路径。'
        $lines += ''
        $lines += '安全提示：本文件含明文密码，配置完成后请删除。'
        try {
            [IO.File]::WriteAllLines($outFile, $lines, (New-Object Text.UTF8Encoding($true)))
            Write-Ok ('连接信息已写入：' + $outFile)
        }
        catch {
            # 脚本目录只读（U 盘写保护/光盘）时不算配置失败：共享已经建好了，手工抄输出即可
            Write-Warn2 ('连接信息文件写入失败：' + $_.Exception.Message)
            Write-Warn2 '共享已配置成功，请手工记下上面的“共享路径/账户/密码”。'
        }
    }

    # ---- 10. 摘要 ----------------------------------------------------------
    Write-Section '完成'
    Write-Host ('共享名   : ' + $ShareName)
    Write-Host ('本地目录 : ' + $dir)
    if ($ips.Count -gt 0) {
        foreach ($ip in $ips) {
            Write-Host ('共享路径 : \\' + $ip.IP + '\' + $ShareName + '    （网卡：' + $ip.Adapter + '）') -ForegroundColor Yellow
        }
    }
    if (-not $Repair) {
        Write-Host ('账户     : ' + $User)
    }
    Write-Host ''
    Write-Host '下一步：把上面“共享路径”填进视觉软件各相机的“保存路径”。' -ForegroundColor Yellow
    Write-Host '源机可用 Connect-ImageShare.ps1 自动保存凭据并测试。' -ForegroundColor Yellow
    Write-Host ''
    exit 0
}
catch {
    Write-Host ''
    Write-Err2 ('脚本执行失败：' + $_.Exception.Message)
    Write-Host ('       位置：' + $_.InvocationInfo.PositionMessage) -ForegroundColor DarkGray
    exit 1
}
