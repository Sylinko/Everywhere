# Execution Strategies and TerminalRun

状态日期：2026-05-27

## 调用方模型

当前执行 API：

```csharp
var execution = strategy.ExecuteAsync(
    session,
    command,
    shellType,
    TimeSpan.FromSeconds(30),
    cancellationToken);

await foreach (var run in execution.WithCancellation(cancellationToken))
{
    terminalRuns.Add(run);

    var displayBlock = new ChatPluginTerminalDisplayBlock(shellType, run, session);
    userInterface.DisplaySink.AppendBlock(displayBlock);

    try
    {
        await run.WaitAsync(cancellationToken);
    }
    finally
    {
        displayBlock.Complete(run.ExitCode);
    }
}
```

没有：

```text
TerminalSubmissionResult
TerminalExecution
ExecuteResult
```

结果对象就是 `TerminalRun`。

## ExecuteStrategy 基类

文件：`src/Everywhere.Core/Terminal/ExecuteStrategy.cs`

### DetectStrategyAsync

检测逻辑：

1. 订阅 `session.Parser.ShellIntegrationMarkerReceived`。
2. 持续 `session.ReadOrIdleAsync`。
3. 看到 `CommandReady(B)` 后返回 `RichExecuteStrategy`。
4. idle timeout 或 absolute timeout 后返回 `NoneExecuteStrategy`。

当前超时：

| 名称 | 默认值 | 用途 |
| --- | --- | --- |
| idle timeout | 3s | 输出安静且没有 B |
| absolute timeout | 10s | 启动输出过于嘈杂或迟迟不 ready |

检测使用同一个 `TerminalSession`，因此启动期间 parser 获得的终端模式会保留下来。

### ExecuteAsync

`ExecuteAsync` 是 channel-backed async iterator：

```text
RunScopeAsync -> ChannelWriter<TerminalRun>
caller        <- ChannelReader<TerminalRun>
```

这样 `TerminalRun` 可以在完成前被 yield 给调用方。UI 能立即创建 block，然后等待 `run.WaitAsync`。

为什么不能让 `await run.WaitAsync()` 阻塞 PTY reader：

```text
caller waits run
scope keeps reading PTY
parser keeps updating run.Output
run completes on D/timeout/cancel
caller resumes
```

如果没有后台 scope，调用方等待 run 会阻止读取 PTY，run 永远无法完成。

## TerminalRun

文件：`src/Everywhere.Core/Terminal/TerminalRun.cs`

`TerminalRun` 是一个简单的 domain object：

```csharp
public sealed class TerminalRun
{
    public TerminalLineBuffer Output { get; }
    public string OutputText { get; }
    public string CommandLine { get; }
    public int? ExitCode { get; }
    public Task WaitAsync(CancellationToken cancellationToken = default);
}
```

内部通过 `TaskCompletionSource` 表示生命周期：

| 内部方法 | 语义 |
| --- | --- |
| `Complete(exitCode)` | 正常完成 |
| `Timeout()` | 超时完成，保留已有 exit code |
| `Cancel()` | 调用方取消 |
| `Fail(exception)` | 执行异常 |
| `SetCommandLine(commandLine, append)` | Rich marker 更新命令文本 |

`ExitCode`：

| 模式 | 值 |
| --- | --- |
| Rich 收到 `D;<code>` | code |
| Rich 没有 code 或 fallback | null |
| None | null |

## RichExecuteStrategy

文件：`src/Everywhere.Core/Terminal/RichExecuteStrategy.cs`

Rich 依赖 OSC 633 marker。它尽量使用 shell 报告的精确边界，而不是 prompt regex。

### 启动

```text
subscribe marker handler
BeginCapture(fallbackBuffer)
SendCommandAsync
read loop
```

### 发送命令

单行命令：

```text
trim command
normalize newline to CR
append final CR
```

多行命令：

1. 如果 bracketed paste enabled：

```text
WritePasteAsync(script with LF)
WriteInputAsync("\r")
```

2. 如果 bracketed paste disabled：

```text
send non-empty lines one by one with CR
delay 100ms between lines
```

这是一种回退，不如 bracketed paste 精确，但比向不支持的 shell 注入 literal paste markers 安全。

### Marker handler

| Marker | 行为 |
| --- | --- |
| `B` | 记录日志，表示 ready |
| `E` | 保存 `_pendingCommandLine` |
| `C` | 创建或更新 active run，切 capture 到 `activeRun.Output` |
| `D` | EndCapture，Complete active run，设置 exit code |
| `A` | 如果已收到 D，记录 final prompt |

### 多个 C

某些 shell 或脚本结构可能在同一次用户提交中产生多个 `E/C`。当前策略在 active run 已存在时，将 pending command line 追加到同一个 run：

```text
run.CommandLine += "\n" + pendingCommandLine
```

输出仍按 parser feed 顺序写入同一个 `TerminalLineBuffer`，避免多段输出倒序。

