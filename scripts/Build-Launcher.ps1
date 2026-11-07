param([string]$MsvcRoot=$env:VCToolsInstallDir,
      [string]$SdkRoot='C:\Program Files (x86)\Windows Kits\10',[string]$SdkVersion='10.0.26100.0')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$output=Join-Path $repo 'artifacts\launcher'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$env:PATH=(Join-Path $MsvcRoot 'bin\Hostx64\x64')+';'+(Join-Path $SdkRoot "bin\$SdkVersion\x64")+';'+$env:PATH
$env:INCLUDE=(Join-Path $MsvcRoot 'include')+';'+(Join-Path $SdkRoot "Include\$SdkVersion\um")+';'+(Join-Path $SdkRoot "Include\$SdkVersion\shared")+';'+(Join-Path $SdkRoot "Include\$SdkVersion\ucrt")
$env:LIB=(Join-Path $MsvcRoot 'lib\x64')+';'+(Join-Path $SdkRoot "Lib\$SdkVersion\um\x64")+';'+(Join-Path $SdkRoot "Lib\$SdkVersion\ucrt\x64")
Push-Location (Join-Path $repo 'native\Launcher')
try {
 & (Join-Path $SdkRoot "bin\$SdkVersion\x64\rc.exe") /nologo "/fo$output\Launcher.res" Launcher.rc
 if($LASTEXITCODE){throw '启动器资源构建失败'}
 & (Join-Path $MsvcRoot 'bin\Hostx64\x64\cl.exe') /nologo /std:c++17 /utf-8 /EHsc /W4 /O2 /MT /DUNICODE /D_UNICODE "/Fo$output\Launcher.obj" Launcher.cpp "$output\Launcher.res" /link /SUBSYSTEM:WINDOWS "/OUT:$output\RoboCopyUI.exe" /MANIFEST:EMBED "/MANIFESTINPUT:Launcher.manifest" user32.lib shell32.lib
 if($LASTEXITCODE){throw '启动器构建失败'}
}finally{Pop-Location}
$output
