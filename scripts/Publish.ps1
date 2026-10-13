param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
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
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $staging
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
Write-Output $archive
