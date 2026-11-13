<#
.SYNOPSIS
  准备便携 PostgreSQL：解压 → initdb → 建库 sfms（幂等，可重复执行）。
.DESCRIPTION
  优先使用 tools/db/pg16.zip（若不存在则从 EDB 官网下载），解压到 tools/db/pgsql，
  数据目录 .pgdata，监听 127.0.0.1:5433，账号 postgres/postgres（仅开发环境）。
#>
[CmdletBinding()]
param(
  [int]$Port = 5433,
  [string]$DbName = 'sfms',
  [string]$Password = 'postgres',
  [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root

$dbDir = Join-Path $root 'tools\db'
$zip = Join-Path $dbDir 'pg16.zip'
$pgHome = Join-Path $dbDir 'pgsql'
$pgBin = Join-Path $pgHome 'bin'
$dataDir = Join-Path $root '.pgdata'
$url = 'https://get.enterprisedb.com/postgresql/postgresql-16.15-1-windows-x64-binaries.zip'

function Step($m) { Write-Host "`n=== $m" -ForegroundColor Cyan }

if ($Force -and (Test-Path $dataDir)) {
  Write-Host '  [!] -Force 指定：删除旧数据目录' -ForegroundColor Yellow
  Remove-Item -Recurse -Force $dataDir
}

# 1) 解压二进制
if (-not (Test-Path (Join-Path $pgBin 'pg_ctl.exe'))) {
  if (-not (Test-Path $zip)) {
    Step "下载 PostgreSQL 16 便携包（约 330MB）"
    curl.exe -L --retry 3 -o $zip $url
  }
  Step '解压到 tools\db\pgsql'
  Expand-Archive -Path $zip -DestinationPath $dbDir -Force
  # EDB 包内层是 pgsql 目录；若解出 pgsql\pgsql 则上移一层
  if (Test-Path (Join-Path $dbDir 'pgsql\pgsql\bin')) {
    Move-Item (Join-Path $dbDir 'pgsql\pgsql') $pgHome -Force
  }
  if (-not (Test-Path (Join-Path $pgBin 'pg_ctl.exe'))) {
    throw "解压后未找到 pg_ctl.exe，请检查 $pgHome"
  }
  Write-Host "  [OK] $pgBin" -ForegroundColor Green
} else {
  Write-Host "  [OK] 二进制已就绪：$pgBin" -ForegroundColor Green
}

# 2) initdb
if (-not (Test-Path (Join-Path $dataDir 'PG_VERSION'))) {
  Step '初始化数据目录 .pgdata'
  $pwFile = Join-Path $env:TEMP 'sfms_pw.txt'
  Set-Content -Path $pwFile -Value $Password -NoNewline -Encoding ascii
  & (Join-Path $pgBin 'initdb.exe') -D $dataDir -U postgres -A scram-sha-256 --pwfile=$pwFile -E UTF8 --locale=C | Out-Host
  Remove-Item $pwFile -Force
  if ($LASTEXITCODE -ne 0) { throw 'initdb 失败' }
  Write-Host '  [OK] 数据目录已初始化' -ForegroundColor Green
} else {
  Write-Host "  [OK] 数据目录已存在：$dataDir" -ForegroundColor Green
}

# 3) 启动并建库
Step "启动实例（端口 $Port）并创建数据库 $DbName"
$ready = & (Join-Path $pgBin 'pg_isready.exe') -h 127.0.0.1 -p $Port 2>&1
if ($LASTEXITCODE -ne 0) {
  $log = Join-Path $root '.run'
  New-Item -ItemType Directory -Force -Path $log | Out-Null
  & (Join-Path $pgBin 'pg_ctl.exe') -D $dataDir -l (Join-Path $log 'pg.log') `
    -o "-p $Port -c listen_addresses=127.0.0.1" start | Out-Null
  Start-Sleep -Seconds 3
}

$env:PGPASSWORD = $Password
$exists = & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p $Port -U postgres -tAc `
  "SELECT 1 FROM pg_database WHERE datname='$DbName'" 2>&1
if ("$exists".Trim() -ne '1') {
  & (Join-Path $pgBin 'createdb.exe') -h 127.0.0.1 -p $Port -U postgres -E UTF8 $DbName
  Write-Host "  [OK] 已创建数据库 $DbName" -ForegroundColor Green
} else {
  Write-Host "  [OK] 数据库 $DbName 已存在" -ForegroundColor Green
}

& (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p $Port -U postgres -d $DbName -c 'SELECT version();' | Out-Host
$env:PGPASSWORD = $null

Write-Host ''
Write-Host "PostgreSQL 就绪：Host=127.0.0.1;Port=$Port;Database=$DbName;Username=postgres;Password=$Password" -ForegroundColor Green
Write-Host '下一步：.\tools\start-all.ps1' -ForegroundColor White
