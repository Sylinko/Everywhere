namespace Everywhere.Chat;

public static class VisualContextLengthLimitExtensions
{
    public static int ToTokenLimit(this VisualContextLengthLimit limit)
    {
        return limit switch
        {
            VisualContextLengthLimit.Minimal => 1024,
            VisualContextLengthLimit.Balanced => 4096,
            VisualContextLengthLimit.Detailed => 10240,
            VisualContextLengthLimit.Ultimate => 40960,
            VisualContextLengthLimit.Unlimited => int.MaxValue,
            _ => throw new ArgumentOutOfRangeException(nameof(limit), limit, null)
        };
    }
}