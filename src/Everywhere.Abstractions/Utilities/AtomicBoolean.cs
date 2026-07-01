using System.Diagnostics;

namespace Everywhere.Utilities;

/// <summary>
/// A thread-safe boolean wrapper that allows atomic operations on a boolean value.
/// </summary>
public readonly ref struct AtomicBoolean
{
    private readonly ref int _value;

    /// <summary>
    /// Initializes a new instance of the AtomicBoolean struct with a reference to an integer value as the underlying storage.
    /// The integer should be 0 for false and 1 for true.
    /// </summary>
    /// <param name="value"></param>
    public AtomicBoolean(ref int value)
    {
        Debug.Assert(value is 0 or 1);
        _value = ref value;
    }

    /// <summary>
    /// Gets or sets the boolean value atomically. Internally, it uses Interlocked operations to ensure thread safety.
    /// </summary>
    public bool Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Interlocked.CompareExchange(ref _value, 0, 0) != 0;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Interlocked.Exchange(ref _value, value ? 1 : 0);
    }

    /// <summary>
    /// Atomically sets the value to newValue if the current value equals expected.
    /// </summary>
    /// <param name="expected"></param>
    /// <param name="newValue"></param>
    /// <returns>true if the value was set to newValue; false otherwise.</returns>
    public bool CompareSet(bool expected, bool newValue)
    {
        var e = expected ? 1 : 0;
        var n = newValue ? 1 : 0;
        return Interlocked.CompareExchange(ref _value, n, e) == e;
    }

    /// <summary>
    /// Atomically flips the value if the current value is false.
    /// </summary>
    /// <returns>The original value before the flip.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool FlipIfFalse() => Interlocked.CompareExchange(ref _value, 1, 0) == 0;

    /// <summary>
    /// Atomically flips the value if the current value is true.
    /// </summary>
    /// <returns>The original value before the flip.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool FlipIfTrue() => Interlocked.CompareExchange(ref _value, 0, 1) == 1;

    /// <summary>
    /// Implicit conversion to bool for easy usage in conditions.
    /// </summary>
    /// <param name="atomicBoolean"></param>
    /// <returns></returns>
    public static implicit operator bool(AtomicBoolean atomicBoolean) => atomicBoolean.Value;
}