# Terminal Architecture Overview

状态日期：2026-05-27

## 目标

Everywhere 的终端插件使用 PTY 执行用户批准的 shell 脚本。它不是简单地启动一个进程并读取 stdout，而是在 PTY 的另一端扮演一个最小终端模拟器：

```text
shell stdout/stderr -> TerminalSession -> PtyTextDecoder -> TerminalParser
shell stdin         <- TerminalResponseWriter / command input / paste input
```

这样做的原因是交互 shell、PSReadLine、readline、zle、prompt framework 和 TUI 程序都会假设自己面对的是一个真实终端。它们会输出 VT/CSI/OSC 控制序列，也会发起终端查询。Everywhere 必须解析这些序列，维护输入模式，并在必要时回写终端响应。

当前设计的核心目标是：

1. PTY 字节流只有一个 reader。
2. `TerminalSession` 是一次 PTY 会话的唯一 IO 和 parser 所有者。
3. `TerminalParser` 负责 VT/CSI/OSC 解析、终端模式、终端响应和捕获映射。
4. `ExecuteStrategy.ExecuteAsync` 直接返回 `IAsyncEnumerable<TerminalRun>`。
5. 每个 `TerminalRun` 表示一次 shell 报告或 fallback 合成的命令运行。
6. 每个 `TerminalRun` 只拥有一个 bounded `TerminalLineBuffer`，用于 UI 和模型输出。
7. 不再保留 `ScreenBuffer` / `VirtualTerminalBuffer`。当前实现没有第三个 buffer。

## 文档地图

| 文档 | 内容 |
| --- | --- |
| `00-Overview.md` | 当前架构总览、边界和术语 |
| `01-Shell-Integration-And-Osc633.md` | OSC 633 协议、shell 脚本、历史记录、括号粘贴 |
| `02-Session-Parser-And-IO.md` | `TerminalSession`、reader、parser、终端响应、VT 捕获 |
| `03-Execution-Strategies-And-Runs.md` | `ExecuteStrategy`、Rich/None 管线、`TerminalRun` 生命周期 |
| `04-LineBuffer-And-UI.md` | `TerminalLineBuffer`、行级映射、`TerminalCodeBlockBridge` |
| `05-Testing-And-Troubleshooting.md` | 测试矩阵、已知回归、排障步骤 |

## 当前组件

### TerminalSession

文件：`src/Everywhere.Core/Terminal/TerminalSession.cs`

`TerminalSession` 绑定一个 `IPtyConnection`，并持有：

| 成员 | 职责 |
| --- | --- |
| `Pty` | PTY 连接 |
| `Dimensions` | 当前终端尺寸 |
| `TextDecoder` | 字节到 UTF-16 文本的增量解码 |
| `Parser` | VT/CSI/OSC parser |
| `ResponseWriter` | 终端查询响应队列 |
| `ReadBuffer` | 单次 read 使用的 byte buffer |

`ReadOrIdleAsync` 是策略层读取 PTY 的统一入口。它有一个 `_pendingReadTask`，保证 idle timeout 不会取消底层 read，也不会启动多个并发 reader。

### TerminalParser

文件：`src/Everywhere.Core/Terminal/TerminalParser.cs`

`TerminalParser` 是一个最小 VT parser。它做四类事情：

1. 解析 printable text、CR、LF、BS、TAB、CSI、OSC 等序列。
2. 追踪终端模式，例如 focus event、bracketed paste、Win32 input mode。
3. 回应必要的终端查询，例如 DA、DSR、窗口尺寸查询。
4. 在 `BeginCapture` 和 `EndCapture` 之间，把屏幕影响映射到当前 `TerminalLineBuffer`。

它不再写入 session-scoped screen buffer。捕获对象由策略根据时序切换：

```text
Rich fallback startup capture -> fallbackBuffer
Rich C marker                 -> activeRun.Output
Rich D marker                 -> EndCapture
None strategy                 -> syntheticRun.Output
```

### ExecuteStrategy

文件：`src/Everywhere.Core/Terminal/ExecuteStrategy.cs`

策略层只有一个对调用方暴露的执行入口：

```csharp
IAsyncEnumerable<TerminalRun> ExecuteAsync(
    TerminalSession session,
    string script,
    ShellType shellType,
    TimeSpan timeout,
    CancellationToken cancellationToken)
```

调用方不需要先拿到额外的 `TerminalExecution` 或 `TerminalSubmissionResult`。枚举产生 `TerminalRun`，`TerminalRun.WaitAsync` 表示这个 run 已经完成。

### TerminalRun

文件：`src/Everywhere.Core/Terminal/TerminalRun.cs`

