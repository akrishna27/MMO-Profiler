using LiteDB;
using MmoProfiler.Core;

namespace MmoProfiler.Storage.LiteDb;

public sealed class LiteDbMessageLog : IMessageLog
{
    private const string CollectionName = "messages";

    private readonly ILiteCollection<BsonDocument> _col;

    internal LiteDbMessageLog(LiteDatabase db)
    {
        _col = db.GetCollection(CollectionName, BsonAutoId.Int64);
        _col.EnsureIndex("senderId");
        _col.EnsureIndex("timestampUtc");
    }

    public void Append(IEnumerable<LoggedMessage> messages)
    {
        var docs = messages.Select(ToDocument).ToList();
        if (docs.Count > 0) _col.InsertBulk(docs);
    }

    public IReadOnlyList<LoggedMessage> GetRecent(CharacterKey key, int take)
    {
        if (take <= 0) return Array.Empty<LoggedMessage>();
        return _col.Find(Query.EQ("senderId", key.Id))
            .OrderByDescending(d => d["timestampUtc"].AsInt64)
            .ThenByDescending(d => d["_id"].AsInt64)
            .Take(take)
            .Select(FromDocument).ToList();
    }

    public int DeleteFor(CharacterKey key) => _col.DeleteMany(Query.EQ("senderId", key.Id));

    public int DeleteOlderThan(DateTimeOffset cutoffUtc) =>
        _col.DeleteMany(Query.LT("timestampUtc", LiteDbStorage.ToStored(cutoffUtc)));

    public void DeleteAll() => _col.DeleteAll();

    private static BsonDocument ToDocument(LoggedMessage m)
    {
        var msg = m.Message;
        var a = m.Analysis;
        return new BsonDocument
        {
            ["senderId"] = msg.Sender.Id,
            ["gameId"] = msg.Sender.GameId,
            ["name"] = msg.Sender.Name,
            ["realm"] = msg.Sender.Realm,
            ["channel"] = msg.Channel.ToString(),
            ["channelLabel"] = msg.ChannelLabel is null ? BsonValue.Null : new BsonValue(msg.ChannelLabel),
            ["text"] = msg.Text,
            ["timestampUtc"] = LiteDbStorage.ToStored(msg.TimestampUtc),
            ["intents"] = new BsonArray(a.Intents.Select(t => new BsonValue(t))),
            ["tones"] = new BsonArray(a.Tones.Select(t => new BsonValue(t))),
            ["flags"] = new BsonArray(a.Flags.Select(t => new BsonValue(t))),
            ["language"] = a.Language,
            ["topics"] = new BsonArray(a.Topics.Select(t => new BsonValue(t))),
        };
    }

    private static LoggedMessage FromDocument(BsonDocument d)
    {
        var sender = new CharacterKey(d["gameId"].AsString, d["name"].AsString, d["realm"].AsString);
        var message = new ChatMessage(
            sender,
            LiteDbStorage.ParseEnum(d["channel"].AsString, ChatChannel.Say),
            d["channelLabel"].IsNull ? null : d["channelLabel"].AsString,
            d["text"].AsString,
            LiteDbStorage.FromStored(d["timestampUtc"].AsInt64));
        var analysis = new MessageAnalysis(
            ReadStrings(d, "intents"),
            ReadStrings(d, "tones"),
            d["language"].AsString,
            d["topics"].AsArray.Select(t => t.AsString).ToList())
        {
            Flags = ReadStrings(d, "flags"),
        };
        return new LoggedMessage(message, analysis);
    }

    private static List<string> ReadStrings(BsonDocument d, string field) =>
        d.TryGetValue(field, out var v) && v.IsArray
            ? v.AsArray.Where(x => x.IsString).Select(x => x.AsString).ToList()
            : new List<string>();
}
