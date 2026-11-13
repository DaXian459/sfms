<#
.SYNOPSIS
  停止 SFMS 全部进程（API → 模拟器 → PostgreSQL），只停本项目，不动系统服务。
#>
[CmdletBinding()]
param(
  [switch]$KeepDb,
  [int]$PgPort = 5433
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Kill-ByPattern($name, $pattern, $label) {
  $procs = Get-CimInstance Win32_Process -Filter "Name='$name'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine -like "*$pattern*" }
  if (-not $procs) { Write-Host "  [-] $label 未运行" -ForegroundColor DarkGray; return }
  foreach ($p in $procs) {
    try {
      Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
      Write-Host "  [x] 已停止 $label (PID $($p.ProcessId))" -ForegroundColor Yellow
    } catch {
      Write-Host "  [!] 停止 $label 失败：$($_.Exception.Message)" -ForegroundColor Red
    }
  }
}

Write-Host "`n=== 停止 SFMS 进程" -ForegroundColor Cyan
Kill-ByPattern 'dotnet.exe' 'SFMS.Api' 'API 与采集服务'
Kill-ByPattern 'node.exe' 'sfms' 'Vite 开发服务器'
Kill-ByPattern 'python.exe' 'run_sim.py' '双协议模拟器'

if (-not $KeepDb) {
  $pgCtl = Join-Path $root 'tools\db\pgsql\bin\pg_ctl.exe'
  $pgData = Join-Path $root '.pgdata'
  if ((Test-Path $pgCtl) -and (Test-Path $pgData)) {
    & $pgCtl -D $pgData stop -m fast 2>&1 | Out-Null
    Write-Host '  [x] 已停止 PostgreSQL' -ForegroundColor Yellow
  }
}

Write-Host "`n完成。日志保留在 .run\ 目录。" -ForegroundColor Green
