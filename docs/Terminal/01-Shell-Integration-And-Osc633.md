# Shell Integration and OSC 633

状态日期：2026-05-27

## 目标

Shell integration 的目标是让 shell 主动报告命令边界，而不是让 Everywhere 通过 prompt regex 猜测。

当前支持的 shell：

| Shell | 脚本 |
| --- | --- |
| PowerShell / pwsh | `src/Everywhere.Core/Assets/Terminal/shellIntegration.ps1` |
| zsh | `src/Everywhere.Core/Assets/Terminal/shellIntegration.zsh` |
| bash | `src/Everywhere.Core/Assets/Terminal/shellIntegration.bash` |

脚本由 `ShellIntegrationScript` 准备并通过 shell 启动参数或 wrapper rc 文件加载。

## OSC 633 marker

Everywhere 借鉴 VS Code 的 OSC 633 约定。marker 使用 OSC 序列：

```text
ESC ] 633 ; <marker> [ ; payload ] BEL
```

当前 marker：

| Marker | 名称 | 语义 |
| --- | --- | --- |
| `A` | PromptStart | prompt 开始渲染 |
| `B` | CommandReady | prompt 已结束，shell line editor 可以接收输入 |
| `E` | CommandLine | shell 报告即将执行的命令文本 |
| `C` | CommandExecuted | 命令开始执行，后续输出属于 active run |
| `D` | CommandFinished | 命令结束，可带退出码 |

典型 Rich 时序：

```text
A
B
E;<command>
C
<output bytes>
D;<exitCode>
A
B
```

## 为什么 Detect 等待 B

不能因为看到任意 `OSC 633` 就返回 Rich。启动阶段可能出现：

```text
D
A
?2004h
B
```

其中空 `D` 只是 shell 初始化状态，不代表命令 ready。`B` 才是安全输入点。检测逻辑：

1. 创建 `TerminalSession`。
2. 订阅 `ShellIntegrationMarkerReceived`。
3. 持续 `ReadOrIdleAsync`。
4. 直到看到 `CommandReady(B)` 返回 `RichExecuteStrategy`。
5. idle 或 absolute timeout 后 fallback 到 `NoneExecuteStrategy`。

## CommandLine payload 转义

`OSC 633;E` 的 command line payload 不能直接放任控制字符和分号：

| 原字符 | 编码 |
| --- | --- |
| 控制字符，例如 LF | `\xNN` |
| 分号 `;` | `\x3b` |
| 反斜杠 `\` | `\\` |

zsh/bash 脚本都会转义 command line。`TerminalParser` 在解析 `E` marker 时通过 `DecodeOsc633Value` 反解。

这修复过一个重要问题：

```text
输入:
pwd
ls -F

旧 CommandLine:
pwd\x0als -F

