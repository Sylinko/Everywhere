# ---------------------------------------------------------------------------------------------
# Everywhere Shell Integration for Zsh
# Simplified version of VS Code's shellIntegration-rc.zsh
# Emits OSC 633 markers: A (PromptStart), B (CommandReady), E (CommandLine), C (CommandExecuted), D (CommandFinished)
# ---------------------------------------------------------------------------------------------

# Prevent installing more than once
if [ -n "$EVERYWHERE_SHELL_INTEGRATION" ]; then
	return
fi
EVERYWHERE_SHELL_INTEGRATION=1

# Nonce for command verification
__everywhere_nonce="$EVERYWHERE_NONCE"
unset EVERYWHERE_NONCE

__everywhere_in_command_execution=""
__everywhere_current_command=""
__everywhere_prior_prompt=""
__everywhere_prior_rprompt=""

# Escape value for OSC sequences
__everywhere_escape_value() {
	builtin emulate -L zsh
	builtin local LC_ALL=C str="$1" i byte token out='' val
	for (( i = 0; i < ${#str}; ++i )); do
		byte="${str:$i:1}"
		val=$(printf "%d" "'$byte")
		if (( val < 31 )); then
			token=$(printf "\\\\x%02x" "'$byte")
		elif [ "$byte" = "\\" ]; then
			token="\\\\"
		elif [ "$byte" = ";" ]; then
			token="\\x3b"
		else
			token="$byte"
		fi
		out+="$token"
	done
	builtin print -r -- "$out"
}

__everywhere_prompt_start() {
	builtin printf '\e]633;A\a'
}

__everywhere_prompt_end() {
	builtin printf '\e]633;B\a'
}

__everywhere_command_output_start() {
	builtin printf '\e]633;E;%s\a' "$(__everywhere_escape_value "${__everywhere_current_command}")"
	builtin printf '\e]633;C\a'
}

__everywhere_command_complete() {
	if [[ "$__everywhere_current_command" == "" ]]; then
		builtin printf '\e]633;D\a'
	else
		builtin printf '\e]633;D;%s\a' "$__everywhere_status"
	fi
}

# Update PS1/RPROMPT to wrap with markers
__everywhere_update_prompt() {
	__everywhere_prior_prompt="$PS1"
	__everywhere_in_command_execution=""
	PS1="%{$(__everywhere_prompt_start)%}$PS1%{$(__everywhere_prompt_end)%}"
	if [ -n "$RPROMPT" ]; then
		__everywhere_prior_rprompt="$RPROMPT"
	fi
}

__everywhere_precmd() {
	builtin local __everywhere_status="$?"
	if [ -z "${__everywhere_in_command_execution-}" ]; then
		__everywhere_command_output_start
	fi

	__everywhere_command_complete "$__everywhere_status"
	__everywhere_current_command=""

	if [ -n "$__everywhere_in_command_execution" ]; then
		__everywhere_update_prompt
	fi
}

__everywhere_preexec() {
	PS1="$__everywhere_prior_prompt"
	if [ -n "$RPROMPT" ]; then
		RPROMPT="$__everywhere_prior_rprompt"
	fi
	__everywhere_in_command_execution="1"
	__everywhere_current_command=$1
	__everywhere_command_output_start
}

autoload -Uz add-zsh-hook
add-zsh-hook precmd __everywhere_precmd
add-zsh-hook preexec __everywhere_preexec
