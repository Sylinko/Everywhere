# ---------------------------------------------------------------------------------------------
# Everywhere Shell Integration for Bash
# Emits OSC 633 markers: A (PromptStart), B (CommandReady), E (CommandLine), C (CommandExecuted), D (CommandFinished)
# ---------------------------------------------------------------------------------------------

# Prevent installing more than once
if [ -n "$EVERYWHERE_SHELL_INTEGRATION" ]; then
    return
fi
EVERYWHERE_SHELL_INTEGRATION=1

__everywhere_disable_history() {
    set +o history
    set +H
    HISTFILE=/dev/null
    HISTSIZE=0
    HISTFILESIZE=0
    history -c 2>/dev/null || true
}

__everywhere_disable_history

__everywhere_last_history_id=""
__everywhere_in_command_execution=""
__everywhere_in_prompt=""

__everywhere_escape_value() {
    local str="$1"
    local out="" byte val
    local i
    for (( i = 0; i < ${#str}; ++i )); do
        byte="${str:$i:1}"
        val=$(printf "%d" "'$byte")
        if (( val < 31 )); then
            out+="$(printf '\\x%02x' "$val")"
        elif [ "$byte" = "\\" ]; then
            out+="\\\\"
        elif [ "$byte" = ";" ]; then
            out+="\\x3b"
        else
            out+="$byte"
        fi
    done
    printf '%s' "$out"
}

__everywhere_prompt_start() {
    printf '\e]633;A\a'
}

__everywhere_prompt_end() {
    printf '\e]633;B\a'
}

__everywhere_update_prompt() {
    if [[ -z "$__everywhere_custom_PS1" || "$__everywhere_custom_PS1" != "$PS1" ]]; then
        __everywhere_original_PS1="$PS1"
        __everywhere_custom_PS1="\[$(__everywhere_prompt_start)\]$__everywhere_original_PS1\[$(__everywhere_prompt_end)\]"
        PS1="$__everywhere_custom_PS1"
    fi
}

__everywhere_get_trap() {
    builtin local -a terms
    builtin eval "terms=( $(trap -p "${1:-DEBUG}") )"
    builtin printf '%s' "${terms[2]:-}"
}

__everywhere_restore_exit_code() {
    return "$1"
}

__everywhere_prompt_cmd() {
    local status="$?"
    __everywhere_in_prompt="1"
    __everywhere_disable_history

    if [ -n "$__everywhere_in_command_execution" ]; then
        __everywhere_in_command_execution=""
        printf '\e]633;D;%s\a' "$status"
    fi

    local cmd
    for cmd in "${__everywhere_original_prompt_commands[@]}"; do
        if [ -n "$cmd" ]; then
            __everywhere_restore_exit_code "$status"
            eval "$cmd"
        fi
    done

    __everywhere_update_prompt
    __everywhere_in_prompt=""
}

__everywhere_preexec() {
    if [ -n "$__everywhere_in_prompt" ] || [ -n "$__everywhere_in_command_execution" ]; then
        return
    fi
    if [[ "$BASH_COMMAND" == __everywhere_prompt_cmd* ]]; then
        return
    fi
    __everywhere_in_command_execution="1"
    printf '\e]633;E;%s\a' "$(__everywhere_escape_value "$BASH_COMMAND")"
    printf '\e]633;C\a'
}

# Capture and wrap PROMPT_COMMAND (supporting Bash 5.1+ array PROMPT_COMMAND)
if [[ "$(declare -p PROMPT_COMMAND 2>/dev/null)" == "declare -a"* ]]; then
    __everywhere_original_prompt_commands=("${PROMPT_COMMAND[@]}")
    unset PROMPT_COMMAND
elif [[ -n "${PROMPT_COMMAND:-}" && "${PROMPT_COMMAND:-}" != "__everywhere_prompt_cmd" ]]; then
    __everywhere_original_prompt_commands=("$PROMPT_COMMAND")
    unset PROMPT_COMMAND
else
    __everywhere_original_prompt_commands=()
fi
PROMPT_COMMAND="__everywhere_prompt_cmd"

# Preserve any existing DEBUG trap configured by user rc files
__everywhere_dbg_trap="$(__everywhere_get_trap DEBUG)"
if [[ -z "$__everywhere_dbg_trap" ]]; then
    trap '__everywhere_preexec' DEBUG
elif [[ "$__everywhere_dbg_trap" != '__everywhere_preexec' && "$__everywhere_dbg_trap" != '__everywhere_preexec_all' ]]; then
    __everywhere_preexec_all() {
        __everywhere_preexec
        builtin eval "${__everywhere_dbg_trap}"
    }
    trap '__everywhere_preexec_all' DEBUG
fi
