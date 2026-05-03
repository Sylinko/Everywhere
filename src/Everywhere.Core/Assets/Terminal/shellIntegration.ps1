# ---------------------------------------------------------------------------------------------
# Everywhere Shell Integration for PowerShell
# Simplified version of VS Code's shellIntegration.ps1
# Emits OSC 633 markers: A (PromptStart), B (CommandReady), E (CommandLine), C (CommandExecuted), D (CommandFinished)
# ---------------------------------------------------------------------------------------------

# Prevent installing more than once per session
if ((Test-Path variable:global:__EverywhereState) -and $null -ne $Global:__EverywhereState.OriginalPrompt) {
	return;
}

# Disable shell integration when the language mode is restricted
if ($ExecutionContext.SessionState.LanguageMode -ne "FullLanguage") {
	return;
}

# Disable all history providers to prevent them from interfering with our history tracking
Get-PSReadlineOption | ForEach-Object {
    if ($_.HistorySaveStyle -ne "SaveNothing") {
        Set-PSReadlineOption -HistorySaveStyle SaveNothing
    }
    if ($_.HistorySavePath -ne $null) {
        Set-PSReadlineOption -HistorySavePath $null
    }
}

$Global:__EverywhereState = @{
	OriginalPrompt = $function:Prompt
	LastHistoryId = -1
	IsInExecution = $false
	Nonce = $env:EVERYWHERE_NONCE
}

# Clear the nonce from environment
$env:EVERYWHERE_NONCE = $null

function Global:Prompt() {
	$FakeCode = [int]!$global:?
	Set-StrictMode -Off
	$LastHistoryEntry = Get-History -Count 1
	$Result = ""

	# Command finished
	if ($Global:__EverywhereState.LastHistoryId -ne -1 -and ($Global:__EverywhereState.HasPSReadLine -eq $false -or $Global:__EverywhereState.IsInExecution -eq $true)) {
		$Global:__EverywhereState.IsInExecution = $false
		if ($LastHistoryEntry.Id -eq $Global:__EverywhereState.LastHistoryId) {
			# No new command was executed (e.g. ctrl+c, enter on empty line)
			$Result += "$([char]0x1b)]633;D`a"
		}
		else {
			# Command finished with exit code
			$Result += "$([char]0x1b)]633;D;$FakeCode`a"
		}
	}

	# Prompt started
	$Result += "$([char]0x1b)]633;A`a"

	# Run the original prompt
	if ($FakeCode -ne 0) {
		Write-Error "failure" -ea ignore
	}
	$OriginalPrompt = $Global:__EverywhereState.OriginalPrompt.Invoke()
	$Result += $OriginalPrompt

	# Command ready (prompt finished rendering)
	$Result += "$([char]0x1b)]633;B`a"
	$Global:__EverywhereState.LastHistoryId = $LastHistoryEntry.Id
	return $Result
}

# Wrap PSConsoleHostReadLine to emit E (CommandLine) and C (CommandExecuted)
$Global:__EverywhereState.HasPSReadLine = $false
if (Get-Module -Name PSReadLine) {
	$Global:__EverywhereState.HasPSReadLine = $true

	$Global:__EverywhereState.OriginalPSConsoleHostReadLine = $function:PSConsoleHostReadLine
	function Global:PSConsoleHostReadLine {
		$CommandLine = $Global:__EverywhereState.OriginalPSConsoleHostReadLine.Invoke()
		$Global:__EverywhereState.IsInExecution = $true

		# Command line
		$Result = "$([char]0x1b)]633;E;$CommandLine`a"
		# Command executed
		$Result += "$([char]0x1b)]633;C`a"

		[Console]::Write($Result)
		$CommandLine
	}
}
