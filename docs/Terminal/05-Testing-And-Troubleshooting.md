# Testing and Troubleshooting

状态日期：2026-05-27

## 常用验证命令

### 终端相关测试

```bash
dotnet test tests/Everywhere.Core.Tests/Everywhere.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~Everywhere.Core.Tests.Terminal"
```

当前基线：

```text
通过: 145
跳过: 4
失败: 0
总计: 149
```

### TestApp 构建

```bash
dotnet build tests/Everywhere.Terminal.TestApp/Everywhere.Terminal.TestApp.csproj --no-restore
```

当前基线：

```text
0 warnings
0 errors
```

### TestApp 交互执行

```bash
dotnet run --no-restore --project tests/Everywhere.Terminal.TestApp/Everywhere.Terminal.TestApp.csproj -- --shell zsh
dotnet run --no-restore --project tests/Everywhere.Terminal.TestApp/Everywhere.Terminal.TestApp.csproj -- --shell bash
dotnet run --no-restore --project tests/Everywhere.Terminal.TestApp/Everywhere.Terminal.TestApp.csproj -- --shell pwsh
```

输入多行命令时，TestApp 支持 JSON string 形式：

```text
"echo \"A\"\necho \"B\""
```

### 启动观察

```bash
dotnet run --no-restore --project tests/Everywhere.Terminal.TestApp/Everywhere.Terminal.TestApp.csproj -- --observe --shell zsh --duration 2500
```

观察输出包括：

1. PTY read chunk。
2. OSC 633 marker。
3. bracketed paste mode。
4. focus / win32 input mode。
5. terminal response。

## 测试分层

### Parser tests

文件：

```text
tests/Everywhere.Core.Tests/Terminal/TerminalParserTests.cs
```

覆盖：

1. printable text。
2. CR/LF/BS/TAB。
3. CSI 清行/清屏/光标移动。
4. OSC 633 marker。
5. command line payload 反转义。
6. terminal mode tracking。

### Line buffer tests

文件：

```text
tests/Everywhere.Core.Tests/Terminal/TerminalLineBufferTests.cs
```

覆盖：

1. 稳定行 id。
2. CR 覆盖。
3. trailing spaces 裁剪。
4. trailing empty live line 不进入 `CopyLines`。
5. BeginUpdate 合并事件。
6. erase/insert/delete。
7. max line trimming。

### Strategy tests

文件：

```text
tests/Everywhere.Core.Tests/Terminal/PtyExecutionTests.cs
```

包含两类：

| 类型 | 用途 |
| --- | --- |
| simulated PTY | 精确构造 marker 顺序和延迟 |
| real PTY | 验证真实 shell、真实 line editor、真实脚本 |

重要 real PTY 回归：

| 测试 | 覆盖 |
| --- | --- |
| `RichStrategy_Zsh_DisablesBangHistoryForDoubleQuotedExclamation` | zsh `!` 不进入 `dquote>` |
| `RichStrategy_Bash_DisablesHistoryAndHistoryExpansion` | bash history/histexpand 关闭 |
| `RichStrategy_CurrentPlatform_WithShellIntegration_ExecutesScenario` | 当前平台 Rich 单行/多行/续行 |

重要 simulated 回归：

| 测试 | 覆盖 |
| --- | --- |
| `DetectStrategy_WaitsForCommandReadyBeforeReturningRich` | Detect 等待 B |
| `RichStrategy_WaitsForCommandFinishedAfterSilentPeriod` | Rich C 后静默必须等 D |
| `RichStrategy_MultiLine_MultiC_Simulated_OutputPreservesOrder` | 多 C 输出顺序 |
| `RichStrategy_CommandLine_UsesOsc633E` | CommandLine 采用 E marker |

## 已解决问题与排障方式

### 1. CommandLine 显示 `\x0a`

症状：

```text
CommandLine:
pwd\x0als -F
```

原因：

shell integration 为了安全发送 OSC payload，会把 LF 编码为 `\x0a`。parser 旧逻辑未反解。

修复：

`TerminalParser.DecodeOsc633Value` 支持：

| 编码 | 结果 |
| --- | --- |
| `\xNN` | 对应字符 |
| `\\` | 反斜杠 |
| unknown escape | 原样保留 |

验证：

```text
Feed_DecodesEscapedShellIntegrationCommandLine
```

### 2. zsh 出现 `dquote>`

症状：

```text
echo "Process completed!"
dquote>
ExitCode: null
```

原因：

交互 zsh 的 history expansion 会处理双引号中的 `!`，导致 line editor 进入续行状态，命令没有进入 `preexec`，因此没有 `E/C/D`。

修复：

zsh 集成脚本：

1. `unsetopt BANG_HIST`。
2. `HISTFILE=/dev/null`。
3. `SAVEHIST=0`。
4. `zshaddhistory return 1`。
5. `precmd` 重复收束。

验证命令：

```sh
echo "Process completed!"
```

预期：

