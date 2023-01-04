using CachingFramework.Redis;
using CachingFramework.Redis.Contracts.Providers;
using CachingFramework.Redis.Serializers;
using StackExchange.Redis;

namespace _26listeners;
internal sealed class RedisConn
{
    public ICacheProviderAsync Cache { get; init; }
    public IPubSubProviderAsync PubSub { get; init; }

    private readonly Dictionary<RedisChannel, ChannelMessageQueue> _pubsubChannels = new();

    public RedisConn(string host)
    {
        var context = new RedisContext(ConnectionMultiplexer.Connect(host), new JsonSerializer());
        Log.Information("REDIS CONNECTED");
        Cache = context.Cache;
        PubSub = context.PubSub;
    }
}
