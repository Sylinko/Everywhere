namespace Everywhere.AI;

/// <summary>
/// QoS-like proportional fair-share token budget allocator.
/// Distributes a fixed token budget across multiple items based on their content length,
/// guaranteeing each item a minimum allocation and capping at a maximum to prevent monopoly.
/// <para>
/// Algorithm (analogous to Weighted Fair Queuing in routing):
/// <list type="number">
///   <item><b>Minimum guarantee</b> — every item gets <c>min(desired, minPerItem)</c>.</item>
///   <item><b>Proportional surplus</b> — remaining budget is distributed in proportion to each item's
///       remaining need (capped at <c>maxPerItem</c>).</item>
///   <item><b>Remainder rounding</b> — any leftover 1-token units are assigned to items with the
///       largest unmet need first.</item>
/// </list>
/// Short items that fit within their proportional share are kept intact; their unused quota
/// automatically flows back into the pool for longer items.
/// </para>
/// </summary>
public static class TokenBudget
{
    /// <summary>
    /// Allocates a token budget across items.
    /// </summary>
    /// <param name="desiredTokens">Ideal token count per item (e.g. content length in tokens).</param>
    /// <param name="totalBudget">Maximum total tokens to allocate.</param>
    /// <param name="minTokensPerItem">
    /// Minimum guaranteed tokens per item. Items shorter than this are kept intact and their
    /// surplus flows back to the pool. Use 0 to disable the minimum guarantee.
    /// </param>
    /// <param name="maxTokensPerItem">
    /// Maximum tokens any single item may receive. Prevents a single huge item from starving
    /// all others. When negative (default), automatically set to <c>totalBudget / 2</c>.
    /// </param>
    /// <returns>
    /// Array of allocated token counts, same length as <paramref name="desiredTokens"/>.
    /// Invariants: <c>allocated[i] ≤ desiredTokens[i]</c>, <c>∑ allocated ≤ totalBudget</c>.
    /// </returns>
    public static int[] Allocate(
        ReadOnlySpan<int> desiredTokens,
        int totalBudget,
        int minTokensPerItem = 200,
        int maxTokensPerItem = -1)
    {
        var n = desiredTokens.Length;
        if (n == 0) return [];
        if (totalBudget <= 0) return new int[n];

        var effectiveMax = maxTokensPerItem > 0 ? maxTokensPerItem : totalBudget / 2;
        var allocated = new int[n];
        var remaining = totalBudget;

        // ── Phase 1: Minimum guarantee ──
        for (var i = 0; i < n; i++)
        {
            allocated[i] = Math.Min(desiredTokens[i], minTokensPerItem);
            remaining -= allocated[i];
        }

        if (remaining <= 0) return allocated;

        // ── Phase 2: Calculate remaining need (capped) ──
        var needs = new int[n];
        long totalNeed = 0;
        for (var i = 0; i < n; i++)
        {
            var cap = Math.Min(desiredTokens[i], effectiveMax);
            needs[i] = Math.Max(0, cap - allocated[i]);
            totalNeed += needs[i];
        }

        if (totalNeed == 0) return allocated;

        // ── Phase 3: If all needs can be satisfied, give exact amounts ──
        if (totalNeed <= remaining)
        {
            for (var i = 0; i < n; i++)
                allocated[i] += needs[i];
            return allocated;
        }

        // ── Phase 4: Proportional distribution ──
        long distributed = 0;
        // Track fractional remainders for rounding
        var remainders = new (int index, double frac)[n];
        int remaindersCount = 0;

        for (var i = 0; i < n; i++)
        {
            if (needs[i] == 0) continue;

            // floor(need[i] / totalNeed * remaining)
            var share = (long)needs[i] * remaining / totalNeed;
            allocated[i] += (int)share;
            distributed += share;

            // Keep fractional part for remainder phase
            var exact = (double)needs[i] * remaining / totalNeed;
            var frac = exact - share;
            if (frac > 0)
                remainders[remaindersCount++] = (i, frac);
        }

        // ── Phase 5: Distribute leftover 1-token units ──
        var leftover = (int)(remaining - distributed);
        if (leftover > 0 && remaindersCount > 0)
        {
            // Sort by fractional remainder descending
            Array.Sort(remainders, 0, remaindersCount,
                Comparer<(int index, double frac)>.Create((a, b) => b.frac.CompareTo(a.frac)));

            for (var r = 0; r < Math.Min(leftover, remaindersCount); r++)
            {
                var idx = remainders[r].index;
                if (allocated[idx] < Math.Min(desiredTokens[idx], effectiveMax))
                    allocated[idx]++;
            }
        }

        return allocated;
    }
}
