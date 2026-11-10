using LiteDB;
using MmoProfiler.Core;

namespace MmoProfiler.Storage.LiteDb;

public sealed class LiteDbProfileStore : IProfileStore
{
    private const string CollectionName = "profiles";
    public const int CurrentSchemaVersion = 1;

    private readonly ILiteCollection<BsonDocument> _col;

    internal LiteDbProfileStore(LiteDatabase db)
    {
        _col = db.GetCollection(CollectionName);
        _col.EnsureIndex("lastSeenUtc");
        _col.EnsureIndex("nameLower");
    }

    public Profile? Get(CharacterKey key)
    {
        var doc = _col.FindById(key.Id);
        return doc is null ? null : FromDocument(doc);
    }

    public void Upsert(Profile profile) => _col.Upsert(ToDocument(profile));

    public void UpsertMany(IEnumerable<Profile> profiles) =>
        _col.Upsert(profiles.Select(ToDocument).ToList());

    public IReadOnlyList<Profile> List(string? nameContains, int skip, int take)
    {
        if (take <= 0) return Array.Empty<Profile>();
        skip = Math.Max(skip, 0);

        var query = _col.Query().OrderByDescending("lastSeenUtc");
        if (string.IsNullOrEmpty(nameContains))
            return query.Offset(skip).Limit(take).ToList().Select(FromDocument).ToList();

        // Substring match in code rather than a LIKE pattern, so wildcard characters typed by
        // the user are taken literally.
        var needle = nameContains.ToLowerInvariant();
        return query.ToEnumerable()
            .Where(d => d["nameLower"].AsString.Contains(needle, StringComparison.Ordinal))
            .Skip(skip).Take(take)
            .Select(FromDocument).ToList();
    }

    public long Count() => _col.LongCount();

    public bool Delete(CharacterKey key) => _col.Delete(key.Id);

    public void DeleteAll() => _col.DeleteAll();

    private static BsonDocument ToDocument(Profile p)
    {
        var key = p.Key;
        return new BsonDocument
        {
            ["_id"] = p.Id,
            ["schemaVersion"] = p.SchemaVersion,
            ["gameId"] = key.GameId,
            ["name"] = key.Name,
            ["realm"] = key.Realm,
            ["nameLower"] = key.Name.ToLowerInvariant(),
            ["firstSeenUtc"] = LiteDbStorage.ToStored(p.FirstSeenUtc),
            ["lastSeenUtc"] = LiteDbStorage.ToStored(p.LastSeenUtc),
            ["messageCount"] = p.MessageCount,
            ["analyzedCount"] = p.AnalyzedCount,
            ["channelCounts"] = EnumCounts(p.ChannelCounts),
            ["toneCounts"] = EnumCounts(p.ToneCounts),
            ["intentCounts"] = EnumCounts(p.IntentCounts),
            ["languageCounts"] = StringCounts(p.LanguageCounts),
            ["topicCounts"] = StringCounts(p.TopicCounts),
            ["activity"] = new BsonArray(p.ActivityByHourOfWeekUtc.Select(v => new BsonValue(v))),
            ["summary"] = p.Summary is null ? BsonValue.Null : new BsonValue(p.Summary),
            ["summaryUpdatedUtc"] = p.SummaryUpdatedUtc is { } s
                ? new BsonValue(LiteDbStorage.ToStored(s))
                : BsonValue.Null,
            ["messagesSinceSummary"] = p.MessagesSinceSummary,
            ["userNotes"] = p.UserNotes,
        };
    }

    private static Profile FromDocument(BsonDocument doc)
    {
        doc = Migrate(doc);
        var key = new CharacterKey(doc["gameId"].AsString, doc["name"].AsString, doc["realm"].AsString);
        var activity = new int[Profile.HoursPerWeek];
        if (doc.TryGetValue("activity", out var a) && a.IsArray)
        {
            var i = 0;
            foreach (var v in a.AsArray)
            {
                if (i >= activity.Length) break;
                activity[i++] = v.AsInt32;
            }
        }

        return new Profile
        {
            SchemaVersion = CurrentSchemaVersion,
            Id = doc["_id"].AsString,
            Key = key,
            FirstSeenUtc = LiteDbStorage.FromStored(doc["firstSeenUtc"].AsInt64),
            LastSeenUtc = LiteDbStorage.FromStored(doc["lastSeenUtc"].AsInt64),
            MessageCount = doc["messageCount"].AsInt64,
            AnalyzedCount = doc["analyzedCount"].AsInt64,
            ChannelCounts = ReadEnumCounts<ChatChannel>(doc, "channelCounts"),
            ToneCounts = ReadEnumCounts<Tone>(doc, "toneCounts"),
            IntentCounts = ReadEnumCounts<Intent>(doc, "intentCounts"),
            LanguageCounts = ReadStringCounts(doc, "languageCounts"),
            TopicCounts = ReadStringCounts(doc, "topicCounts"),
            ActivityByHourOfWeekUtc = activity,
            Summary = doc["summary"].IsNull ? null : doc["summary"].AsString,
            SummaryUpdatedUtc = doc["summaryUpdatedUtc"].IsNull
                ? null
                : LiteDbStorage.FromStored(doc["summaryUpdatedUtc"].AsInt64),
            MessagesSinceSummary = doc["messagesSinceSummary"].AsInt32,
            UserNotes = doc["userNotes"].AsString,
        };
    }

    /// <summary>
    /// The seam for later schema versions. Version 1 is the only one so far; a document written by a
    /// newer plugin is refused rather than read wrongly.
    /// </summary>
    private static BsonDocument Migrate(BsonDocument doc)
    {
        var version = doc.TryGetValue("schemaVersion", out var v) && v.IsNumber ? v.AsInt32 : 1;
        if (version > CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Profile was written with schema version {version}; this build understands up to {CurrentSchemaVersion}.");
        return doc;
    }

    private static BsonDocument EnumCounts<TEnum>(Dictionary<TEnum, long> counts) where TEnum : struct, Enum
    {
        var doc = new BsonDocument();
        foreach (var (k, v) in counts) doc[k.ToString()] = v;
        return doc;
    }

    private static BsonDocument StringCounts(Dictionary<string, long> counts)
    {
        var doc = new BsonDocument();
        foreach (var (k, v) in counts) doc[k] = v;
        return doc;
    }

    private static Dictionary<TEnum, long> ReadEnumCounts<TEnum>(BsonDocument doc, string field)
        where TEnum : struct, Enum
    {
        var result = new Dictionary<TEnum, long>();
        if (!doc.TryGetValue(field, out var value) || !value.IsDocument) return result;
        foreach (var (name, count) in value.AsDocument)
        {
            // Names only: Enum.TryParse would also accept numeric strings like "5".
            if (Enum.TryParse<TEnum>(name, false, out var parsed)
                && Enum.GetName(parsed) == name
                && count.IsNumber)
                result[parsed] = count.AsInt64;
        }
        return result;
    }

    private static Dictionary<string, long> ReadStringCounts(BsonDocument doc, string field)
    {
        var result = new Dictionary<string, long>();
        if (!doc.TryGetValue(field, out var value) || !value.IsDocument) return result;
        foreach (var (name, count) in value.AsDocument)
            if (count.IsNumber) result[name] = count.AsInt64;
        return result;
    }
}
