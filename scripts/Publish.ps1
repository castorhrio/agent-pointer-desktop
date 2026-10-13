param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$NsisCompiler = 'makensis.exe'
)

$ErrorActionPreference = 'Stop'
if ($Runtime -eq 'win-x64') {
    $compiler = (Get-Command $NsisCompiler -ErrorAction Stop).Source
}
$repoRoot = Split-Path $PSScriptRoot -Parent
$artifactRoot = Join-Path $repoRoot 'artifacts'
$packageId = [Guid]::NewGuid().ToString('N')
$staging = Join-Path $artifactRoot ('package-' + $packageId)
$cliStaging = Join-Path $artifactRoot ('cli-' + $packageId)
[IO.Directory]::CreateDirectory($staging) | Out-Null

& dotnet publish (Join-Path $repoRoot 'src/AgentPointer.Cli/AgentPointer.Cli.csproj') `
    -c Release -r $Runtime --self-contained true -p:PublishReadyToRun=false -o $cliStaging
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
& dotnet publish (Join-Path $repoRoot 'src/AgentPointer.Tray/AgentPointer.Tray.csproj') `
    -c Release -r $Runtime --self-contained true -p:PublishReadyToRun=false -o $staging
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
# Keep the desktop runtime: WinForms-only publishing can include facade assemblies
# with the same names as the WPF runtime's full implementations (WindowsBase.dll).
Get-ChildItem -LiteralPath $cliStaging -Filter 'agentpointer.*' -File | Copy-Item -Destination $staging -Force

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1') -Destination $staging
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.cmd') -Destination $staging
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $staging
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.zh-CN.md') -Destination $staging
$previewFiles = @('desktop-preview.png', 'overlay-preview.png')
[IO.Directory]::CreateDirectory((Join-Path $staging 'docs')) | Out-Null
foreach ($previewFile in $previewFiles) {
    $previewSource = Join-Path $repoRoot ('docs/' + $previewFile)
    if (Test-Path -LiteralPath $previewSource) {
        Copy-Item -LiteralPath $previewSource -Destination (Join-Path $staging 'docs')
    }
}
$programSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src/AgentPointer.Cli/Program.cs') -Encoding UTF8 -Raw
$version = [regex]::Match($programSource, 'public const string Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw 'Could not read the CLI version.' }
$archive = Join-Path $artifactRoot ("agentpointer-$version-$Runtime.zip")
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -Force
$releaseFiles = @($archive)
if ($Runtime -eq 'win-x64') {
    $uninstallFiles = Join-Path $artifactRoot ('uninstall-' + $packageId + '.nsh')
    $uninstallCommands = @(Get-ChildItem -LiteralPath $staging -Recurse -File | ForEach-Object {
        'Delete "$INSTDIR\' + $_.FullName.Substring($staging.Length + 1).Replace('$', '$$') + '"'
    })
    $uninstallCommands += @(Get-ChildItem -LiteralPath $staging -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
        'RMDir "$INSTDIR\' + $_.FullName.Substring($staging.Length + 1).Replace('$', '$$') + '"'
    })
    [IO.File]::WriteAllText($uninstallFiles, (($uninstallCommands -join "`r`n") + "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
    $setup = Join-Path $artifactRoot "agentpointer-$version-$Runtime-setup.exe"
    & $compiler /V2 /WX /INPUTCHARSET UTF8 "/DPACKAGE_DIR=$staging" "/DOUTPUT_FILE=$setup" "/DVERSION=$version" `
        "/DAPP_ICON=$(Join-Path $repoRoot 'src/AgentPointer.Tray/app.ico')" "/DUNINSTALL_FILES=$uninstallFiles" (Join-Path $PSScriptRoot 'Installer.nsi')
    if ($LASTEXITCODE -ne 0) { throw 'Windows installer compilation failed.' }
    $releaseFiles += $setup
}
$checksumPath = Join-Path $artifactRoot "agentpointer-$version-$Runtime.sha256"
$checksums = @($releaseFiles | ForEach-Object {
    (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_)
})
[IO.File]::WriteAllText($checksumPath, (($checksums -join "`r`n") + "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Output $releaseFiles
Write-Output $checksumPath
