param([Parameter(Mandatory=$true)][string]$DotnetRoot,[string]$RuntimeVersion='10.0.14',
 [string]$InnoCompiler='ISCC.exe',
 [string]$MsvcRoot=$env:VCToolsInstallDir,
 [string]$SdkRoot='C:\Program Files (x86)\Windows Kits\10',[string]$SdkVersion='10.0.26100.0')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$payload=Join-Path $repo ('artifacts\package\'+[guid]::NewGuid().ToString('N')+'\payload')
$project=[xml][IO.File]::ReadAllText((Join-Path $repo 'RoboCopyUI.csproj'))
$productVersion=[string]($project.Project.PropertyGroup.Version | Select-Object -First 1)
if($productVersion -notmatch '^\d+\.\d+\.\d+$'){throw 'Unexpected product version'}
$version=$productVersion.Split('-')[0]+'.0'
$output=Join-Path $repo ('artifacts\installer\'+$productVersion)
New-Item -ItemType Directory -Path $payload,$output -Force | Out-Null
$env:DOTNET_ROOT=$DotnetRoot
& (Join-Path $DotnetRoot 'dotnet.exe') publish (Join-Path $repo 'RoboCopyUI.csproj') -c Release --self-contained false -p:UseAppHost=false -p:UseSharedCompilation=false -o (Join-Path $payload 'App')
if($LASTEXITCODE){throw 'Application publish failed'}
& (Join-Path $PSScriptRoot 'Build-Launcher.ps1') -MsvcRoot $MsvcRoot -SdkRoot $SdkRoot -SdkVersion $SdkVersion
$runtime=Join-Path $payload 'runtime'
New-Item -ItemType Directory -Path $runtime,(Join-Path $runtime 'host'),(Join-Path $runtime 'shared\Microsoft.NETCore.App'),(Join-Path $runtime 'shared\Microsoft.WindowsDesktop.App') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'artifacts\launcher\RoboCopyUI.exe') -Destination $payload -Force
foreach($name in @('dotnet.exe','LICENSE.txt','ThirdPartyNotices.txt')){Copy-Item -LiteralPath (Join-Path $DotnetRoot $name) -Destination $runtime -Force}
Copy-Item -LiteralPath (Join-Path $DotnetRoot "host\fxr") -Destination (Join-Path $runtime 'host') -Recurse -Force
foreach($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')){Copy-Item -LiteralPath (Join-Path $DotnetRoot "shared\$framework\$RuntimeVersion") -Destination (Join-Path $runtime "shared\$framework") -Recurse -Force}
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'packaging\README.zh-CN.txt'),(Join-Path $repo 'packaging\README.en-US.txt') -Destination $payload -Force
if(@(Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object Extension -in @('.pfx','.msix','.cer')).Count -or (Test-Path -LiteralPath (Join-Path $runtime 'sdk'))){throw 'Private signing material, identity packages, certificates or SDK must not enter the payload'}
$manifest=Join-Path $payload 'installed-manifest.json'
$files=@(Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object FullName -ne $manifest | Sort-Object FullName | ForEach-Object {@{Path=$_.FullName.Substring($payload.Length+1);Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
@{SchemaVersion=1;Owner='RoboCopyUI.SafeDesktop.v1';Version=$version;Files=$files} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifest -Encoding UTF8
& $InnoCompiler "/DPayloadDir=$payload" "/DOutputDir=$output" "/DAppVersion=$productVersion" (Join-Path $repo 'packaging\RoboCopyUI.iss')
if($LASTEXITCODE){throw 'Per-user installer compile failed'}
$installer=Join-Path $output ("RoboCopyUI-$productVersion-win-x64-setup.exe")
Copy-Item -LiteralPath (Join-Path $repo 'packaging\README.zh-CN.txt'),(Join-Path $repo 'packaging\README.en-US.txt') -Destination $output -Force
Get-FileHash -LiteralPath $installer -Algorithm SHA256 | Select-Object @{Name='File';Expression={Split-Path $_.Path -Leaf}},Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.json') -Encoding UTF8
$output
