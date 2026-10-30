[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$MsBuildPath
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# 定位 MSBuild：优先 vswhere（VS2022），回退 Framework 自带
if (-not $MsBuildPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $MsBuildPath = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
    if (-not $MsBuildPath) {
        $MsBuildPath = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
    }
}
if (-not (Test-Path $MsBuildPath)) {
    Write-Error "未找到 MSBuild：$MsBuildPath"
    exit 2
}

$proj = Join-Path $PSScriptRoot '..\src\PyTools\PyTools.csproj'
Write-Host "== PyTools v2 build ($Configuration) =="
Write-Host "MSBuild: $MsBuildPath"

& $MsBuildPath $proj "/p:Configuration=$Configuration" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "构建失败（exit $LASTEXITCODE）"
    exit $LASTEXITCODE
}

$exe = Join-Path $PSScriptRoot "..\src\PyTools\bin\$Configuration\PyTools.exe"
if (-not (Test-Path $exe)) {
    Write-Error "未找到产物：$exe"
    exit 3
}
$size = [Math]::Round((Get-Item $exe).Length / 1KB)
Write-Host "OK -> $exe ($size KB)"
exit 0
