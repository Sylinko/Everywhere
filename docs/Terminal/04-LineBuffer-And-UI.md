# TerminalLineBuffer and UI Bridge

状态日期：2026-05-27

## 目标

UI 需要把终端输出显示成 Chat block，同时模型需要拿到最终文本。当前统一使用 run-scoped `TerminalLineBuffer`。

```text
TerminalParser capture
  -> TerminalLineBuffer
     -> GetText() for model
     -> CopyLines() for UI
        -> TerminalCodeBlockBridge
           -> CodeBlock.Inlines
```

不再有 `ScreenBuffer` 或 `VirtualTerminalBuffer`。

## TerminalLine

文件：`src/Everywhere.Core/Terminal/TerminalLineBuffer.cs`

一行输出是：

```csharp
public readonly record struct TerminalLine(long Id, string Text, long Revision);
```

| 字段 | 用途 |
| --- | --- |
| `Id` | 行身份，插入后保持稳定 |
| `Text` | 行文本 |
| `Revision` | 行内容版本，内容变化时递增 |

UI bridge 使用 `Id` 对齐行，使用 `Revision` 判断是否需要替换 inline。

## Buffer 边界

`TerminalLineBuffer` 是 bounded buffer：

```csharp
public const int DefaultMaxLines = 2000;
```

超过上限时从头部裁剪旧行。这样长输出不会无限增长。

## 写入语义

### 普通文本

连续 printable text 会被合并写入当前行，避免每个 char 都触发一次 replace。

### CR

```text
\r
```

只把当前列重置为 0，不换行。用于进度条覆盖：

```text
Processing 10%\rProcessing 20%
```

最终行应是：

```text
Processing 20%
```

### LF

```text
\n
```

创建下一行，并把 cursor column 重置为 0。尾部 live empty line 会保留在 buffer 内部，以便后续输出继续写入；但 `GetText` 和 `CopyLines` 会裁掉尾部空行。

### Cursor movement

支持：

| 方法 | 含义 |
| --- | --- |
| `CursorUp` | 上移 |
| `CursorDown` | 下移 |
| `CursorForward` | 右移 |
| `CursorBackward` | 左移 |
| `CursorPosition` | 1-based 行列定位 |
| `CursorHorizontalAbsolute` | 1-based 列定位 |

这些方法用于 parser 映射 CSI。

### Erase / Insert / Delete

支持：

| 方法 | 含义 |
| --- | --- |
| `EraseLine(mode)` | 清当前行部分或整行 |
| `EraseDisplay(mode)` | 清屏或清部分显示 |
| `DeleteChars(count)` | 删除字符 |
| `EraseChars(count)` | 用空格擦除字符 |
| `InsertChars(count)` | 插入空格 |

## 空格与空行处理

### trailing spaces

终端清行、光标跳转和固定宽度输出可能留下大量右侧空格。`AddLine` 和 `ReplaceLine` 会调用：

```csharp
NormalizeLineText(text) => text.TrimEnd(' ')
```

这避免 UI 和模型看到一整行无意义空格。

### trailing empty live line

命令输出经常以 LF 结束。内部 buffer 会有一个空的当前行：

```text
line 0: "dearva"
line 1: ""
```

`GetText()` 返回：

```text
dearva
```

`CopyLines()` 也返回一行，避免 UI 多显示一个空行。

注意：这只是快照裁剪，不会删除内部 live line。保留内部 live line 是为了后续 VT 操作仍有正确 cursor 位置。

## Change notification

`TerminalLineBuffer` 暴露 internal event：

```csharp
internal event EventHandler? Changed;
```

它不传复杂 diff，只表示：

```text
buffer version changed
```

### BeginUpdate

`BeginUpdate` 使用 `Monitor.Enter` 持有锁直到 scope dispose：

```csharp
using (buffer.BeginUpdate())
{
    ...
}
```

在嵌套 update 中，只在最外层结束时触发一次 `Changed`。

### Version

