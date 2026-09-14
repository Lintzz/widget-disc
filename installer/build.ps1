<#
.SYNOPSIS
    Gera o instalador: artifacts\DiscordVoiceWidget-Setup-<versao>.exe

.DESCRIPTION
    1. Publica o app (Release, ReadyToRun) em artifacts\publish.
    2. Le a versao do executavel publicado - a unica fonte e o <Version> do .csproj.
    3. Compila installer\DiscordVoiceWidget.iss com o Inno Setup 6.

    Requisitos: .NET SDK 10 e Inno Setup 6 (winget install JRSoftware.InnoSetup).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

# Pasta limpa: um arquivo removido do projeto nao pode sobrar dentro do instalador.
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host '==> Publicando o app' -ForegroundColor Cyan
& dotnet publish (Join-Path $root 'src\DiscordVoiceWidget.App\DiscordVoiceWidget.App.csproj') `
    -c Release -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou ($LASTEXITCODE)" }

$exe = Join-Path $publish 'DiscordVoiceWidget.exe'
$info = (Get-Item $exe).VersionInfo
$version = '{0}.{1}.{2}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart
Write-Host "    versao $version" -ForegroundColor DarkGray

$iscc = @(
    (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue).Source,
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) {
    throw 'Inno Setup 6 nao encontrado. Instale com: winget install JRSoftware.InnoSetup'
}

Write-Host '==> Compilando o instalador' -ForegroundColor Cyan
& $iscc "/DAppVersion=$version" /Q (Join-Path $PSScriptRoot 'DiscordVoiceWidget.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC falhou ($LASTEXITCODE)" }

$setup = Join-Path $artifacts "DiscordVoiceWidget-Setup-$version.exe"
$sizeKb = [math]::Round((Get-Item $setup).Length / 1KB)
Write-Host "==> Pronto: $setup ($sizeKb KB)" -ForegroundColor Green
