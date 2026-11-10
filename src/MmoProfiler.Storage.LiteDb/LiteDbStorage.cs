using LiteDB;

namespace MmoProfiler.Storage.LiteDb;

/// <summary>
/// Owns the single <see cref="LiteDatabase"/> shared by the profile store and the message log.
/// Dispose closes the database.
/// </summary>
public sealed class LiteDbStorage : IDisposable
{
    private readonly LiteDatabase _db;

    private LiteDbStorage(LiteDatabase db)
    {
        _db = db;
        Profiles = new LiteDbProfileStore(db);
        Messages = new LiteDbMessageLog(db);
    }

    public LiteDbProfileStore Profiles { get; }
    public LiteDbMessageLog Messages { get; }

    /// <summary>Opens (or creates) the database file in direct mode.</summary>
    public static LiteDbStorage Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var connection = new ConnectionString { Filename = path, Connection = ConnectionType.Direct };
        return new LiteDbStorage(new LiteDatabase(connection));
    }

    /// <summary>A throw-away database held in memory, for tests.</summary>
    public static LiteDbStorage OpenInMemory() => new(new LiteDatabase(new MemoryStream()));

    public void Dispose() => _db.Dispose();

    internal static long ToStored(DateTimeOffset value) => value.UtcDateTime.Ticks;

    internal static DateTimeOffset FromStored(long ticks) =>
        new(new DateTime(ticks, DateTimeKind.Utc), TimeSpan.Zero);

    internal static T ParseEnum<T>(string name, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(name, false, out var v) && Enum.GetName(v) == name ? v : fallback;
}
