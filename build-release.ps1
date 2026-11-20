param(
    # Defaults to the <Version> in src\SpotySync.App\SpotySync.App.csproj (e.g. 1.5.0-T1).
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = ([xml](Get-Content -Raw (Join-Path $projectRoot "src\SpotySync.App\SpotySync.App.csproj"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$distRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot "dist"))
$packageName = "SpotySync-$Version-win-x64"
$packageRoot = [IO.Path]::GetFullPath((Join-Path $distRoot $packageName))
$zipPath = [IO.Path]::GetFullPath((Join-Path $distRoot "$packageName.zip"))
$cacheRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot ".build-cache"))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $cacheRoot "publish"))
$uiCaptureRoot = [IO.Path]::GetFullPath((Join-Path $cacheRoot "ui-captures"))
$toolSeedRoot = [IO.Path]::GetFullPath((Join-Path $cacheRoot "tool-seed"))
$stagingZipPath = [IO.Path]::GetFullPath((Join-Path $cacheRoot "$packageName.zip"))

function Assert-ChildPath([string]$Path, [string]$Parent) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing operation outside expected directory: $fullPath"
    }
}

Assert-ChildPath $packageRoot $distRoot
Assert-ChildPath $publishRoot $cacheRoot
Assert-ChildPath $uiCaptureRoot $cacheRoot
Assert-ChildPath $toolSeedRoot $cacheRoot
Assert-ChildPath $stagingZipPath $cacheRoot
Assert-ChildPath $zipPath $distRoot
Assert-ChildPath $cacheRoot $projectRoot

