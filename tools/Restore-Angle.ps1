param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference='Stop'
$Destination=[IO.Path]::GetFullPath($Destination)
[IO.Directory]::CreateDirectory($Destination) | Out-Null
$archive=Join-Path $Destination 'package.zip'
if(-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://github.com/electron/electron/releases/download/v41.0.3/electron-v41.0.3-win32-x64.zip' -OutFile $archive
}
if((Get-FileHash -LiteralPath $archive).Hash -ne '93346B31DFCD779C4C4CDFDFB2E3F9642E7A9888E388A8971FF3E95DBF948887'){throw 'Electron archive hash mismatch'}
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach($name in @('libEGL.dll','libGLESv2.dll','LICENSE','LICENSES.chromium.html')) {
        $entry=$zip.GetEntry($name)
        if($null -eq $entry){throw "Missing pinned archive entry: $name"}
        $outputName=if($name -eq 'LICENSE'){'electron-LICENSE'}else{$name}
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $Destination $outputName),$true)
    }
} finally { $zip.Dispose() }
