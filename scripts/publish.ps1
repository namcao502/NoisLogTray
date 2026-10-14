<#
Rebuilds the tray app into bin\publish\win-x64 even while that copy is running.

A running exe or a loaded DLL (NoisLogTray.exe, conpty.dll, x64\OpenConsole.exe, ...) cannot be overwritten, but Windows
lets it be renamed. So the build goes to a staging folder first, and each file that is in use is renamed to *.old and
the new one put in its place. The running app keeps working on its old copy, terminals and Claude sessions included;
the new build starts the next time the app is opened. *.old files are deleted by the next run once nothing holds them.

  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1            # update the files, keep the app running
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Restart   # also stop the running app and start the new one
#>
param(
    # Stops the running app (its terminals end; Claude sessions resume with --continue) and starts the new build.
    [switch]$Restart
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$targetDir = Join-Path $repoRoot 'bin\publish\win-x64'
$stagingDir = Join-Path $repoRoot 'bin\publish\staging'
$exePath = Join-Path $targetDir 'NoisLogTray.exe'

if (Test-Path $stagingDir) {
    Remove-Item $stagingDir -Recurse -Force
}
& dotnet publish (Join-Path $repoRoot 'NoisLogTray.csproj') -p:PublishProfile=win-x64 "-p:PublishDir=$stagingDir\"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE); bin\publish\win-x64 is unchanged."
}

# Swapped-out files from earlier runs; one still held by a running process is left for next time.
if (Test-Path $targetDir) {
    Get-ChildItem $targetDir -Recurse -File -Filter '*.old' | ForEach-Object {
        try {
            [System.IO.File]::Delete($_.FullName)
        }
        catch {
        }
    }
}

$copiedCount = 0
$swappedCount = 0
foreach ($file in Get-ChildItem $stagingDir -Recurse -File) {
    $relativePath = $file.FullName.Substring($stagingDir.Length + 1)
    $targetPath = Join-Path $targetDir $relativePath
    [System.IO.Directory]::CreateDirectory((Split-Path $targetPath)) | Out-Null
    try {
        [System.IO.File]::Copy($file.FullName, $targetPath, $true)
        $copiedCount++
    }
    catch [System.IO.IOException], [System.UnauthorizedAccessException] {
        # In use: put the new copy beside it, move the running one aside, then give the new one its name.
        $incomingPath = "$targetPath.new"
        $asidePath = "$targetPath.$(Get-Date -Format 'yyyyMMddHHmmss').old"
        [System.IO.File]::Copy($file.FullName, $incomingPath, $true)
        [System.IO.File]::Move($targetPath, $asidePath)
        try {
            [System.IO.File]::Move($incomingPath, $targetPath)
        }
        catch {
            [System.IO.File]::Move($asidePath, $targetPath)
            throw
        }
        $swappedCount++
    }
}
Remove-Item $stagingDir -Recurse -Force

$running = Get-Process -Name 'NoisLogTray' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and [System.IO.Path]::GetDirectoryName($_.Path) -ieq $targetDir }
Write-Host "Published to $targetDir ($copiedCount file(s) copied, $swappedCount in-use file(s) swapped)."

if ($Restart) {
    if ($running) {
        Write-Host 'Stopping the running app...'
        $running | Stop-Process -Force
        $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    }
    Start-Process -FilePath $exePath -WorkingDirectory $targetDir
    Write-Host 'Started the new build.'
}
elseif ($running) {
    Write-Host 'The app is still running the previous build. Quit it from the tray and open it again to use the new one.'
}
