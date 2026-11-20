param(
    # The app's Tools folder, e.g. src\SpotySync.App\bin\Release\net8.0-windows\Tools
    [Parameter(Mandatory = $true)][string]$ToolsDir
)

# Downloads the portable tools SpotySync needs (they are too large for git) from their official
# releases, verifies each against a pinned SHA-256 and puts them in $ToolsDir. Tools that are already
# there are left alone (yt-dlp updates itself), and downloads are cached in .tools-cache so they
# happen only once. Runs automatically after every build of SpotySync.App.

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # the progress bar makes Invoke-WebRequest very slow
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$cacheRoot = Join-Path $projectRoot ".tools-cache"
$ToolsDir = [IO.Path]::GetFullPath($ToolsDir)
New-Item -ItemType Directory -Force -Path $ToolsDir, $cacheRoot | Out-Null

function Get-Verified([string]$Url, [string]$FileName, [string]$Sha256) {
    $path = Join-Path $cacheRoot $FileName
    if ((Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $Sha256) { return $path }
    Write-Host "SpotySync tools: downloading $FileName ..."
    $partial = "$path.partial"
    for ($attempt = 1; ; $attempt++) {
        try { Invoke-WebRequest -Uri $Url -OutFile $partial -UseBasicParsing; break }
        catch { if ($attempt -ge 3) { throw "Could not download $Url : $($_.Exception.Message)" }; Start-Sleep -Seconds 3 }
    }
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Sha256) {
        Remove-Item -LiteralPath $partial -Force
        throw "SHA-256 check failed for $FileName (downloaded from $Url)."
    }
    Move-Item -LiteralPath $partial -Destination $path -Force
    return $path
}

function Expand-Entry([string]$Zip, [string]$EntryPath, [string]$Destination) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq $EntryPath } | Select-Object -First 1
        if ($null -eq $entry) { throw "$EntryPath not found in $Zip" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $Destination, $true)
    }
    finally { $archive.Dispose() }
}

function Need([string]$RelativePath) { -not (Test-Path -LiteralPath (Join-Path $ToolsDir $RelativePath)) }

if (Need "yt-dlp.exe") {
    $file = Get-Verified "https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe" "yt-dlp-2026.10.26.exe" `
        "F5389D3698ECDE4EF06F4E6B1CE0294FE9AC1396228C8990B8D3416D6D177A7D"
    Copy-Item -LiteralPath $file -Destination (Join-Path $ToolsDir "yt-dlp.exe")
}

if (Need "spotdl.exe") {
    # 4.5.2's bundled yt-dlp gets HTTP 403 from YouTube (Nov 2026); 4.6.0 works.
    $file = Get-Verified "https://github.com/spotDL/spotify-downloader/releases/download/v4.6.0/spotdl-4.6.0-win32.exe" "spotdl-4.6.0-win32.exe" `
        "E674030C3337CD74120A3A086887CE1A5195765CFC17E1971A2807E0A398B505"
    Copy-Item -LiteralPath $file -Destination (Join-Path $ToolsDir "spotdl.exe")
}

if (Need "deno.exe") {
    $zip = Get-Verified "https://github.com/denoland/deno/releases/download/v2.9.8/deno-x86_64-pc-windows-msvc.zip" "deno-2.9.8-x86_64-pc-windows-msvc.zip" `
        "CBA84B26E0DF5AE7F74FC66B86A95B866872A86292EBD84FA1E959927EDD93BF"
    Expand-Entry $zip "deno.exe" (Join-Path $ToolsDir "deno.exe")
}

if ((Need "ffmpeg.exe") -or (Need "ffprobe.exe")) {
    $zip = Get-Verified "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip" "ffmpeg-9.0.2-essentials_build.zip" `
        "60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA"
    foreach ($tool in "ffmpeg.exe", "ffprobe.exe") {
        if (Need $tool) { Expand-Entry $zip "ffmpeg-9.0.2-essentials_build/bin/$tool" (Join-Path $ToolsDir $tool) }
    }
}

# bgutil PO-token plugin (YouTube Premium ~256 kbps audio). yt-dlp and spotDL search Tools\yt-dlp-plugins.
if (Need "yt-dlp-plugins\bgutil-ytdlp-pot-provider.zip") {
    $file = Get-Verified "https://github.com/Brainicism/bgutil-ytdlp-pot-provider/releases/download/2.0.2/bgutil-ytdlp-pot-provider.zip" "bgutil-ytdlp-pot-provider-2.0.2.zip" `
        "A26B980BF6D8E8400D8F21445DAA4D8381BF6183D20F0BE309391F86B31E27EF"
    New-Item -ItemType Directory -Force -Path (Join-Path $ToolsDir "yt-dlp-plugins") | Out-Null
    Copy-Item -LiteralPath $file -Destination (Join-Path $ToolsDir "yt-dlp-plugins\bgutil-ytdlp-pot-provider.zip")
}

# The plugin's token server (source only; SpotySync installs its npm packages with Deno on first use).
if (Need "bgutil-server\src\generate_once.ts") {
    $zip = Get-Verified "https://github.com/Brainicism/bgutil-ytdlp-pot-provider/archive/refs/tags/2.0.2.zip" "bgutil-ytdlp-pot-provider-2.0.2-source.zip" `
        "DF68EE6210E4960757BCEAE298927FB2EBD4D5CD9D390F9FF3A8287B0D2D1054"
    $server = Join-Path $ToolsDir "bgutil-server"
    $prefix = "bgutil-ytdlp-pot-provider-2.0.2/"
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name -eq "${prefix}LICENSE") { $relative = "LICENSE" }
            elseif ($name.StartsWith("${prefix}server/")) { $relative = $name.Substring("${prefix}server/".Length) }
            else { continue }
            if ($relative.Length -eq 0 -or $relative.EndsWith("/")) { continue }
            $target = [IO.Path]::GetFullPath((Join-Path $server $relative))
            if (-not $target.StartsWith($server + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe path in archive: $name" }
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    }
    finally { $archive.Dispose() }
}

$copy = Join-Path $ToolsDir "browser_options.example.txt"
$example = Join-Path $projectRoot "browser_options.example.txt"
if (-not (Test-Path -LiteralPath $copy) -and (Test-Path -LiteralPath $example)) { Copy-Item -LiteralPath $example -Destination $copy }
