# Session, Parser and IO

状态日期：2026-05-27

## 目标

本层负责 PTY 会话的唯一 IO 管线：

```text
IPtyConnection.ReaderStream
  -> TerminalSession.ReadOrIdleAsync
  -> PtyTextDecoder
  -> TerminalParser
  -> TerminalLineBuffer capture
  -> TerminalResponseWriter.FlushAsync
```

原则是：所有读取都必须通过同一个 `TerminalSession`，不能让 Detect、Rich、None、UI 或测试 app 各自消费 PTY 字节流。

## TerminalSession 的职责

文件：`src/Everywhere.Core/Terminal/TerminalSession.cs`

`TerminalSession` 是一次 PTY shell 生命周期内的共享对象。它不是 command result，也不是 UI model。

### 持有对象

| 对象 | 生命周期 | 说明 |
| --- | --- | --- |
| `IPtyConnection Pty` | session | shell PTY |
| `TerminalDimensions Dimensions` | session | 终端尺寸 |
| `PtyTextDecoder TextDecoder` | session | 增量 UTF-8 解码 |
| `TerminalParser Parser` | session | parser、模式、marker、capture |
| `TerminalResponseWriter ResponseWriter` | session | 终端响应写回 |
| `byte[] ReadBuffer` | session | read buffer |

### ReadOrIdleAsync

`ReadOrIdleAsync` 返回：

| 返回值 | 含义 |
| --- | --- |
| `Data` | 本次读到了数据，已经 feed parser |
| `Idle` | 在 idlePeriod 内没有新数据 |
| `EndOfStream` | PTY reader 返回 0 |

关键点是 `_pendingReadTask`：

```csharp
var readTask = _pendingReadTask ??= Pty.ReaderStream.ReadAsync(ReadBuffer, CancellationToken.None).AsTask();
```

底层 read 不绑定调用方 cancellation token。这样 idle timeout 只是判断“这段时间没有新数据”，不会取消已经挂起的 PTY read。

如果没有 `_pendingReadTask`，每个 idle 轮询都可能启动新 read，最终产生并发读取、乱序数据或死锁。

### WaitForIdleAsync

`WaitForIdleAsync(maxWait, quietPeriod)` 用于 None 模式启动阶段和 Ctrl+C 后的沉淀：

1. 持续读数据。
2. 只要读到数据就继续等 quiet period。
3. quiet period 内没有数据则返回。
4. 超过 max wait 也返回。

它不表示命令完成，只表示 PTY 当前安静。

### WriteInputAsync

普通输入写入：

```csharp
await Pty.WriterStream.WriteAsync(Encoding.UTF8.GetBytes(input), cancellationToken);
await Pty.WriterStream.FlushAsync(cancellationToken);
```

调用方应明确输入语义：

| 输入 | 语义 |
| --- | --- |
| `"\r"` | Enter |
| `"\x03"` | Ctrl+C |
| text | 直接注入字符 |
| VT key sequence | 方向键、Delete 等 |

### WritePasteAsync

如果 parser 当前追踪到 bracketed paste enabled：

```text
ESC[200~
payload with LF
ESC[201~
```

否则 fallback：

```text
text with LF converted to CR
```

`WritePasteAsync` 只负责“粘贴文本”。提交命令的最终 Enter 由策略层单独发送。

## PtyTextDecoder

文件：`src/Everywhere.Core/Terminal/PtyTextDecoder.cs`

PTY 输出是 byte stream，不能假设每次 read 都以完整 UTF-8 字符结束。`PtyTextDecoder` 负责增量解码，避免多字节字符跨 read 时产生乱码或丢失。

测试覆盖点包括：

1. 普通 UTF-8。
2. 多字节字符跨 chunk。
3. 非 UTF-8 字节不导致崩溃。

## TerminalParser

文件：`src/Everywhere.Core/Terminal/TerminalParser.cs`

`TerminalParser` 是最小 VT parser，不追求完整 xterm 兼容。它只实现 Everywhere 当前需要的行为：

1. 文本捕获。
2. 常见 cursor movement。
3. line/display erase。
4. terminal query response。
5. terminal mode tracking。
6. OSC 633 shell integration marker。

