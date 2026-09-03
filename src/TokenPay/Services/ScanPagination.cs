namespace TokenPay.Services;

public static class ScanPagination
{
    public static async Task<IReadOnlyList<T>> ReadEvmAsync<T>(
        Func<int, CancellationToken, Task<(bool Success, IReadOnlyList<T> Items, string? Error)>> fetch,
        CancellationToken ct, int offset = 100, int maxPages = 1000)
    {
        var all = new List<T>();
        for (var page = 1; page <= maxPages; page++)
        {
            var result = await fetch(page, ct);
            if (!result.Success) throw new ChainQueryException(result.Error ?? "EVM scan page failed; cursor was not advanced.");
            all.AddRange(result.Items);
            if (result.Items.Count < offset) return all;
        }
        throw new ChainQueryException($"EVM scan exceeded {maxPages} pages; cursor was not advanced.");
    }

    public static async Task<IReadOnlyList<T>> ReadTronAsync<T>(
        Func<string?, CancellationToken, Task<(bool Success, IReadOnlyList<T> Items, string? Fingerprint)>> fetch,
        string? continuation, Func<string, CancellationToken, Task> checkpoint, CancellationToken ct, int maxPages = 1000)
    {
        var all = new List<T>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < maxPages; page++)
        {
            var result = await fetch(continuation, ct);
            if (!result.Success) throw new ChainQueryException("TRON scan page failed; time cursor was not advanced.");
            all.AddRange(result.Items);
            if (string.IsNullOrWhiteSpace(result.Fingerprint)) return all;
            if (!seen.Add(result.Fingerprint)) throw new ChainQueryException("TRON scan returned a repeated fingerprint.");
            continuation = result.Fingerprint;
            await checkpoint(continuation, ct);
        }
        throw new ChainQueryException($"TRON scan exceeded {maxPages} pages; time cursor was not advanced.");
    }
}
