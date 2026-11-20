param([ValidateSet('win-x64','linux-x64')][string]$Runtime='win-x64',[Parameter(Mandatory)][string]$Destination,[Parameter(Mandatory)][string]$CacheDirectory)
$ErrorActionPreference='Stop'
# A solution build can run this for the App twice at once (two references with different properties): one at a time,
# or both copy into the same destination and one fails on a locked file.
$lock=[Threading.Mutex]::new($false,'Local\EnbNextPrepareCef')
try{ try{[void]$lock.WaitOne()}catch [Threading.AbandonedMutexException]{}
$cacheRoot=[IO.Path]::GetFullPath($CacheDirectory)
$cache=Join-Path $cacheRoot 'cef';New-Item -ItemType Directory -Force $cache | Out-Null
$version='150.0.23+g4ccc4ca+chromium-150.0.7871.287'
if($Runtime-eq 'win-x64'){$platform='windows64';$digest='8167B250A2F746F6C79370DFB2F7E0965495BA418241E7AF2A3EB2663DFCC0BB'}else{$platform='linux64';$digest='438F8EFD9849F7EB86E31633F88B2C9FC0EA1CDC931049E54DD44D281F3008D6'}
$name="cef_binary_${version}_${platform}_minimal"
$archive=Join-Path $cache ($name+'.tar.bz2');$cef=Join-Path $cache $name
if(!(Test-Path $archive)){Invoke-WebRequest ('https://cef-builds.spotifycdn.com/'+[uri]::EscapeDataString($name+'.tar.bz2')) -OutFile $archive}
if((Get-FileHash $archive -Algorithm SHA256).Hash-ne $digest){throw 'Pinned CEF archive hash mismatch'}
if(!(Test-Path (Join-Path $cef 'include/capi/cef_app_capi.h'))){tar -xf $archive -C $cache;if($LASTEXITCODE-ne 0){throw 'CEF extraction failed'}}
$destination=[IO.Path]::GetFullPath($Destination);New-Item -ItemType Directory -Force $destination | Out-Null
$native=Join-Path $cache ($Runtime+'-bridge');New-Item -ItemType Directory -Force $native | Out-Null
if($Runtime-eq 'win-x64'){& (Join-Path $PSScriptRoot 'Build-Cef.ps1') -CefRoot $cef -OutputDirectory $native}
else {
 # The bridge is rebuilt only when its sources or build script are newer than the built pair, so a Windows
 # publish needs WSL only after a bridge change (a bridge built on a Linux machine can be dropped in).
 $sources=@(Get-ChildItem (Join-Path $PSScriptRoot '../src/EnbNext.App/Native/Cef') -File | Where-Object Extension -in '.cpp','.h')+@(Get-Item (Join-Path $PSScriptRoot 'Build-Cef.sh'))
 $built=@('libEnbNext.Cef.so','EnbNext.CefHelper' | ForEach-Object { Join-Path $native $_ } | Where-Object { Test-Path $_ } | Get-Item)
 $fresh=$built.Count -eq 2 -and ($built | Measure-Object LastWriteTimeUtc -Minimum).Minimum -ge ($sources | Measure-Object LastWriteTimeUtc -Maximum).Maximum
 if($fresh){}
 elseif($IsWindows){
  $cefLinux=(& wsl -d Debian --exec wslpath -u $cef.Replace([char]92,[char]47)).Trim();$nativeLinux=(& wsl -d Debian --exec wslpath -u $native.Replace([char]92,[char]47)).Trim();$scriptLinux=(& wsl -d Debian --exec wslpath -u (Join-Path $PSScriptRoot 'Build-Cef.sh').Replace([char]92,[char]47)).Trim()
  & wsl -d Debian --exec sh $scriptLinux $cefLinux $nativeLinux
 }else{& sh (Join-Path $PSScriptRoot 'Build-Cef.sh') $cef $native}
 if(-not $fresh -and $LASTEXITCODE-ne 0){throw 'Linux CEF bridge/helper build failed'}
}
Copy-Item (Join-Path $cef 'Release/*') $destination -Recurse -Force
Copy-Item (Join-Path $cef 'Resources/*') $destination -Recurse -Force
Copy-Item (Join-Path $cef 'LICENSE.txt'),(Join-Path $cef 'CREDITS.html') $destination -Force
# Only runtime files ship: no sandbox bootstrap executables or import library.
foreach($stale in 'bootstrap.exe','bootstrapc.exe','libcef.lib','EnbNext.exe','EnbNext.dll','EnbNext.CefHelper.dll'){Remove-Item (Join-Path $destination $stale) -ErrorAction SilentlyContinue}
if($Runtime-eq 'win-x64'){Copy-Item (Join-Path $native 'EnbNext.Cef.dll'),(Join-Path $native 'EnbNext.CefHelper.exe') $destination -Force}
else{
 Copy-Item (Join-Path $native 'libEnbNext.Cef.so'),(Join-Path $native 'EnbNext.CefHelper') $destination -Force
 # The Linux CEF library carries its debug information (1.4 GB of 1.6 GB); a release does not need it. Where strip is
 # available on a Linux build it is removed, which changes nothing at run time.
 if($IsLinux -and (Get-Command strip -ErrorAction SilentlyContinue)){& strip --strip-debug (Join-Path $destination 'libcef.so');if($LASTEXITCODE-ne 0){throw 'Stripping libcef.so failed'}}
}
}finally{$lock.ReleaseMutex();$lock.Dispose()}