每次有实际变化时 `_version++`。UI bridge 通过 `CopyLines(maxVisibleLines, out version)` 获取 snapshot 和版本。

## TerminalCodeBlockBridge

文件：`src/Everywhere.Core/Views/Chat/TerminalCodeBlockBridge.cs`

`TerminalCodeBlockBridge` 是 UI 专用 bridge。它不追求通用复用，直接面向 `LiveMarkdown.Avalonia.CodeBlock`。

### 生命周期

构造即 start：

```csharp
new TerminalCodeBlockBridge(run, codeBlock, maxVisibleLines)
```

构造时：

1. 保存 `run`、`run.Output`、`codeBlock`。
2. 订阅 `_buffer.Changed`。
3. 注册 `_run.Completion` continuation。
4. 安排一次初始 UI refresh。

Dispose 时：

1. 标记 disposed。
2. 增加 generation，取消已排队旧 refresh。
3. 取消订阅 `_buffer.Changed`。

### UI 派发

`Changed` 可能来自 PTY reader 线程。bridge 不直接操作 UI，而是：

```csharp
Dispatcher.UIThread.Post(() => Flush(generation), DispatcherPriority.Background);
```

`Flush` 在 UI 线程：

1. 调用 `Synchronize(out appliedVersion)`。
2. 如果 inline 变化，调用 `codeBlock.HighlightSyntax()`。
3. 如果期间又有 buffer 变化，重新 schedule。
4. 如果 run 已完成并且最终版本已应用，取消订阅并停止。

### LineSlot

bridge 内部维护：

```csharp
private readonly record struct LineSlot(long Id, long Revision);
```

`_slots[i]` 对应 `CodeBlock.Inlines` 中第 `i` 行的 `Run`。

inline 结构：

```text
Run(line0)
LineBreak
Run(line1)
LineBreak
Run(line2)
...
```

最后一行后没有额外 `LineBreak`。

### 增量同步

同步流程：

1. `CopyLines(maxVisibleLines, out version)` 获取可见行。
2. 如果当前没有 slot，rebuild。
3. 查找新旧行 `Id` 的最佳 overlap。
4. 删除不再可见的旧行。
5. 插入新增行。
6. 对 `Id` 相同但 `Revision` 变化的行，仅替换对应 `Run`。

这样滚动窗口前移时，不需要每次重建整个 `InlineCollection`。

## 为什么没有 BatchUpdateStarted/Completed

旧想法是让 buffer 发出 batch started/completed 事件，然后 UI 监听。最终删除了这类事件。

原因：

1. buffer 不知道 UI thread。
2. 如果 buffer event 负责 `Post`，执行到 UI 时 `_lines` 可能已经变了。
3. 如果 buffer event 负责 `Invoke` 阻塞 UI，会拖慢 PTY reader。
4. UI bridge 自己按 version 拉 snapshot 更简单。

因此 buffer 的职责收敛为：

```text
data structure + Changed + snapshot
```

UI bridge 的职责是：

```text
thread dispatch + snapshot diff + InlineCollection update
```

## 模型输出与 UI 输出的区别

### 模型输出

模型只需要最终文本：

```csharp
run.OutputText
```

这会：

1. 裁掉尾部空行。
2. 用 `\n` 拼接行。
3. 保留中间空行。

### UI 输出

UI 需要行对象：

```csharp
run.Output.CopyLines(maxVisibleLines, out version)
```

这会：

1. 裁掉尾部空 live line。
2. 应用可见行上限。
3. 保留 `Id` 和 `Revision`，用于增量更新。

## 已知边界

`TerminalLineBuffer` 是行级 terminal output buffer，不是完整 xterm screen emulator。

当前不做：

1. 富文本样式保留。
2. SGR 颜色映射。
3. alternate screen。
4. 复杂 scroll region 的完整屏幕语义。
5. TUI 全屏交互精确复刻。

当前要做的是：

1. 命令输出文字正确。
2. CR progress 正确覆盖。
3. 清行/清屏不污染输出。
4. UI 行级增量稳定。
5. 模型文本不带无意义空白。