New-Item -ItemType Directory -Force -Path $distRoot, $cacheRoot | Out-Null
if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
if (Test-Path -LiteralPath $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
if (Test-Path -LiteralPath $uiCaptureRoot) { Remove-Item -LiteralPath $uiCaptureRoot -Recurse -Force }
if (Test-Path -LiteralPath $toolSeedRoot) { Remove-Item -LiteralPath $toolSeedRoot -Recurse -Force }
if (Test-Path -LiteralPath $stagingZipPath) { Remove-Item -LiteralPath $stagingZipPath -Force }

# Large portable tools are already present in the latest clean release ZIP. Reuse
# those instead of retaining a second 460+ MB uncompressed toolchain in the source
# tree. A legacy SpotDL folder remains supported for first-time/older workspaces.
$portableTools = @("ffmpeg.exe", "ffprobe.exe", "deno.exe", "yt-dlp.exe")
$appToolsRoot = Join-Path $projectRoot "src\SpotySync.App\bin\Release\net8.0-windows\Tools"
$legacyToolsRoot = Join-Path $projectRoot "SpotDL"
$installedToolsRoot = Join-Path $env:ProgramFiles "SpotySync\Tools"
$legacyToolsComplete = $portableTools | ForEach-Object { Test-Path -LiteralPath (Join-Path $legacyToolsRoot $_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count
$distToolsComplete = $portableTools | ForEach-Object { Test-Path -LiteralPath (Join-Path $distRoot $_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count
$appToolsComplete = $portableTools | ForEach-Object { Test-Path -LiteralPath (Join-Path $appToolsRoot $_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count
$installedToolsComplete = $portableTools | ForEach-Object { Test-Path -LiteralPath (Join-Path $installedToolsRoot $_) } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count
if ($legacyToolsComplete -eq 0) {
    $toolInputRoot = $legacyToolsRoot
}
elseif ($distToolsComplete -eq 0) {
    # Loose tool binaries kept directly in dist (binaries only are copied; credentials never are).
    $toolInputRoot = $distRoot
    Write-Host "Reused portable tools from: $distRoot"
}
elseif ($appToolsComplete -eq 0) {
    # Binaries from the locally built app folder (its settings, Data and Logs are never copied).
    $toolInputRoot = $appToolsRoot
    Write-Host "Reused portable tools from: $appToolsRoot"
}
elseif ($installedToolsComplete -eq 0) {
    # Reuse binaries only. Credentials and every other installed/user file are
    # deliberately excluded by the explicit copy list later in this script.
    $toolInputRoot = $installedToolsRoot
    Write-Host "Reused portable tools from installed SpotySync: $installedToolsRoot"
}
else {
    $seedArchive = Get-ChildItem -LiteralPath $distRoot -Filter "SpotySync-*-win-x64.zip" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $seedArchive) {
        throw "Portable toolchain is unavailable. Keep the latest SpotySync release ZIP in dist, or restore the SpotDL tool binaries."
    }
    New-Item -ItemType Directory -Force -Path $toolSeedRoot | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($seedArchive.FullName)
    try {
        foreach ($tool in @($portableTools + "spotdl.exe")) {
            $entry = $archive.Entries | Where-Object {
                $_.FullName.Replace('\', '/').EndsWith("/Tools/$tool", [StringComparison]::OrdinalIgnoreCase)
            } | Select-Object -First 1
            if ($null -eq $entry) { throw "The retained release ZIP is missing Tools/$tool." }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $toolSeedRoot $tool), $true)
        }
    }
    finally { $archive.Dispose() }
    $toolInputRoot = $toolSeedRoot
    Write-Host "Reused portable tools from: $($seedArchive.FullName)"
}

# Build each project (the .slnx solution format needs the .NET 9 SDK; these build with .NET 8).
foreach ($project in @("src\SpotySync.Core\SpotySync.Core.csproj", "src\SpotySync.MusicBeePlugin\SpotySync.MusicBeePlugin.csproj",
                       "src\SpotySync.App\SpotySync.App.csproj", "tests\SpotySync.Core.Tests\SpotySync.Core.Tests.csproj",
                       "tests\SpotySync.Ui.Smoke\SpotySync.Ui.Smoke.csproj")) {
    dotnet build (Join-Path $projectRoot $project) -c Release
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}

$testExe = Join-Path $projectRoot "tests\SpotySync.Core.Tests\bin\Release\net8.0\SpotySync.Core.Tests.exe"
& $testExe
if ($LASTEXITCODE -ne 0) { throw "Test suite failed." }

$uiSmokeExe = Join-Path $projectRoot "tests\SpotySync.Ui.Smoke\bin\Release\net8.0-windows\SpotySync.Ui.Smoke.exe"
& $uiSmokeExe $uiCaptureRoot
if ($LASTEXITCODE -ne 0) { throw "UI smoke test failed." }

dotnet publish (Join-Path $projectRoot "src\SpotySync.App\SpotySync.App.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw "Portable application publish failed." }

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $publishRoot "SpotySync.exe") -Destination (Join-Path $packageRoot "SpotySync.exe")
Copy-Item -LiteralPath (Join-Path $projectRoot "README.md") -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot "QUICK START.txt") -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot "THIRD-PARTY-NOTICES.txt") -Destination $packageRoot

$pluginRoot = Join-Path $packageRoot "MusicBee Plugin"
New-Item -ItemType Directory -Force -Path $pluginRoot | Out-Null
$pluginDll = Join-Path $projectRoot "src\SpotySync.MusicBeePlugin\bin\Release\net472\mb_MusicBeeSyncToService.dll"
Copy-Item -LiteralPath $pluginDll -Destination $pluginRoot
Copy-Item -LiteralPath (Join-Path $projectRoot "MusicBee Plugin README.txt") -Destination (Join-Path $pluginRoot "README.txt")
Compress-Archive -LiteralPath (Join-Path $pluginRoot "mb_MusicBeeSyncToService.dll"), (Join-Path $pluginRoot "README.txt") `
    -DestinationPath (Join-Path $pluginRoot "SpotySync-MusicBee-Plugin.zip") -CompressionLevel Optimal

$toolsRoot = Join-Path $packageRoot "Tools"
New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null
# 4.5.2's bundled yt-dlp gets HTTP 403 from YouTube (Nov 2026); 4.6.0 works.
$spotDlCache = Join-Path $cacheRoot "spotdl-4.6.0-win32.exe"
$spotDlUrl = "https://github.com/spotDL/spotify-downloader/releases/download/v4.6.0/spotdl-4.6.0-win32.exe"
$spotDlSha256 = "e674030c3337cd74120a3a086887ce1a5195765cfc17e1971a2807e0a398b505"
if (-not (Test-Path -LiteralPath $spotDlCache) -or (Get-FileHash -LiteralPath $spotDlCache -Algorithm SHA256).Hash -ne $spotDlSha256) {
    $seedSpotDl = Join-Path $toolInputRoot "spotdl.exe"
    if ((Test-Path -LiteralPath $seedSpotDl) -and (Get-FileHash -LiteralPath $seedSpotDl -Algorithm SHA256).Hash -eq $spotDlSha256) {
        Copy-Item -LiteralPath $seedSpotDl -Destination $spotDlCache
    }
    else {
        Invoke-WebRequest -Uri $spotDlUrl -OutFile $spotDlCache -UseBasicParsing
    }
}
if ((Get-FileHash -LiteralPath $spotDlCache -Algorithm SHA256).Hash -ne $spotDlSha256) {
    throw "Downloaded spotDL SHA-256 verification failed."
}
Copy-Item -LiteralPath $spotDlCache -Destination (Join-Path $toolsRoot "spotdl.exe")
$spot2yt = Join-Path $toolInputRoot "spot2yt.exe"
if (Test-Path -LiteralPath $spot2yt) { Copy-Item -LiteralPath $spot2yt -Destination (Join-Path $toolsRoot "spot2yt.exe") }
foreach ($tool in @("ffmpeg.exe", "ffprobe.exe", "deno.exe", "yt-dlp.exe")) {
    Copy-Item -LiteralPath (Join-Path $toolInputRoot $tool) -Destination $toolsRoot
}
# bgutil PO-token plugin for YouTube Premium (~256 kbps) audio. It lives in Tools\yt-dlp-plugins,
# which yt-dlp.exe and spotdl.exe both search next to themselves, so the package stays portable and
# never depends on %APPDATA% (which sandboxed launchers such as MSIX apps silently redirect).
# The plugin needs the bgutil token server too: its source ships in Tools\bgutil-server and SpotySync
# installs its npm packages with the bundled Deno on first use (node_modules is links and cannot be zipped).
$bgutilCache = Join-Path $cacheRoot "bgutil-ytdlp-pot-provider-2.0.2.zip"
$bgutilUrl = "https://github.com/Brainicism/bgutil-ytdlp-pot-provider/releases/download/2.0.2/bgutil-ytdlp-pot-provider.zip"
$bgutilSha256 = "a26b980bf6d8e8400d8f21445daa4d8381bf6183d20f0be309391f86b31e27ef"
if (-not (Test-Path -LiteralPath $bgutilCache) -or (Get-FileHash -LiteralPath $bgutilCache -Algorithm SHA256).Hash -ne $bgutilSha256) {
    Invoke-WebRequest -Uri $bgutilUrl -OutFile $bgutilCache -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $bgutilCache -Algorithm SHA256).Hash -ne $bgutilSha256) {
    throw "Downloaded bgutil-ytdlp-pot-provider SHA-256 verification failed."
}
$pluginRoot = Join-Path $toolsRoot "yt-dlp-plugins"
New-Item -ItemType Directory -Force -Path $pluginRoot | Out-Null
Copy-Item -LiteralPath $bgutilCache -Destination (Join-Path $pluginRoot "bgutil-ytdlp-pot-provider.zip")
$bgutilSourceCache = Join-Path $cacheRoot "bgutil-ytdlp-pot-provider-2.0.2-source.zip"
$bgutilSourceUrl = "https://github.com/Brainicism/bgutil-ytdlp-pot-provider/archive/refs/tags/2.0.2.zip"
$bgutilSourceSha256 = "df68ee6210e4960757bceae298927fb2ebd4d5cd9d390f9ff3a8287b0d2d1054"
if (-not (Test-Path -LiteralPath $bgutilSourceCache) -or (Get-FileHash -LiteralPath $bgutilSourceCache -Algorithm SHA256).Hash -ne $bgutilSourceSha256) {
    Invoke-WebRequest -Uri $bgutilSourceUrl -OutFile $bgutilSourceCache -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $bgutilSourceCache -Algorithm SHA256).Hash -ne $bgutilSourceSha256) {
    throw "Downloaded bgutil-ytdlp-pot-provider source SHA-256 verification failed."
}
$bgutilExtract = Join-Path $cacheRoot "bgutil-source"
if (Test-Path -LiteralPath $bgutilExtract) { Remove-Item -LiteralPath $bgutilExtract -Recurse -Force }
Expand-Archive -LiteralPath $bgutilSourceCache -DestinationPath $bgutilExtract
$bgutilRepo = Join-Path $bgutilExtract "bgutil-ytdlp-pot-provider-2.0.2"
Copy-Item -LiteralPath (Join-Path $bgutilRepo "server") -Destination (Join-Path $toolsRoot "bgutil-server") -Recurse
Copy-Item -LiteralPath (Join-Path $bgutilRepo "LICENSE") -Destination (Join-Path $toolsRoot "bgutil-server\LICENSE")
# No browser_options.txt is shipped: SpotySync then uses the Windows default browser's YouTube sign-in.
Copy-Item -LiteralPath (Join-Path $projectRoot "browser_options.example.txt") -Destination $toolsRoot

# The package must never inherit the developer/user's real tokens or downloader credentials.
$forbiddenNames = @("spotify-token.json", "spotify_credentials.txt", "SpotifyAuth.ini", "SpotySync.ini", "browser_options.txt", "liked-cache.json", "sync-state.json")
$leaked = Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object { $forbiddenNames -contains $_.Name }
if ($leaked) { throw "Credential-bearing file was copied into the clean package: $($leaked.FullName -join ', ')" }

Compress-Archive -LiteralPath $packageRoot -DestinationPath $stagingZipPath -CompressionLevel Optimal
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Move-Item -LiteralPath $stagingZipPath -Destination $zipPath
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

# Keep only the newest compressed release. The unpacked package, older releases,
# build cache, and compiler outputs are all reproducible and needlessly large.
# Only SpotySync-* packages are removed: loose tools or files the user keeps in dist stay.
Get-ChildItem -LiteralPath $distRoot -Force | Where-Object { $_.FullName -ne $zipPath -and $_.Name -like "SpotySync-*" } | ForEach-Object {
    Assert-ChildPath $_.FullName $distRoot
    Remove-Item -LiteralPath $_.FullName -Recurse -Force
}
# bin\ and obj\ are left alone: the locally built app folder holds the user's settings, login and data.
if (Test-Path -LiteralPath $cacheRoot) { Remove-Item -LiteralPath $cacheRoot -Recurse -Force }

Write-Host "ZIP:              $zipPath"
Write-Host "ZIP SHA-256:      $zipHash"