### 静默命令不能靠 idle 完成

已修复的关键问题：

```sh
echo "Start: $(date +%H:%M:%S)"
python3 -c "import time; time.sleep(10)"
echo "End:   $(date +%H:%M:%S)"
```

旧行为：收到 `C` 后，2.1 秒无输出就误判完成，kill PTY，导致只看到 Start。

当前行为：

```text
activeRun != null
idle only logs once
continue waiting for D marker
```

也就是 Rich 模式中：

```text
C 后的 silence == command still running
D 才是 completion
```

### Rich fallback

如果发送命令后一直没有 `C`：

1. fallbackBuffer 会捕获发送后的终端画面。
2. 当 prompt-like line 或 fallback idle threshold 出现时退出。
3. 创建一个 synthetic `TerminalRun(script)`。
4. `OutputCleaner.StripCommandEchoAndPrompt(rawOutput, script)` 清理 echo/prompt。
5. `ExitCode = null`。

典型触发原因：

| 原因 | 例子 |
| --- | --- |
| shell line editor 未提交命令 | zsh history expansion 进入 `dquote>` |
| shell integration 脚本异常 | marker 缺失 |
| shell 崩溃或输出不完整 | 未收到 `C` |

## NoneExecuteStrategy

文件：`src/Everywhere.Core/Terminal/NoneExecuteStrategy.cs`

None 是 fallback 策略，适用于没有 shell integration 的 shell。

### 时序

```text
create TerminalRun(script)
yield run immediately
WaitForIdleAsync(5s, 200ms)
send Ctrl+C
WaitForIdleAsync(500ms, 200ms)
BeginCapture(run.Output)
send command
wait for output start
wait idle + prompt heuristic
EndCapture
Complete(null)
```

### 为什么先 Ctrl+C

None 模式无法通过 OSC marker 得知 shell 是否处于干净 prompt。启动后可能有残留输入或 line editor 状态。发送 Ctrl+C 是为了尽量回到 prompt。

### Prompt heuristic

None 用 `OutputCleaner.IsShellPrompt` 检查 `run.Output` 的最后几行。它不看 session startup output，避免启动 prompt 污染当前命令判断。

### None 的多行输入

None 不使用 bracketed paste。它把多行拆开，逐行发送 Enter：

```text
line1\r
delay
line2\r
delay
...
```

原因是没有 shell integration 时，不能可靠确认 bracketed paste 模式和 line editor ready 状态。

### None 的限制

| 能力 | 状态 |
| --- | --- |
| 获取输出 | heuristic |
| 去掉 echo/prompt | heuristic |
| 退出码 | 不可靠，保持 null |
| 长时间静默命令 | 可能被 idle heuristic 误判 |
| 复杂 TUI | 不保证 |

None 是兼容层，不是精确执行层。

## Timeout 和 cancellation

两种策略都接受 timeout 和 cancellation token。

### Timeout

timeout 代表命令执行最长等待时间。超时后：

1. 标记 timed out。
2. 尝试 `session.Pty.Kill()`。
3. 完成或取消 active run。

当前 `TerminalRun.Timeout()` 不抛异常，调用方会得到已捕获的输出和当前 exit code。

### Cancellation

调用方 cancellation 会：

1. 取消 async enumeration。
2. scope finally 中取消 linked token。
3. active run `Cancel()`。
4. 抛出 `OperationCanceledException` 给调用方。

## 输出给模型

调用方收集多个 run：

```csharp
var output = string.Join(
    "\n",
    terminalRuns
        .Select(run => run.OutputText)
        .Where(text => !string.IsNullOrEmpty(text)));
```

随后再做 token omit：

```csharp
TokenHelper.OmitTo(output, resultBuilder, 8000, "[... OUTPUT OMITTED ...]");
resultBuilder.TrimEnd().AppendLine().Append("Exit code: ").Append(exitCode);
```

当前最终 exit code 使用最后一个 run 的 exit code。

## 设计取舍

### 为什么 `TerminalRun` 不继承 ObservableObject

UI 实际需要的是输出行变化，而不是 run 对象属性频繁通知。`TerminalRun` 生命周期很简单，输出变化集中在 `TerminalLineBuffer`。

### 为什么 `ExecuteAsync` 直接返回 `IAsyncEnumerable<TerminalRun>`

调用方需要：

1. 尽早拿到 run。
2. 立刻创建 UI block。
3. 等待 run 完成。
4. 继续处理后续 run。

`IAsyncEnumerable<TerminalRun>` 正好表达这个生命周期，不需要额外 wrapper。

### 为什么 Rich fallback 仍存在

即使 Detect 已经看到了 `B`，后续执行仍可能因为 shell line editor、用户 rc、history expansion、脚本错误而缺失 `C/D`。fallback 可以让调用方至少看到终端实际画面，而不是永久等待。

