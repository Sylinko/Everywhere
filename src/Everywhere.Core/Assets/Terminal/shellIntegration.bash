# ---------------------------------------------------------------------------------------------
# Everywhere Shell Integration for Bash
# Emits OSC 633 markers: A (PromptStart), B (CommandReady), E (CommandLine), C (CommandExecuted), D (CommandFinished)
# ---------------------------------------------------------------------------------------------

# Prevent installing more than once
if [ -n "$EVERYWHERE_SHELL_INTEGRATION" ]; then
    return
fi
EVERYWHERE_SHELL_INTEGRATION=1

__everywhere_last_history_id=""
__everywhere_in_command_execution=""

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

__everywhere_precmd() {
    local status="$?"

    if [ -n "$__everywhere_in_command_execution" ]; then
        __everywhere_in_command_execution=""
        printf '\e]633;D;%s\a' "$status"
    fi
}

__everywhere_preexec() {
    __everywhere_in_command_execution="1"
    printf '\e]633;E;%s\a' "$(__everywhere_escape_value "$BASH_COMMAND")"
    printf '\e]633;C\a'
}

# Wrap PROMPT_COMMAND to emit markers
if [ -z "$PROMPT_COMMAND" ]; then
    PROMPT_COMMAND="__everywhere_precmd"
else
    PROMPT_COMMAND="__everywhere_precmd;${PROMPT_COMMAND}"
fi

# Use DEBUG trap for preexec-like behavior
trap '__everywhere_preexec' DEBUG