`TerminalRun` 是一次命令运行的可等待对象：

| 属性/方法 | 含义 |
| --- | --- |
| `CommandLine` | shell integration 报告或 fallback 使用的命令文本 |
| `Output` | 该 run 的 `TerminalLineBuffer` |
| `OutputText` | 从 `Output.GetText()` 得到的模型文本 |
| `ExitCode` | Rich 模式下来自 `OSC 633;D;<code>`；None 模式通常为 null |
| `WaitAsync` | 等待 run 完成、取消或失败 |

`TerminalRun` 不是 UI 对象，也不继承 `ObservableObject`。UI 通过 `TerminalLineBuffer` 和 bridge 订阅输出变化。

### TerminalLineBuffer

文件：`src/Everywhere.Core/Terminal/TerminalLineBuffer.cs`

`TerminalLineBuffer` 是 run-scoped bounded line buffer。它维护稳定的 `TerminalLine.Id` 和 `Revision`，支持 CR 覆盖、LF 换行、光标移动、擦除、插入、删除等行级终端操作。

它同时服务两类消费者：

1. 模型输出：`GetText()`，会裁掉尾部空行。
2. UI 显示：`CopyLines(maxVisibleLines, out version)`，会返回稳定行快照并裁掉尾部 live empty line。

## 关键不变量

### 只有一个 PTY reader

任何时间都只能通过 `TerminalSession.ReadOrIdleAsync` 或 `WaitForIdleAsync` 消费 PTY 输出。Detect、Rich、None 都共享同一个 session，不允许各自创建 reader 或 parser。

原因：

```text
Detect 如果先消费启动输出，Execute 必须继续从同一 parser 状态读取。
否则 bracketed paste、OSC 633 marker、终端查询响应都会丢失上下文。
```

### Detect 必须等待 CommandReady

不能因为看到任意 `OSC 633` 就认为 Rich 可用。zsh 启动阶段可能先发空 `D`，PowerShell 也可能先有启动 marker。Rich 的输入安全点是 `OSC 633;B`：

```text
A prompt started
B command ready
```

`B` 表示 prompt 已经渲染完成，line editor 已经进入接收输入的状态，bracketed paste 等模式也更可靠。

### Rich 的完成信号是 D，不是 idle

收到 `OSC 633;C` 之后，命令可能长时间没有输出，例如：

```sh
python3 -c "import time; time.sleep(10)"
```

这种静默是正常执行中，不是完成。Rich 模式下 active run 存在时，idle 只能用于日志诊断，不能完成 run。完成应来自：

1. `OSC 633;D;<exitCode>`
2. 超时
3. PTY 结束

`A/B` 在 `D` 之后用于确认 prompt 已返回和 session 已稳定。

### None 模式是 fallback，不提供 exit code

没有 shell integration 时，shell 不会可靠报告命令边界和退出码。None 模式返回一个 synthetic `TerminalRun`，`ExitCode` 保持 `null`。

### UI 和模型共用 TerminalLineBuffer，但取法不同

模型文本使用：

```csharp
run.OutputText
```

UI 使用：

```csharp
run.Output.CopyLines(maxVisibleLines, out version)
```

两者都裁掉尾部空行，避免命令输出后多出空白行。

## 已删除的旧设计

### ScreenBuffer / VirtualTerminalBuffer

曾经考虑过 session-scoped screen buffer 与 run-scoped line buffer 并存。最终删除了该设计。

原因：

1. 当前产品需要的是命令 block 的输出，不是完整终端屏幕复刻。
2. 双 buffer 会让 `TerminalParser` 对同一字节流做两次机械映射。
3. 光标和模式状态可以由 parser 自己维护。
4. run output 的滚动、裁剪、UI 绑定都已经由 `TerminalLineBuffer` 覆盖。

因此当前只有两类状态：

```text
TerminalParser  -> cursor/mode/parser state
TerminalLineBuffer -> run output lines
```

### TerminalExecution / TerminalSubmissionResult / ExecuteResult

旧设计中曾经考虑过多层执行对象。最终收敛为：

```csharp
await foreach (var run in strategy.ExecuteAsync(...))
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

没有额外 `ValueTask<TerminalExecution>`，也没有提交结果包装层。

## 当前验证基线

最后一次终端相关验证：

```text
dotnet test tests/Everywhere.Core.Tests/Everywhere.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~Everywhere.Core.Tests.Terminal"
通过: 145
跳过: 4
失败: 0
总计: 149
```

TestApp 构建：

```text
dotnet build tests/Everywhere.Terminal.TestApp/Everywhere.Terminal.TestApp.csproj --no-restore
0 warnings
0 errors
```