```text
Process completed!
ExitCode: 0
```

### 3. bash 需要避免 history

症状风险：

1. `!` 被 history expansion。
2. 用户命令写入 history 文件。
3. 当前 session history 保存敏感命令。

修复：

bash 集成脚本：

```bash
set +o history
set +H
HISTFILE=/dev/null
HISTSIZE=0
HISTFILESIZE=0
history -c
```

验证：

```bash
printf 'HISTFILE=%s\n' "${HISTFILE-<unset>}"
printf 'HISTSIZE=%s\n' "${HISTSIZE-<unset>}"
printf 'HISTFILESIZE=%s\n' "${HISTFILESIZE-<unset>}"
set -o | grep -E '^(history|histexpand)'
history
echo "Process completed!"
```

预期：

```text
HISTFILE=/dev/null
HISTSIZE=0
HISTFILESIZE=0
histexpand off
history off
Process completed!
```

### 4. 10 秒静默命令两三秒结束

症状：

```sh
echo "Start: $(date +%H:%M:%S)"
python3 -c "import time; time.sleep(10)"
echo "End:   $(date +%H:%M:%S)"
```

旧输出只有：

```text
Start: 20:13:27
```

原因：

Rich 旧逻辑在收到 `C` 后，如果 2.1 秒没有输出且没有 prompt match，就假设完成。这对静默命令是错误的。

修复：

只要 `_activeRun is not null`：

```text
idle -> continue waiting for D
```

验证：

```text
RichStrategy_WaitsForCommandFinishedAfterSilentPeriod
```

真实 TestApp 验证预期：

```text
Start: HH:MM:SS
End:   HH:MM:SS + 10s
ExitCode: 0
```

### 5. 输出多一行空行

症状：

```text
dearva

```

原因：

LF 后 buffer 内部会创建一个 live empty line。`GetText` 已裁剪，但 UI 旧 snapshot 可能保留这行。

修复：

`TerminalLineBuffer.CopyLines` 与 `GetText` 一样裁掉尾部空行，但不修改内部 `_lines`。

验证：

```text
CopyLines_OmitsTrailingEmptyLiveLine
```

### 6. 输出含大量尾部空格

症状：

```text
dearva                                                                                                                        
```

原因：

终端 repaint、清行、固定列输出可能留下右侧空格。

修复：

`AddLine` 和 `ReplaceLine` 统一 `TrimEnd(' ')`。

验证：

```text
Write_TrailingSpaces_AreTrimmedFromStoredLines
```

### 7. bracketed paste 状态错误

症状：

```text
CommandReady marker callback bracketedPaste=false
```

原因：

zsh 旧实现把 `OSC 633;B` 放在 prompt 字符串中，可能早于 zle 真正启用 bracketed paste。

修复：

zsh 在 `zle-line-init` 中：

```text
emit zle_bracketed_paste[1]
emit OSC 633;B
```

预期顺序：

```text
?2004h -> OSC 633;B
```

验证：

```text
DetectStrategy_WaitsForCommandReadyBeforeReturningRich
```

## Debug checklist

### 判断是 Rich 还是 None

看日志：

```text
[Detect] Shell Integration command-ready detected ... using Rich strategy
[Detect] Falling back to None strategy
```

### 判断 Rich 是否正常进入命令执行

正常 Rich 必须看到：

```text
E command=...
C
...
D exitCode=...
A
B
```

如果没有 `C`：

1. shell line editor 没提交命令。
2. history expansion 或 quoting 让 shell 进入续行 prompt。
3. shell integration hook 没执行。
4. bracketed paste 或发送时序有问题。

如果有 `C` 没有 `D`：

1. 命令仍在执行。
2. shell 卡住。
3. shell integration complete hook 失效。
4. 最终应由 timeout 处理。

### 判断是否是 UI 问题

比较：

```csharp
run.OutputText
```

和 UI `CodeBlock.Inlines`。

如果 `OutputText` 正确而 UI 错，问题在 `TerminalCodeBlockBridge` 或 `CopyLines`。

如果 `OutputText` 也错，问题在 parser capture 或 strategy。

### 判断是否是 parser 问题

看原始 output 是否包含：

```text
\r
CSI K
CSI J
cursor movement
OSC 633
```

如果原始输出靠 CR 覆盖，必须验证 `TerminalLineBuffer` 的最终行，而不是简单 string append。

## 何时添加 headless UI 测试

需要，但不要替代 PTY 策略测试。

适合 headless UI 的内容：

1. `TerminalCodeBlockBridge` 初始刷新。
2. buffer 多次变化后只替换 revision 变化的行。
3. max visible lines 滚动裁剪。
4. run completion 后最终刷新并取消订阅。
5. 不显示尾部空 live line。

不适合 headless UI 的内容：

1. OSC 633 marker 时序。
2. bracketed paste。
3. zsh/bash/pwsh history 行为。
4. PTY idle 和 timeout。

这些必须用 strategy tests 或 TestApp 真实验证。

