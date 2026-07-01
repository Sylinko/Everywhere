using Porta.Pty;

namespace Everywhere.Terminal;

public readonly record struct TerminalDimensions
{
    public static TerminalDimensions Default { get; } = new(120, 30);

    public int Columns { get; }

    public int Rows { get; }

    public TerminalDimensions(int columns, int rows)
    {
        Columns = Math.Max(columns, 1);
        Rows = Math.Max(rows, 1);
    }

    public static TerminalDimensions FromPtyOptions(PtyOptions options) => new(
        options.Cols > 0 ? options.Cols : Default.Columns,
        options.Rows > 0 ? options.Rows : Default.Rows);
}
