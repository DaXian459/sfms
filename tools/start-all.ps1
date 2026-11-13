<#
.SYNOPSIS
  SFMS 一键启动：便携 PostgreSQL → 双协议模拟器 → 采集服务与 API（可选 Vite 前端）。
.DESCRIPTION
  依赖检查会先确认 .NET SDK 8、PostgreSQL 可执行文件、Python venv 是否就绪；
  任一环节失败都会给出修复命令，不会静默继续。
.EXAMPLE
  .\tools\start-all.ps1              # 仅后端（API 同时托管已构建的前端）
  .\tools\start-all.ps1 -Dev         # 额外启动 Vite 开发服务器（5173 热更新）
  .\tools\start-all.ps1 -SkipSim     # 不启动模拟器（对接真实 PLC 时）
  .\tools\start-all.ps1 -Build       # 启动前先 dotnet build + 前端构建
#>
[CmdletBinding()]
param(
  [switch]$Dev,
  [switch]$SkipSim,
  [switch]$Build,
  [int]$ApiPort = 5080,
  [int]$PgPort = 5433
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Write-Step($msg) { Write-Host "`n=== $msg" -ForegroundColor Cyan }
function Write-Ok($msg) { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "  [!] $msg" -ForegroundColor Yellow }
function Write-Err($msg) { Write-Host "  [X] $msg" -ForegroundColor Red }

$runDir = Join-Path $root '.run'
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

# ---------------------------------------------------------------- 依赖检查
Write-Step '检查依赖'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
if (-not $dotnet) {
  Write-Err '.NET SDK 未安装。请把 SDK 解压到 %USERPROFILE%\.dotnet 后重开终端，或执行：'
  Write-Host '  winget install Microsoft.DotNet.SDK.8' -ForegroundColor DarkGray
  exit 1
}
$sdks = & dotnet --list-sdks 2>$null
if (-not ($sdks -match '^8\.')) {
  Write-Err "未找到 .NET 8 SDK，当前：$($sdks -join '; ')"
  exit 1
}
Write-Ok ".NET SDK：$((($sdks | Where-Object { $_ -match '^8\.' }) -split '\[')[0].Trim())"

$pgBin = Join-Path $root 'tools\db\pgsql\bin'
if (-not (Test-Path (Join-Path $pgBin 'pg_ctl.exe'))) {
  Write-Err "未找到便携 PostgreSQL：$pgBin"
  Write-Host '  请先执行 .\tools\db\setup-pg.ps1 下载并初始化数据库' -ForegroundColor DarkGray
  exit 1
}
Write-Ok "PostgreSQL：$pgBin"

if (-not $SkipSim) {
  $py = Join-Path $root 'tools\sim\.venv\Scripts\python.exe'
  if (-not (Test-Path $py)) {
    Write-Err '模拟器 venv 不存在，请执行：python -m venv tools\sim\.venv; tools\sim\.venv\Scripts\pip install -r tools\sim\requirements.txt'
    exit 1
  }
  Write-Ok "模拟器解释器：$py"
}

if ($Build) {
  Write-Step '构建后端与前端'
  & dotnet build -c Release --nologo | Out-Host
  if ($LASTEXITCODE -ne 0) { Write-Err 'dotnet build 失败'; exit 1 }
  Push-Location (Join-Path $root 'web')
  & npm run build | Out-Host
  Pop-Location
  Write-Ok '构建完成'
}

# ---------------------------------------------------------------- 1. PostgreSQL
Write-Step "启动 PostgreSQL（端口 $PgPort）"
$pgData = Join-Path $root '.pgdata'
$pgLog = Join-Path $runDir 'pg.log'
$status = & (Join-Path $pgBin 'pg_isready.exe') -h 127.0.0.1 -p $PgPort 2>&1
if ($LASTEXITCODE -ne 0) {
  if (-not (Test-Path $pgData)) {
    Write-Err "数据目录不存在：$pgData（请先执行 tools\db\setup-pg.ps1）"
    exit 1
  }
  & (Join-Path $pgBin 'pg_ctl.exe') -D $pgData -l $pgLog -o "-p $PgPort -c listen_addresses=127.0.0.1" start | Out-Null
  Start-Sleep -Seconds 2
  $status = & (Join-Path $pgBin 'pg_isready.exe') -h 127.0.0.1 -p $PgPort 2>&1
}
if ($LASTEXITCODE -eq 0) { Write-Ok "数据库就绪（$status）" } else { Write-Err "数据库启动失败，见 $pgLog"; exit 1 }

# ---------------------------------------------------------------- 2. 模拟器
if (-not $SkipSim) {
  Write-Step '启动双协议模拟器'
  $py = Join-Path $root 'tools\sim\.venv\Scripts\python.exe'
  $simOut = Join-Path $runDir 'sim.out.log'
  $simErr = Join-Path $runDir 'sim.err.log'
  $existing = Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
    Where-Object { $_.CommandLine -like '*run_sim.py*' }
  if ($existing) {
    Write-Warn2 "模拟器已在运行（PID $($existing.ProcessId -join ',')）"
  } else {
    Start-Process -FilePath $py -ArgumentList 'tools\sim\run_sim.py' -WorkingDirectory $root `
      -RedirectStandardOutput $simOut -RedirectStandardError $simErr -WindowStyle Hidden
    Start-Sleep -Seconds 3
    try {
      $h = Invoke-RestMethod 'http://127.0.0.1:18080/health' -TimeoutSec 5
      Write-Ok "模拟器就绪：Modbus 1502 / RTU-TCP 1503 / S7 1103（代理 1102、1504），控制面 18080"
    } catch {
      Write-Err "模拟器健康检查失败，见 $simErr"
    }
  }
}

# ---------------------------------------------------------------- 3. API
Write-Step "启动 SFMS API（端口 $ApiPort）"
$apiOut = Join-Path $runDir 'api.out.log'
$apiErr = Join-Path $runDir 'api.err.log'
$apiProj = Join-Path $root 'src\SFMS.Api'
$env:ASPNETCORE_URLS = "http://127.0.0.1:$ApiPort"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__Sfms = "Host=127.0.0.1;Port=$PgPort;Database=sfms;Username=postgres;Password=postgres"

$apiRunning = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
  Where-Object { $_.CommandLine -like '*SFMS.Api*' }
if ($apiRunning) {
  Write-Warn2 "API 已在运行（PID $($apiRunning.ProcessId -join ',')）"
} else {
  Start-Process -FilePath $dotnet.Source -ArgumentList @('run', '--project', $apiProj, '--no-launch-profile') `
    -WorkingDirectory $root -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -WindowStyle Hidden

  $ok = $false
  for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 1
    try {
      $r = Invoke-RestMethod "http://127.0.0.1:$ApiPort/health/ready" -TimeoutSec 3
      if ($r.ready) { $ok = $true; break }
    } catch { }
  }
  if ($ok) { Write-Ok "API 就绪：http://127.0.0.1:$ApiPort" }
  else { Write-Err "API 启动超时，见 $apiErr"; exit 1 }
}

# ---------------------------------------------------------------- 4. 前端（可选）
if ($Dev) {
  Write-Step '启动 Vite 开发服务器（5173）'
  Push-Location (Join-Path $root 'web')
  Start-Process -FilePath 'npm.cmd' -ArgumentList 'run', 'dev' -WorkingDirectory (Get-Location) `
    -RedirectStandardOutput (Join-Path $runDir 'web.out.log') `
    -RedirectStandardError (Join-Path $runDir 'web.err.log') -WindowStyle Hidden
  Pop-Location
  Start-Sleep -Seconds 4
  Write-Ok '前端开发服务器：http://127.0.0.1:5173'
}

Write-Host ''
Write-Host '------------------------------------------------------------' -ForegroundColor DarkCyan
if ($Dev) { Write-Host '  打开 http://127.0.0.1:5173 （热更新）' -ForegroundColor White }
else { Write-Host "  打开 http://127.0.0.1:$ApiPort （已构建的静态前端 + API）" -ForegroundColor White }
Write-Host '  演示账号：admin/admin123  engineer/eng123  operator/op123  observer/ob123'
Write-Host '  接口文档：/swagger   健康检查：/health/ready'
Write-Host '------------------------------------------------------------' -ForegroundColor DarkCyan
