param(
    [int]$Port = 5080,
    [switch]$Publish,
    [string]$ImportHar = '',
    [switch]$CheckSession
)

$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $dir
try {
    $cfg = Join-Path $dir 'appsettings.json'
    $raw = Get-Content $cfg -Raw
    if ($raw -match 'CHANGE-ME-PLEASE') {
        Write-Host '[!] 请先编辑 appsettings.json，把 DoorApi:ApiKey 改成自己的密钥' -ForegroundColor Yellow
    }
    $cfgObj = $raw | ConvertFrom-Json
    $mode = $cfgObj.DoorApi.Mode
    Write-Host "[*] 当前模式: $mode" -ForegroundColor Cyan
    if ($mode -eq 'Login') {
        Write-Host "    自主登录，无需抓包；DeviceGuid = $($cfgObj.DoorApi.DeviceGuid)" -ForegroundColor Gray
    }

    # 一键导入 HAR 抓包（解析 + 无副作用验活 + 写配置，会切回 Replay 兜底模式）
    if ($ImportHar) {
        Write-Host "[*] 导入抓包: $ImportHar" -ForegroundColor Cyan
        python3 (Join-Path $dir 'tools\import_har.py') $ImportHar
        if ($LASTEXITCODE -ne 0) {
            Write-Host '[!] 抓包导入未成功，请按提示重新抓包后再试' -ForegroundColor Yellow
        }
    }

    if ($CheckSession) {
        Write-Host '[*] 会话验活（无副作用，不会开门）:' -ForegroundColor Cyan
        python3 (Join-Path $dir 'tools\import_har.py') --check-session
    }

    $ip = (Get-NetIPAddress -AddressFamily IPv4 |
           Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
           Select-Object -First 1).IPAddress
    if ($ip) { Write-Host "[*] 手机访问: http://$ip`:$Port" -ForegroundColor Cyan }

    if ($Publish) {
        dotnet publish -c Release -o (Join-Path $dir 'publish')
        & (Join-Path $dir 'publish\GuangmingDoorApi.exe') --urls "http://0.0.0.0:$Port"
    } else {
        dotnet run --urls "http://0.0.0.0:$Port"
    }
} finally {
    Pop-Location
}
