<#
.SYNOPSIS
Runs real HTTP SDK tests using an existing MockServer, WSL Containers, or Docker.
#>
[CmdletBinding()]
param(
    [ValidateSet('Auto', 'Existing', 'Wslc', 'Docker')][string]$Runtime = 'Auto',
    [string]$MockServerUrl = $env:EVERYWHERE_MOCKSERVER_URL,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$Filter = 'TestCategory=LlmIntegration',
    [string]$ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskRunId = [Guid]::NewGuid().ToString('N')
$taskImage = 'docker.io/mockserver/mockserver:8.0.0@sha256:b8426e0b3c8089d65928531376147067002e8be4e9f437210b10a0395aeff6ce'
$taskContainer = "everywhere-llm-$taskRunId"
$taskExecutable = $null
$hasOwnedContainer = $false
$taskPreviousUrl = $env:EVERYWHERE_MOCKSERVER_URL
$taskPreviousRequired = $env:EVERYWHERE_REQUIRE_MOCKSERVER
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $taskRoot "artifacts/llm-tests/$taskRunId" }
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
$taskResultName = "llm-$taskRunId.trx"

try {
    New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
    if (-not $MockServerUrl) {
        if ($Runtime -eq 'Existing') { throw 'Existing mode requires -MockServerUrl or EVERYWHERE_MOCKSERVER_URL.' }
        if ($Runtime -eq 'Auto') {
            if (Get-Command wslc -ErrorAction SilentlyContinue) { $Runtime = 'Wslc' }
            elseif (Get-Command docker -ErrorAction SilentlyContinue) { $Runtime = 'Docker' }
            else { throw 'Supply -MockServerUrl, or install wslc or Docker.' }
        }
        $taskExecutable = if ($Runtime -eq 'Wslc') { 'wslc' } else { 'docker' }
        $taskExecutable = (Get-Command $taskExecutable -ErrorAction Stop).Source
        Write-Host "Starting MockServer 8.0.0 with $Runtime (loopback, random host port)."
        # Own the random name before dispatch so unknown startup outcomes can be cleaned up.
        $hasOwnedContainer = $true
        & $taskExecutable run --detach --name $taskContainer --publish '127.0.0.1::1080' `
            --env MOCKSERVER_LOG_LEVEL=WARN --env MOCKSERVER_DASHBOARD_ANALYTICS_ENABLED=false $taskImage
        if ($LASTEXITCODE -ne 0) { throw "$Runtime failed to start MockServer. No alternate runtime was tried." }
        $taskInspection = & $taskExecutable inspect $taskContainer
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the owned MockServer container.' }
        $taskInfo = ($taskInspection -join "`n" | ConvertFrom-Json)[0]
        # wslc 3.0.1 places Ports at the root; Docker places it under NetworkSettings.
        $taskPorts = if ($Runtime -eq 'Wslc') { $taskInfo.Ports } else { $taskInfo.NetworkSettings.Ports }
        $taskPort = $taskPorts.'1080/tcp'[0].HostPort
        if (-not $taskPort) { throw 'The owned container did not expose its loopback port.' }
        $MockServerUrl = "http://127.0.0.1:$taskPort"
        $taskDeadline = [DateTime]::UtcNow.AddSeconds(90)
        $isReady = $false
        do {
            try {
                $taskStatus = Invoke-RestMethod -Method Put -Uri "$($MockServerUrl.TrimEnd('/'))/mockserver/status" -TimeoutSec 2
                if ($taskStatus.version -ne '8.0.0') { throw "Unexpected MockServer version: $($taskStatus.version)" }
                $isReady = $true
            }
            catch {
                if ([DateTime]::UtcNow -ge $taskDeadline) { throw "MockServer did not become ready: $_" }
                Start-Sleep -Milliseconds 250
            }
        } while (-not $isReady)
    }

    $env:EVERYWHERE_MOCKSERVER_URL = $MockServerUrl
    $env:EVERYWHERE_REQUIRE_MOCKSERVER = '1'
    Write-Host "Running SDK integration tests against $MockServerUrl."
    & dotnet test (Join-Path $taskRoot 'tests/Everywhere.AI.Tests/Everywhere.AI.Tests.csproj') `
        --configuration $Configuration --filter $Filter --logger "trx;LogFileName=$taskResultName" --results-directory $ResultsDirectory --nologo -p:ManagePackageVersionsCentrally=true `
        2>&1 | Tee-Object -FilePath (Join-Path $ResultsDirectory 'console.log') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "SDK integration tests failed. Results: $ResultsDirectory" }
    [xml]$taskResults = Get-Content -LiteralPath (Join-Path $ResultsDirectory $taskResultName)
    $taskCounters = $taskResults.TestRun.ResultSummary.Counters
    if ([int]$taskCounters.executed -eq 0 -or [int]$taskCounters.notExecuted -ne 0) {
        throw 'A required integration run must execute tests and contain no skipped tests.'
    }
    Write-Host "Passed $($taskCounters.passed) AI tests. Results: $ResultsDirectory"
}
catch {
    if ($hasOwnedContainer) {
        try {
            & $taskExecutable logs $taskContainer 2>&1 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'mockserver.log')
        }
        catch { Write-Warning "Could not save owned MockServer logs: $_" }
    }
    throw
}
finally {
    $env:EVERYWHERE_MOCKSERVER_URL = $taskPreviousUrl
    $env:EVERYWHERE_REQUIRE_MOCKSERVER = $taskPreviousRequired
    if ($hasOwnedContainer) {
        & $taskExecutable stop $taskContainer 2>&1 | Out-Host
        & $taskExecutable rm $taskContainer 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Warning "Could not remove owned test container $taskContainer." }
    }
}
