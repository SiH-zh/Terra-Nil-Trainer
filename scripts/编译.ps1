param(
    [Parameter(Mandatory=$true)][string]$GamePath,
    [string]$LoaderZip
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot 'src'
$package = Join-Path $repoRoot 'dist\Terra-Nil-Trainer'
$cache = Join-Path $repoRoot '.cache'
$managed = Join-Path $GamePath 'Terra Nil_Data\Managed'
if (-not (Test-Path -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll'))) { throw '未找到游戏程序集，请指定实际的游戏安装目录。' }
New-Item -ItemType Directory -Force -Path $cache,$package | Out-Null
if (-not $LoaderZip) {
    $LoaderZip = Join-Path $cache 'BepInEx_win_x64_5.4.23.5.zip'
    if (-not (Test-Path -LiteralPath $LoaderZip)) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip' -OutFile $LoaderZip
    }
}
$expected = '82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4'
if ((Get-FileHash -LiteralPath $LoaderZip -Algorithm SHA256).Hash -ne $expected) { throw '加载框架压缩包校验失败。' }
$payload = Join-Path $package '载荷'
New-Item -ItemType Directory -Force -Path $payload | Out-Null
Expand-Archive -LiteralPath $LoaderZip -DestinationPath $payload -Force
$core = Join-Path $payload 'BepInEx\core'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '未找到系统 C# 编译器。' }
$names = @('Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.IMGUIModule.dll','Unity.InputSystem.dll','UnityEngine.TextRenderingModule.dll','UnityEngine.InputLegacyModule.dll','netstandard.dll','Sirenix.Serialization.dll','Sirenix.OdinInspector.Attributes.dll')
$references = foreach ($name in $names) {
    $reference = Join-Path $managed $name
    if (-not (Test-Path -LiteralPath $reference)) { throw "缺少游戏依赖程序集：$name" }
    '/reference:' + $reference
}
$pluginDir = Join-Path $payload 'BepInEx\plugins\TerraNilTrainer'
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
$sources = @(Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | Where-Object { $_.Name -ne 'Installer.cs' } | ForEach-Object { $_.FullName })
& $compiler /nologo /target:library /optimize+ "/out:$pluginDir\TerraNilTrainer.dll" "/reference:$core\BepInEx.dll" "/reference:$core\0Harmony.dll" $references $sources
if ($LASTEXITCODE -ne 0) { throw '插件编译失败。' }
$installerSources = @((Join-Path $source 'Installer.cs'))
if (Test-Path -LiteralPath (Join-Path $source 'ShortcutSettings.cs')) { $installerSources += Join-Path $source 'ShortcutSettings.cs' }
& $compiler /nologo /target:winexe /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$package\伊始之地修改器.exe" $installerSources
if ($LASTEXITCODE -ne 0) { throw '安装工具编译失败。' }
$config = Join-Path $payload 'doorstop_config.ini'
$configText = Get-Content -LiteralPath $config -Raw
$configText = [regex]::Replace($configText, '(?m)^enabled\s*=.*$', 'enabled=true')
$configText = [regex]::Replace($configText, '(?m)^target_assembly\s*=.*$', 'target_assembly=BepInEx\core\BepInEx.Preloader.dll')
[IO.File]::WriteAllText($config,$configText,[Text.UTF8Encoding]::new($false))
$manifest = foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse) {
    if ($file.Name -eq 'package.tsv') { continue }
    $file.FullName.Substring($payload.Length + 1).Replace('\','/') + "`t" + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
[IO.File]::WriteAllLines((Join-Path $payload 'package.tsv'),[string[]]$manifest,[Text.UTF8Encoding]::new($true))
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE'),(Join-Path $repoRoot 'LICENSE.zh-CN.md') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $package -Recurse -Force
Copy-Item -LiteralPath $source -Destination $package -Recurse -Force
Copy-Item -LiteralPath $PSScriptRoot -Destination $package -Recurse -Force
Write-Host "编译完成：$package。未安装或修改游戏。正式对外分发请补齐第三方对应源码并重新生成完整包校验。"
