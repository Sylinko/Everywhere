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
	Write-Host -NoNewline "$([char]0x1b)]633;P;IntegrationError=LanguageMode:$($ExecutionContext.SessionState.LanguageMode)$([char]7)"
	return;
}

$EverywhereUtf8NoBomEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $EverywhereUtf8NoBomEncoding
$OutputEncoding = $EverywhereUtf8NoBomEncoding

# Disable all history providers to prevent them from interfering with our history tracking
try {
    Get-PSReadlineOption | ForEach-Object {
        if ($_.HistorySaveStyle -ne "SaveNothing") {
            Set-PSReadlineOption -HistorySaveStyle SaveNothing
        }
    }

    # Fix for PSReadLine issue #496: LF bytes inside bracketed paste are
    # interpreted as Ctrl+Enter, whose default binding InsertLineAbove
    # reverses multi-line command order. Replace with plain newline insertion.
    Set-PSReadLineKeyHandler -Chord Ctrl+Enter -ScriptBlock {
        [Microsoft.PowerShell.PSConsoleReadLine]::Insert("`n")
    }
}
catch {}

$Global:__EverywhereState = @{
	OriginalPrompt = $function:Prompt
	LastHistoryId = -1
	LastError = $null
	IsInExecution = $false
	Nonce = $env:EVERYWHERE_NONCE
}

# Clear the nonce from environment
$env:EVERYWHERE_NONCE = $null

function Global:__EverywhereEscapeValue([string]$Value) {
	$Builder = [System.Text.StringBuilder]::new()
	for ($Index = 0; $Index -lt $Value.Length; $Index++) {
		$Char = $Value[$Index]
		$Code = [int][char]$Char
		if ($Code -lt 0x20) {
			[void]$Builder.Append("\x$($Code.ToString('x2'))")
		}
		elseif ($Char -eq "\") {
			[void]$Builder.Append("\\")
		}
		elseif ($Char -eq ";") {
			[void]$Builder.Append("\x3b")
		}
		else {
			[void]$Builder.Append($Char)
		}
	}

	$Builder.ToString()
}

function Global:Prompt() {
	$LastSuccess = $global:?
	$LastExit = $global:LASTEXITCODE
	$LastError = if ($global:Error.Count -gt 0) { $global:Error[0] } else { $null }
	Set-StrictMode -Off
	$LastHistoryEntry = Get-History -Count 1
	$FakeCode = 0
	if (-not $LastSuccess) {
		$HasNewError = $null -ne $LastError -and -not [object]::ReferenceEquals($LastError, $Global:__EverywhereState.LastError)
		if (-not $HasNewError) {
			if ($null -ne $LastExit -and [int]$LastExit -ne 0) {
				$FakeCode = [int]$LastExit
			}
			else {
				$FakeCode = 1
			}
		}
		elseif ($null -ne $LastError.Exception -and $LastError.Exception.GetType().Name -eq 'NativeCommandExitException') {
			$FakeCode = [int]$LastError.Exception.ExitCode
		}
		else {
			$FakeCode = 1
			if ($null -ne $LastExit -and [int]$LastExit -ne 0 -and $null -ne $LastHistoryEntry -and $null -ne $LastError.InvocationInfo) {
				try {
					$ParsedAst = [System.Management.Automation.Language.Parser]::ParseInput($LastHistoryEntry.CommandLine, [ref]$null, [ref]$null)
					$LastStatement = if ($null -ne $ParsedAst.EndBlock -and $ParsedAst.EndBlock.Statements.Count -gt 0) {
						$ParsedAst.EndBlock.Statements[$ParsedAst.EndBlock.Statements.Count - 1]
					}
					else {
						$null
					}
					if ($null -ne $LastStatement -and $LastError.InvocationInfo.ScriptPosition.Offset -lt $LastStatement.Extent.StartOffset) {
						$FakeCode = [int]$LastExit
					}
				}
				catch {}
			}
		}
	}
	$Global:__EverywhereState.LastError = $LastError
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
		$EscapedCommandLine = __EverywhereEscapeValue $CommandLine

		# Command line
		$Result = "$([char]0x1b)]633;E;$EscapedCommandLine`a"
		# Command executed
		$Result += "$([char]0x1b)]633;C`a"

		[Console]::Write($Result)
		$CommandLine
	}
}
