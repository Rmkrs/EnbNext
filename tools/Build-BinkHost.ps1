param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation=(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
if(!$installation){throw 'Installed Windows MSVC x86 tools are required for the Bink codec host.'}
$vc=Get-ChildItem (Join-Path $installation 'VC/Tools/MSVC') -Directory|Sort-Object Name -Descending|Select-Object -First 1
$sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$version=Get-ChildItem (Join-Path $sdk 'Include') -Directory|Where-Object {Test-Path (Join-Path $_.FullName 'um/windows.h')}|Sort-Object Name -Descending|Select-Object -First 1
if(!$version){throw 'Installed Windows SDK headers are required.'}
$output=[IO.Path]::GetFullPath($OutputDirectory);[IO.Directory]::CreateDirectory($output)|Out-Null
$arguments=@('/nologo','/std:c++17','/EHsc','/MT','/O2','/W4',('/I'+(Join-Path $vc.FullName 'include')))
foreach($part in @('ucrt','shared','um')){$arguments+=('/I'+(Join-Path $version.FullName $part))}
$arguments+=@($Source,('/Fo'+(Join-Path $output 'BinkHost.obj')),('/Fe'+(Join-Path $output 'EnbNext.BinkHost.exe')),'/link','/machine:x86',('/LIBPATH:'+(Join-Path $vc.FullName 'lib/x86')),('/LIBPATH:'+(Join-Path $sdk ('Lib/'+$version.Name+'/ucrt/x86'))),('/LIBPATH:'+(Join-Path $sdk ('Lib/'+$version.Name+'/um/x86'))),'kernel32.lib','user32.lib','dsound.lib','ole32.lib')
& (Join-Path $vc.FullName 'bin/Hostx64/x86/cl.exe') @arguments
if($LASTEXITCODE-ne 0){throw 'Bink codec host compilation failed'}
