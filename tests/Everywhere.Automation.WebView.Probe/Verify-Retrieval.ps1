param([string]$Endpoint = 'http://127.0.0.1:5197/mcp', [string[]]$Addresses = @(), [switch]$ConnectOnly)

# Explicit Agent-style integration check. Start the Probe separately; this script only controls its TestApp.
$ErrorActionPreference = 'Stop'
$headers = @{ Accept = 'application/json, text/event-stream' }
$requestId = 0
function Invoke-Rpc([string]$Method, $Parameters) {
    $script:requestId++
    $body = @{ jsonrpc = '2.0'; id = $script:requestId; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 20 -Compress
    $response = Invoke-WebRequest -Uri $Endpoint -Method Post -Headers $headers -ContentType 'application/json' -Body $body
    if ($response.Headers['Mcp-Session-Id']) { $headers['Mcp-Session-Id'] = [string]($response.Headers['Mcp-Session-Id'] | Select-Object -First 1) }
    $json = [string]$response.Content
    if ($json.StartsWith('event:') -or $json.StartsWith('data:')) {
        $json = (($json -split "`n" | Where-Object { $_.StartsWith('data:') } | Select-Object -Last 1) -replace '^data:\s*', '')
    }
    $result = $json | ConvertFrom-Json -Depth 30
    if ($result.error) { throw ($result.error | ConvertTo-Json -Compress) }
    return $result.result
}
function Invoke-Probe([string]$Name, $Arguments) {
    $result = Invoke-Rpc 'tools/call' @{ name = $Name; arguments = $Arguments }
    if ($result.isError) { throw ($result.content | ConvertTo-Json -Compress) }
    return ($result.content | Where-Object type -eq 'text' | ForEach-Object text) -join "`n"
}
Invoke-Rpc 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'retrieval-verification'; version = '1' } } | Out-Null
if ($ConnectOnly) { return }
Invoke-Probe 'navigate' @{ address = 'https://example.com/' } | Write-Output
Invoke-Probe 'query_visual' @{ target = 'root'; directions = 'child' } | Out-Null
$before = Invoke-Probe 'get_probe_status' @{} | ConvertFrom-Json
if ($before.retainedTurnCount -ne 1) { throw 'An unscoped call should complete one temporary turn.' }
$tree = Invoke-Probe 'query_visual' @{ target = 'root'; directions = 'child'; shouldStartNewTurn = $true }
Write-Output $tree
$documentId = [regex]::Match($tree, '<Document id=(\d+)').Groups[1].Value
if (!$documentId) { throw 'No Document was exposed.' }
for ($index = 0; $index -lt 10; $index++) {
    Invoke-Probe 'query_visual' @{ target = 'root'; directions = 'none' } | Out-Null
}
$during = Invoke-Probe 'get_probe_status' @{} | ConvertFrom-Json
if ($during.retainedTurnCount -ne $before.retainedTurnCount) { throw 'Calls unexpectedly advanced the persistent turn.' }
$page = Invoke-Probe 'read_visual_text' @{ target = [int]$documentId; limit = 32 }
if ($page -notmatch 'Example Domain') { throw 'The original Document did not survive the journey.' }
Write-Output $page
Invoke-Probe 'query_visual' @{ target = 'root'; directions = 'none'; shouldStartNewTurn = $true } | Out-Null
$after = Invoke-Probe 'get_probe_status' @{} | ConvertFrom-Json
if ($after.retainedTurnCount -ne $during.retainedTurnCount + 1) { throw 'Starting a turn did not complete the preceding turn.' }
Invoke-Probe 'diagnose_topology' @{} | Out-Null
Write-Output 'PASS: temporary calls, persistent multi-call retrieval, historical transition, and native topology capture.'

# Optional live journeys are observations, not golden assertions about third-party content.
foreach ($address in $Addresses) {
    try {
        Invoke-Probe 'navigate' @{ address = $address; settleMilliseconds = 2000 } | Out-Null
        $tree = Invoke-Probe 'query_visual' @{ target = 'root'; directions = 'child'; limit = 128; targetTokenBudget = 4096; shouldStartNewTurn = $true }
        $documentId = [regex]::Match($tree, '<Document id=(\d+)').Groups[1].Value
        $summary = [ordered]@{ address = $address; characters = $tree.Length; targets = [regex]::Matches($tree, '\bid=\d+').Count; offscreen = [regex]::Matches($tree, '\boffscreen\b').Count; document = $documentId; pages = @() }
        if ($documentId) {
            $narrow = Invoke-Probe 'query_visual' @{ target = $documentId; directions = 'child'; limit = 32; targetTokenBudget = 2048 }
            $summary.narrowCharacters = $narrow.Length
            $offset = 0
            for ($pageIndex = 0; $pageIndex -lt 4; $pageIndex++) {
                $page = Invoke-Probe 'read_visual_text' @{ target = [int]$documentId; offset = $offset; limit = 2048 }
                $next = [regex]::Match($page, '\bnext=(\d+)').Groups[1].Value
                $summary.pages += @{ offset = $offset; next = $next; characters = $page.Length; hasStatus = $page.Contains('status=') }
                if (!$next -or [int]$next -le $offset) { break }
                $offset = [int]$next
            }
            $followupId = [regex]::Match($narrow, '<(?:Hyperlink|Button|TextEdit) id=(\d+)').Groups[1].Value
            if ($followupId) {
                $followup = Invoke-Probe 'query_visual' @{ target = $followupId; directions = 'none'; limit = 8 }
                $summary.followupId = $followupId
                $summary.followupCharacters = $followup.Length
            }
        }
        $summary | ConvertTo-Json -Depth 8 -Compress | Write-Output
    }
    catch { @{ address = $address; error = $_.Exception.Message } | ConvertTo-Json -Compress | Write-Output }
}
