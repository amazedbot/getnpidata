using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>The stamp <see cref="SnapshotCache{T}"/> compares to see whether the data changed.</summary>
public static class DataStamp
{
    /// <summary>The <c>reference_data</c> row the loader's directory builder writes when it rebuilt the directory tables.</summary>
    public const string DirectorySource = "directories";

    /// <summary>When the loader last rebuilt the facility and group tables ("" before the first build).</summary>
    public static async Task<string> ReadAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT CAST(loaded_at AS CHAR) FROM reference_data WHERE source = @source", new { source = DirectorySource }, cancellationToken: ct)) ?? "";
    }

    /// <summary>True once the loader has built the directory tables (they may then be empty: a database without those datasets).</summary>
    public static async Task<bool> BuiltAsync(MySqlConnection connection, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM reference_data WHERE source = @source", new { source = DirectorySource }, cancellationToken: ct)) > 0;
}

/// <summary>
/// A directory page asked for a list the loader hasn't built yet (the first load after the tables were added hasn't run). The pages say the list is being prepared; the API answers 503.
/// </summary>
public sealed class DirectoryNotReadyException : Exception
{
    public DirectoryNotReadyException()
        : this("The list")
    {
    }

    public DirectoryNotReadyException(string what)
        : base($"{what} is being prepared; it is built after the next data load.")
    {
    }

    public DirectoryNotReadyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A value read once from the database and kept in memory until the data changes (the Facilities &amp; hospitals and Groups &amp;
/// organizations pages, docs/PRODUCT_PLAN.md step 3.6). Every <see cref="CheckEvery"/> a request looks at the data's stamp (when
/// the loader last rebuilt the directory tables, one cheap query); when it changed, the value is reread in the background while
/// requests keep the old one. Only the first request after the site starts waits for the read (small tables: under a second).
/// </summary>
public sealed class SnapshotCache<T>(Func<CancellationToken, Task<string>> stamp, Func<CancellationToken, Task<T>> load) : IDisposable
    where T : class
{
    /// <summary>How often the stamp is read.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(10);

    /// <summary>Rebuilt after this long even when the stamp says nothing changed.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private sealed record Holder(T Value, string Stamp, DateTime BuiltAt, DateTime CheckedAt);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Lock _gate = new();
    private volatile Holder? _current;
    private Task? _refresh;

    public void Dispose() => _lock.Dispose();

    /// <summary>The value if one was built already, else null (never waits for a build).</summary>
    public T? Peek() => _current?.Value;

    public async Task<T> GetAsync(CancellationToken ct)
    {
        if (_current is { } current)
        {
            var now = DateTime.UtcNow;
            if (now - current.CheckedAt >= CheckEvery)
            {
                StartRefresh(current);
            }

            return current.Value;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_current is { } again)
            {
                return again.Value;
            }

            var s = await stamp(ct);
            var value = await load(ct);
            var at = DateTime.UtcNow;
            _current = new Holder(value, s, at, at);
            return value;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void StartRefresh(Holder current)
    {
        lock (_gate)
        {
            if (_refresh is { IsCompleted: false } || !ReferenceEquals(_current, current))
            {
                return;
            }

            // Marked as checked first, so the requests arriving meanwhile don't start another refresh.
            _current = current with { CheckedAt = DateTime.UtcNow };
            _refresh = Task.Run(async () =>
            {
                try
                {
                    var s = await stamp(CancellationToken.None);
                    if (s != current.Stamp || DateTime.UtcNow - current.BuiltAt >= MaxAge)
                    {
                        var value = await load(CancellationToken.None);
                        var at = DateTime.UtcNow;
                        _current = new Holder(value, s, at, at);
                    }
                }
                catch (Exception)
                {
                    // The database was unreachable or the build failed: keep the old value and try again at the next check.
                }
            });
        }
    }
}
