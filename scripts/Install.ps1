param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs/AgentPointer'),
    [switch]$NoPath,
    [switch]$NoShortcut,
    [switch]$NoLaunch,
    [switch]$InstallSkills,
    [switch]$NoSkill
)

$ErrorActionPreference = 'Stop'
$sourceExecutable = Join-Path $PSScriptRoot 'agentpointer.exe'
$sourceDesktop = Join-Path $PSScriptRoot 'AgentPointer.Desktop.exe'
if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf) -or
    -not (Test-Path -LiteralPath $sourceDesktop -PathType Leaf)) {
    throw 'Run Install.ps1 from a complete extracted AgentPointer desktop package.'
}
$destinationPath = [IO.Path]::GetFullPath($Destination)
if ($destinationPath.StartsWith($PSScriptRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose a destination outside the extracted package directory, or use the package directory itself.'
}
[IO.Directory]::CreateDirectory($destinationPath) | Out-Null
if ($destinationPath.TrimEnd('\') -ne $PSScriptRoot.TrimEnd('\')) {
    Get-ChildItem -LiteralPath $PSScriptRoot | Copy-Item -Destination $destinationPath -Recurse -Force
}
$executable = Join-Path $destinationPath 'agentpointer.exe'
$desktopExecutable = Join-Path $destinationPath 'AgentPointer.Desktop.exe'
& $executable --version
if ($LASTEXITCODE -ne 0) { throw 'Installed CLI did not start.' }

if (-not $NoPath) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @($userPath -split ';' | Where-Object { $_ })
    if (@($entries | ForEach-Object { $_.TrimEnd('\') }) -notcontains $destinationPath.TrimEnd('\')) {
        [Environment]::SetEnvironmentVariable('Path', (($entries + $destinationPath) -join ';'), 'User')
    }
    if (($env:Path -split ';').TrimEnd('\') -notcontains $destinationPath.TrimEnd('\')) {
        $env:Path = $env:Path.TrimEnd(';') + ';' + $destinationPath
    }
}
if ($InstallSkills -and -not $NoSkill) {
    & $executable install-skill --force
    if ($LASTEXITCODE -ne 0) { throw 'Skill installation failed.' }
}
if (-not $NoShortcut) {
    $programsDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
    if (-not $programsDirectory) { throw 'Could not locate your Start menu.' }
    $shortcutDirectory = Join-Path $programsDirectory 'Agent Pointer'
    [IO.Directory]::CreateDirectory($shortcutDirectory) | Out-Null
    $shortcutShell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shortcutShell.CreateShortcut((Join-Path $shortcutDirectory 'Agent Pointer.lnk'))
        $shortcut.TargetPath = $desktopExecutable
        $shortcut.WorkingDirectory = $destinationPath
        $shortcut.IconLocation = $desktopExecutable + ',0'
        $shortcut.Description = 'Let your AI agent show you where to act on Windows.'
        $shortcut.Save()
    } finally {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcutShell) | Out-Null
    }
}
Write-Output "Installed at $destinationPath. Open Agent Pointer to try a guide and install agent skills."
if (-not $NoLaunch) {
    Start-Process -FilePath $desktopExecutable -WorkingDirectory $destinationPath
}