### 状态机

核心状态：

| 状态 | 含义 |
| --- | --- |
| `Ground` | 普通文本 |
| `Escape` | 读到 ESC |
| `CsiEntry` | CSI 起始 |
| `CsiParam` | CSI 参数 |
| `CsiIntermediate` | CSI intermediate |
| `OscString` | OSC payload |
| `OscStringEscape` | OSC 内遇到 ESC，等待 ST |
| `CharsetSelect` | 字符集选择，当前只消费 |

### 终端模式

当前公开追踪：

| 属性 | 来源 |
| --- | --- |
| `HasDetectedShellIntegration` | 任意 OSC 633 marker |
| `IsFocusEventTrackingEnabled` | `CSI ? 1004 h/l` |
| `IsBracketedPasteModeEnabled` | `CSI ? 2004 h/l` |
| `IsWin32InputModeEnabled` | `CSI ? 9001 h/l` |

策略层最重要的是 `IsBracketedPasteModeEnabled`。

### 终端响应

parser 通过 `TerminalResponseRequested` 发出要回写的字符串，`TerminalSession` 的 `ResponseWriter` 会排队并在每次 read 后 flush。

响应必须在同一个 session 中发生。Detect 期间看到的查询和模式会影响后续执行。

### Cursor state

parser 内部维护：

```text
CursorX
CursorY
saved cursor
scroll region
dimensions
```

这些状态用于：

1. cursor position report。
2. 将 CSI 操作映射到 active capture buffer。
3. 处理 CR/LF/BS/TAB/erase/delete/insert。

不再需要单独的 `ScreenBuffer`。

## Capture 机制

parser 的 capture API：

```csharp
BeginCapture(TerminalLineBuffer output)
EndCapture()
```

捕获开始后，parser 会把后续 text/control 操作映射到指定的 `TerminalLineBuffer`。

### 为什么是 parser capture，而不是策略手写文本拼接

PTY 输出可能包含：

```text
progress 10%\rprogress 20%
CSI K
CSI 2J
cursor up
cursor horizontal absolute
```

如果策略层只追加字符串，进度条、清行、覆盖、光标移动都会错误。parser capture 至少能以行级粒度还原最终输出。

### Rich capture 切换

Rich 策略启动后先捕获 fallback：

```text
BeginCapture(fallbackBuffer)
send command
```

如果看到 `C`：

```text
EndCapture(fallbackBuffer)
CreateRun()
BeginCapture(activeRun.Output)
```

如果看到 `D`：

```text
EndCapture(activeRun.Output)
Complete(exitCode)
```

如果一直没有 `C`，说明 shell integration 没有进入正常 command execution 时序，Rich 使用 `fallbackBuffer` 合成一个 run。

### None capture

None 策略直接创建一个 synthetic run：

```text
BeginCapture(run.Output)
send command
wait output start
wait prompt/idle/timeout
EndCapture()
```

None 没有可靠命令边界，所以它只返回单个 run。

## 线程与事件

`TerminalLineBuffer.Changed` 由 parser feed 的线程触发。UI 不能直接在这个事件里操作 Avalonia 控件。`TerminalCodeBlockBridge` 订阅该事件后，通过 `Dispatcher.UIThread.Post` 进行 UI 刷新。

原则：

1. parser feed 不阻塞 UI。
2. UI 刷新不阻塞 PTY reader。
3. buffer event 只表示“有变化”，不携带复杂 diff。
4. UI bridge 自己复制 snapshot 并增量同步 inline。

## 不再存在的 ScreenBuffer

旧文档中的 session-scoped `ScreenBuffer` 已删除。保留它会带来两个问题：

1. parser 对同一输出做两次相似映射。
2. UI 和模型需要的是 run output，不是完整 screen state。

当前 parser 内部状态足以支撑 cursor、mode、response；`TerminalLineBuffer` 足以支撑 run display 和 model text。

## Resize

`TerminalSession.ResizeAsync` 会：

1. 调整 PTY 大小。
2. 更新 `Dimensions`。
3. 调用 `Parser.Resize(dimensions)`。
4. flush terminal responses。

resize 后 cursor position report 和窗口尺寸 query 都会使用新尺寸。

