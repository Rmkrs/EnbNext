param([Parameter(Mandatory)][string]$CefRoot,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$source=Join-Path $PSScriptRoot '../src/EnbNext.App/Native/Cef'
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation=(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
$vc=Get-ChildItem (Join-Path $installation 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$version=Get-ChildItem (Join-Path $sdk 'Include') -Directory | Where-Object {Test-Path (Join-Path $_.FullName 'um/windows.h')} | Sort-Object Name -Descending | Select-Object -First 1
$output=[IO.Path]::GetFullPath($OutputDirectory);[IO.Directory]::CreateDirectory($output)|Out-Null
$arguments=@('/nologo','/std:c++17','/EHsc','/MT','/O2','/W4','/DCEF_API_VERSION=15000',('/I'+[IO.Path]::GetFullPath($CefRoot)),('/I'+(Join-Path $vc.FullName 'include')))
foreach($part in @('ucrt','shared','um','winrt')){$arguments+=('/I'+(Join-Path $version.FullName $part))}
$link=@('/link','/machine:x64',('/LIBPATH:'+(Join-Path $vc.FullName 'lib/x64')),('/LIBPATH:'+(Join-Path $sdk ('Lib/'+$version.Name+'/ucrt/x64'))),('/LIBPATH:'+(Join-Path $sdk ('Lib/'+$version.Name+'/um/x64'))),('/LIBPATH:'+[IO.Path]::GetFullPath((Join-Path $CefRoot 'Release'))),'libcef.lib','kernel32.lib','user32.lib')
& (Join-Path $vc.FullName 'bin/Hostx64/x64/cl.exe') @arguments /LD (Join-Path $source 'Browser.cpp') (Join-Path $source 'TextureTransfer.cpp') ('/Fo'+$output+'\') ('/Fe'+(Join-Path $output 'EnbNext.Cef.dll')) @link
if($LASTEXITCODE-ne 0){throw 'CEF bridge compilation failed'}
# The linker embeds the helper manifest with the SDK's mt.exe.
$env:PATH=(Join-Path $sdk ('bin/'+$version.Name+'/x64'))+[IO.Path]::PathSeparator+$env:PATH
# Chromium subprocesses (renderer, GPU, network) run this small helper; the
# game itself is the ordinary .NET executable (no Windows sandbox).
& (Join-Path $vc.FullName 'bin/Hostx64/x64/cl.exe') @arguments (Join-Path $source 'Helper.cpp') ('/Fo'+$output+'\') ('/Fe'+(Join-Path $output 'EnbNext.CefHelper.exe')) @link /SUBSYSTEM:WINDOWS /MANIFEST:EMBED ('/MANIFESTINPUT:'+(Join-Path $source 'CefHelper.manifest'))
if($LASTEXITCODE-ne 0){throw 'CEF helper compilation failed'}
