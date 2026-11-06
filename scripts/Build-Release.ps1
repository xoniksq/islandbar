param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part release version.' }
$root = Split-Path -Parent $PSScriptRoot
$outputPath = [IO.Path]::GetFullPath($Output, (Get-Location).Path)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$work = Join-Path $outputPath ('work-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $work 'publish'
$package = Join-Path $work 'package'
$licenses = Join-Path $package 'Licenses'

dotnet publish (Join-Path $root 'IslandBar.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" -o $publish
if ($LASTEXITCODE -ne 0) { throw 'IslandBar build failed.' }
$exe = Join-Path $publish 'IslandBar.exe'
if ((Get-Item -LiteralPath $exe).VersionInfo.FileVersion -ne "$Version.0") { throw 'Unexpected executable version.' }
$check = Start-Process -FilePath $exe -ArgumentList '--shell-key-check' -WindowStyle Hidden -Wait -PassThru
if ($check.ExitCode -ne 0) { throw 'IslandBar self-check failed.' }

New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $package 'IslandBar.exe')
$assets = Get-Content -LiteralPath (Join-Path $root 'obj/project.assets.json') -Raw | ConvertFrom-Json
$dependencies = $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies
function Runtime-Package([string]$Name) {
    $dependency = $dependencies | Where-Object Name -EQ $Name | Select-Object -First 1
    if (!$dependency -or $dependency.version -notmatch '^\[([^,\]]+)') { throw "Missing runtime version for $Name" }
    $versionDirectory = $Matches[1]
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder ($Name.ToLowerInvariant() + '/' + $versionDirectory)
        if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
    }
    throw "Runtime package not found for $Name"
}
$runtime = Runtime-Package 'Microsoft.NETCore.App.Runtime.win-x64'
$desktop = Runtime-Package 'Microsoft.WindowsDesktop.App.Runtime.win-x64'
Copy-Item -LiteralPath (Join-Path $runtime 'LICENSE.TXT') -Destination (Join-Path $licenses 'dotnet-runtime-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $runtime 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenses 'dotnet-THIRD-PARTY-NOTICES.txt')
Copy-Item -LiteralPath (Join-Path $desktop 'LICENSE') -Destination (Join-Path $licenses 'dotnet-desktop-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $root 'third-party/NAudio-LICENSE.txt') -Destination $licenses
$instructions = @"
Островок $Version для Windows 11 x64

Распакуй архив в постоянную папку и запусти IslandBar.exe. .NET уже в сборке.
ПКМ по часам открывает настройки островка и выход с возвратом панели Windows.
Обновления скачиваются с GitHub и устанавливаются, когда ПК простаивает и хранилище свободно.
"@
[IO.File]::WriteAllText((Join-Path $package 'Установка.txt'), $instructions, [Text.UTF8Encoding]::new($true))

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $outputPath 'IslandBar-Windows-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($package, $zip, [IO.Compression.CompressionLevel]::Optimal, $false, [Text.Encoding]::UTF8)
$manifest = [ordered]@{
    version = $Version
    size = (Get-Item -LiteralPath $zip).Length
    sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $outputPath 'islandbar-update.json'), ($manifest | ConvertTo-Json -Compress))
