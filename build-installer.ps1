# Сборка установщика MusicRPC: приложение -> сжатый payload -> Setup\MusicRPC-Setup.exe -> installer\MusicRPC-Setup-<версия>.exe
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content MusicRPC.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "Version: $version"

# 1. приложение (self-contained, один файл)
dotnet publish MusicRPC.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# 2. payload: 8 байт — размер распакованного файла, дальше deflate-поток
New-Item -ItemType Directory -Force Setup\payload | Out-Null
$payload = Join-Path $PSScriptRoot 'Setup\payload\payload.bin'
$src = Join-Path $PSScriptRoot 'publish\MusicRPC.exe'
$in = [IO.File]::OpenRead($src)
$out = [IO.File]::Create($payload)
try
{
    $out.Write([BitConverter]::GetBytes([int64]$in.Length), 0, 8)
    $def = New-Object IO.Compression.DeflateStream($out, [IO.Compression.CompressionLevel]::Optimal, $true)
    $in.CopyTo($def, 1MB)
    $def.Dispose()
}
finally { $in.Dispose(); $out.Dispose() }
Write-Host ("payload: {0:N1} MB" -f ((Get-Item $payload).Length / 1MB))

# 3. установщик
dotnet build Setup\MusicRPC.Setup.csproj -c Release "-p:AppVersion=$version" "-p:PayloadPath=$payload"
if ($LASTEXITCODE -ne 0) { throw 'setup build failed' }

New-Item -ItemType Directory -Force installer | Out-Null
$dst = "installer\MusicRPC-Setup-$version.exe"
Copy-Item Setup\bin\Release\net48\MusicRPC-Setup.exe $dst -Force
Write-Host ("Done: {0} ({1:N1} MB)" -f $dst, ((Get-Item $dst).Length / 1MB))