当前 CommandLine:
pwd
ls -F
```

## PowerShell 集成

PowerShell 脚本做几件事：

1. 禁用 PSReadLine history save：

```powershell
Set-PSReadlineOption -HistorySaveStyle SaveNothing
```

2. 重写 `Prompt`，输出：

```text
D
A
<original prompt>
B
```

3. 包装 `PSConsoleHostReadLine`，在命令提交时输出：

```text
E;<command>
C
```

4. 修正 PSReadLine 的 `Ctrl+Enter` 行为：

```powershell
Set-PSReadLineKeyHandler -Chord Ctrl+Enter -ScriptBlock {
    [Microsoft.PowerShell.PSConsoleReadLine]::Insert("`n")
}
```

这个修正很关键。Windows/ConPTY/PSReadLine 路径中，bracketed paste 内部的 LF 可能被解释成类似 `Ctrl+Enter` 的编辑动作，而默认 `Ctrl+Enter` 是 `InsertLineAbove`，会导致多行命令顺序反转。

## zsh 集成

zsh 脚本使用 hook：

| Hook | 用途 |
| --- | --- |
| `precmd` | 输出完成 marker，更新 prompt 包装 |
| `preexec` | 记录 command line，输出 `E/C` |
| `line-init` | prompt 完成后输出 bracketed paste enable 序列和 `B` |
| `zshaddhistory` | 阻止命令进入 history |

### B marker 为什么在 line-init

zsh 的 `OSC 633;B` 不应直接放进 `PS1`。如果 `B` 过早发出，bracketed paste 可能尚未真正启用，Detect 会返回 Rich，但 `session.Parser.IsBracketedPasteModeEnabled` 仍是 false。

当前顺序是：

```text
?2004h
OSC 633;B
```

因此 `B` 到达时，parser 已经看到了 bracketed paste mode。

### zsh 历史记录与 BANG_HIST

交互 zsh 默认可能启用 history expansion。命令：

```sh
echo "Process completed!"
```

在 history expansion 未禁用时可能进入 `dquote>`，导致没有 `E/C/D`，最终只能 fallback。

当前 zsh 集成做三层处理：

1. 环境变量层：

```text
HISTFILE=/dev/null
HISTSIZE=0
SAVEHIST=0
```

2. 脚本函数层：

```zsh
unsetopt BANG_HIST APPEND_HISTORY INC_APPEND_HISTORY INC_APPEND_HISTORY_TIME SHARE_HISTORY
HISTFILE=/dev/null
HISTSIZE=0
SAVEHIST=0
```

3. hook 层：

```zsh
__everywhere_zshaddhistory() {
    return 1
}
```

`zshaddhistory return 1` 阻止交互输入保存到 history。注意：zsh 实测可能仍显示 `HISTSIZE=1`，这是 zsh 自身限制；真正防止保存的是 `SAVEHIST=0`、`HISTFILE=/dev/null` 和 `zshaddhistory`。

## bash 集成

bash 脚本使用：

| 机制 | 用途 |
| --- | --- |
| `PROMPT_COMMAND` | prompt 前输出 `D` 并包装 prompt |
| `DEBUG` trap | 近似 preexec，输出 `E/C` |

当前 history 处理：

```bash
set +o history
set +H
HISTFILE=/dev/null
HISTSIZE=0
HISTFILESIZE=0
history -c 2>/dev/null || true
```

含义：

| 设置 | 作用 |
| --- | --- |
| `set +o history` | 禁用 bash history |
| `set +H` | 禁用 history expansion，即 `!` 展开 |
| `HISTFILE=/dev/null` | 防止写入用户文件 |
| `HISTSIZE=0` | 不保留内存历史 |
| `HISTFILESIZE=0` | 不保留文件历史 |
| `history -c` | 清空当前 session 历史 |

`__everywhere_disable_history` 会在脚本加载时执行，也会在 `precmd` 路径执行，防止用户 `.bashrc` 覆盖。

## ShellIntegrationScript

文件：`src/Everywhere.Core/Terminal/ShellIntegrationScript.cs`

### PowerShell

启动参数：

```text
-NoProfile -NoLogo -NoExit -Command "try { . '<script>' } catch {}"
```

PowerShell 不加载用户 profile，主要历史控制在 `shellIntegration.ps1` 中完成。

### zsh

zsh 使用 `ZDOTDIR` wrapper：

```text
ZDOTDIR=<Everywhere writable terminal wrapper dir>
USER_ZDOTDIR=<user home>
```

wrapper `.zshrc`：

```zsh
export EVERYWHERE_NONCE='<nonce>'
source '<shellIntegration.zsh>'
source '<user ~/.zshrc>'
```

因为用户 `.zshrc` 在集成脚本之后加载，history 与 prompt 相关设置可能被覆盖，所以集成脚本在 `precmd` 再次收束关键状态。

### bash

bash 使用 `--rcfile` 指向 wrapper `.bashrc`：

```bash
export EVERYWHERE_NONCE='<nonce>'
source '<shellIntegration.bash>'
source '<user ~/.bashrc>'
```

同理，集成脚本必须在 prompt 路径重复禁用 history。

## Bracketed paste

终端模式：

```text
ESC[?2004h  enable
ESC[?2004l  disable
```

输入格式：

```text
ESC[200~<payload>ESC[201~
```

当前规则：

1. `TerminalParser` 跟踪 `IsBracketedPasteModeEnabled`。
2. Rich 多行命令只有在该模式启用时才使用 bracketed paste。
3. paste payload 中统一使用 LF。
4. paste 结束后单独发送最终 Enter：

```text
WritePasteAsync(normalizedScript)
WriteInputAsync("\r")
```

不要把 `ESC[201~` 和 `\r` 拼在同一次概念性输入里。分开发送更贴近真实终端粘贴后按 Enter 的时序。

## 终端查询响应

shell 和 line editor 可能发起终端查询。`TerminalParser` 识别后通过 `TerminalResponseWriter` 回写。

当前重要响应：

| 查询 | 响应 |
| --- | --- |
| Primary DA `CSI c` | `ESC[?1;0c` |
| Secondary DA `CSI > c` | `ESC[>0;0;0c` |
| DSR cursor position `CSI 6 n` | `ESC[{row};{column}R` |
| Text area size `CSI 18 t` | `ESC[8;{rows};{columns}t` |

这些响应是 Detect 能快速看到 `B` marker 的基础之一，尤其是 PowerShell/PSReadLine 启动阶段。

## Exit code

只有 Rich 模式可以可靠取得退出码：

```text
OSC 633;D;<exitCode>
```

None 模式没有 shell-reported boundary，`ExitCode` 保持 `null`。

