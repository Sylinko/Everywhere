param(
    [Parameter(Mandatory = $true)][string] $ApplicationPath,
    [Parameter(Mandatory = $true)][string] $FailureMessage,
    [Parameter(Mandatory = $false)][int] $ParentProcessId = 0
)

$ErrorActionPreference = 'Stop'
$workspace = $PSScriptRoot
$appDir = [IO.Path]::GetDirectoryName($ApplicationPath)
$stagingDir = [IO.Path]::Combine($workspace, 'new')
$backupDir = [IO.Path]::Combine($workspace, 'old')
$logPath = [IO.Path]::Combine($workspace, 'update.log')
$hasClosed = $false
$hasBackup = $false
$hasInstalled = $false

function Write-Log([string] $message) {
    try {
        [IO.File]::AppendAllText($logPath, "$(Get-Date -Format o) $message`r`n")
    } catch {
        # A logging failure must not interrupt replacement or recovery.
        Write-Warning $_.Exception.Message
    }
}

try {
    # Release the helper's working-directory handle so its workspace can be removed after success.
    [Environment]::CurrentDirectory = [IO.Path]::GetDirectoryName($workspace)
    Set-Location -LiteralPath ([Environment]::CurrentDirectory)

    # Preparation already validated the package. Check its continued presence before closing Main.
    if (-not [IO.File]::Exists([IO.Path]::Combine($stagingDir, [IO.Path]::GetFileName($ApplicationPath)))) {
        throw 'The prepared application executable is missing.'
    }

    if ($ParentProcessId -gt 0) {
        Write-Log "Waiting for the previous application process ($ParentProcessId) to exit."
        Wait-Process -Id $ParentProcessId -Timeout 15 -ErrorAction SilentlyContinue
        if ($null -ne (Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue)) {
            throw "Previous application process $ParentProcessId did not exit within 15 seconds. No files were replaced."
        }
    }
    $hasClosed = $true

    # Directory.Move fails if the destination exists; it never nests or merges directories.
    Write-Log 'Moving the current version to backup.'
    $moveAttempt = 0
    while ($true) {
        try {
            [IO.Directory]::Move($appDir, $backupDir)
            break
        } catch {
            $moveAttempt++
            $isTransientLock =
                $_.Exception -is [IO.IOException] -or
                $_.Exception -is [UnauthorizedAccessException] -or
                $_.Exception.InnerException -is [IO.IOException] -or
                $_.Exception.InnerException -is [UnauthorizedAccessException]
            if ($moveAttempt -ge 20 -or -not $isTransientLock) {
                throw
            }
            Start-Sleep -Milliseconds 150
        }
    }
    $hasBackup = $true
    [IO.Directory]::Move($stagingDir, $appDir)
    $hasInstalled = $true

    Write-Log 'Starting the updated application.'
    Start-Process -FilePath $ApplicationPath -ArgumentList '--ui' -WorkingDirectory ([WildcardPattern]::Escape($appDir))
} catch {
    Write-Log "Update failed: $($_.Exception.Message)"
    try {
        # Undo only completed moves. Never delete the application to make rollback possible.
        if ($hasBackup) {
            if ($hasInstalled) {
                [IO.Directory]::Move($appDir, $stagingDir)
            }
            [IO.Directory]::Move($backupDir, $appDir)
        }
        if ($hasClosed) {
            Start-Process -FilePath $ApplicationPath -ArgumentList '--ui' -WorkingDirectory ([WildcardPattern]::Escape($appDir))
        }
    } catch {
        Write-Log "Recovery failed; files have been retained: $($_.Exception.Message)"
    }

    # Main may no longer exist. Keep the log and recovery files, and report outside its Toast hosts.
    $shell = New-Object -ComObject WScript.Shell
    $null = $shell.Popup($FailureMessage, 0, 'Everywhere', 16)
    exit 1
}

# Launch success is not a health check. Cleanup is best effort and must never trigger rollback.
Write-Log 'The updated application process was launched.'
try {
    [IO.Directory]::Delete($workspace, $true)
} catch {
    Write-Log "Could not clean update files: $($_.Exception.Message)"
}
exit 0
