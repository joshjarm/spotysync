# Builds SpotySync for Linux (x64) from Windows: dist\SpotySync-<version>-linux-x64 and a .tar.gz of it.
# The Linux tools (spotDL, yt-dlp, ffmpeg, Deno) are downloaded once into .build-cache\linux.
$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectRoot "src\SpotySync.Desktop\SpotySync.Desktop.csproj"
[xml]$csproj = Get-Content -LiteralPath $project
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$packageName = "SpotySync-$version-linux-x64"
$distRoot = Join-Path $projectRoot "dist"
$cacheRoot = Join-Path $projectRoot ".tools-cache\linux" # not .build-cache: build-release.ps1 deletes that
$stage = Join-Path $projectRoot ".build-cache\linux-stage\$packageName"
New-Item -ItemType Directory -Force -Path $cacheRoot, $distRoot | Out-Null
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }

Write-Host "Publishing SpotySync $version for linux-x64..."
dotnet publish $project -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

function Get-Cached([string]$url, [string]$file) {
    $path = Join-Path $cacheRoot $file
    if (-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -lt 1MB) {
        Write-Host "Downloading $file..."
        Invoke-WebRequest -Uri $url -OutFile $path -UseBasicParsing
    }
    return $path
}

$tools = Join-Path $stage "Tools"
New-Item -ItemType Directory -Force -Path $tools | Out-Null
Copy-Item (Get-Cached "https://github.com/spotDL/spotify-downloader/releases/download/v4.6.0/spotdl-4.6.0-linux" "spotdl-4.6.0-linux") (Join-Path $tools "spotdl")
Copy-Item (Get-Cached "https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp_linux" "yt-dlp_linux-2026.10.26") (Join-Path $tools "yt-dlp")

$denoZip = Get-Cached "https://github.com/denoland/deno/releases/download/v2.9.8/deno-x86_64-unknown-linux-gnu.zip" "deno-2.9.8-linux.zip"
$denoDir = Join-Path $cacheRoot "deno"
if (-not (Test-Path -LiteralPath (Join-Path $denoDir "deno"))) { Expand-Archive -LiteralPath $denoZip -DestinationPath $denoDir -Force }
Copy-Item (Join-Path $denoDir "deno") (Join-Path $tools "deno")

$ffmpegTar = Get-Cached "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz" "ffmpeg-linux64-gpl.tar.xz"
$ffmpegDir = Join-Path $cacheRoot "ffmpeg"
if (-not (Test-Path -LiteralPath (Join-Path $ffmpegDir "ffmpeg"))) {
    New-Item -ItemType Directory -Force -Path $ffmpegDir | Out-Null
    $extract = Join-Path $cacheRoot "ffmpeg-extract"
    if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    tar -xf $ffmpegTar -C $extract
    if ($LASTEXITCODE -ne 0) { throw "Could not unpack ffmpeg." }
    foreach ($tool in "ffmpeg", "ffprobe") {
        $found = Get-ChildItem -LiteralPath $extract -Recurse -File -Filter $tool | Select-Object -First 1
        Copy-Item $found.FullName (Join-Path $ffmpegDir $tool)
    }
    Remove-Item -LiteralPath $extract -Recurse -Force
}
Copy-Item (Join-Path $ffmpegDir "ffmpeg") (Join-Path $tools "ffmpeg")
Copy-Item (Join-Path $ffmpegDir "ffprobe") (Join-Path $tools "ffprobe")

# The Premium token helper (bgutil) is JavaScript run by Deno, so the Windows build's copy works unchanged.
# It comes from the latest Windows release folder (build-release.ps1 fetches and verifies it).
$winTools = Get-ChildItem -LiteralPath $distRoot -Directory -Filter "SpotySync-*-win-x64" | Sort-Object LastWriteTime -Descending |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "Tools\bgutil-server\src") } | Select-Object -First 1
if (-not $winTools) { throw "Build the Windows version first (build-release.ps1): its Tools\bgutil-server is reused." }
$serverSource = Join-Path $winTools.FullName "Tools\bgutil-server"
$serverTarget = Join-Path $tools "bgutil-server"
New-Item -ItemType Directory -Force -Path $serverTarget | Out-Null
Get-ChildItem -LiteralPath $serverSource -Force | Where-Object { $_.Name -ne "node_modules" } | Copy-Item -Destination $serverTarget -Recurse
Copy-Item (Join-Path $winTools.FullName "Tools\yt-dlp-plugins") $tools -Recurse
Copy-Item (Join-Path $projectRoot "browser_options.example.txt") $tools

Copy-Item (Join-Path $projectRoot "src\SpotySync.App\Assets\SpotySync.png") $stage
Copy-Item (Join-Path $projectRoot "linux\install.sh") $stage
Copy-Item (Join-Path $projectRoot "linux\README-LINUX.txt") $stage
if (Test-Path (Join-Path $projectRoot "THIRD-PARTY-NOTICES.txt")) { Copy-Item (Join-Path $projectRoot "THIRD-PARTY-NOTICES.txt") $stage }

# never ship anyone's login or settings
$forbidden = @("spotify-token.json", "SpotySync.ini", "browser_options.txt", "liked-cache.json", "sync-state.json")
$leaked = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $forbidden -contains $_.Name }
if ($leaked) { throw "A settings or login file got into the package: $($leaked.FullName -join ', ')" }

# Ready-to-copy folder (program files replaced; anything the app created there stays) and the archive.
$folder = Join-Path $distRoot $packageName
New-Item -ItemType Directory -Force -Path $folder | Out-Null
Copy-Item -Path (Join-Path $stage "*") -Destination $folder -Recurse -Force
$archive = Join-Path $distRoot "$packageName.tar.gz"
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
tar -czf $archive -C (Split-Path -Parent $stage) $packageName
if ($LASTEXITCODE -ne 0) { throw "Could not create the archive." }
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ""
Write-Host "LINUX APP:     $folder"
Write-Host "LINUX ARCHIVE: $archive"
Write-Host "SHA-256:       $hash"
