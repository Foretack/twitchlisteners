using StackExchange.Redis;

namespace _26listeners;
/* cache -

 * twitch:channels

 */

/* pub ->

 * shard:status:all
 * shard:status:{id}
 * shard:updates
 * twitch:channels:updates
 * twitch:messages 

 */

/* sub <-

 * shard:manage
 * twitch:channels:manage

 */
internal sealed class RedisConn
{
    public IDatabaseAsync Db { get; private set; }
    public ISubscriber Sub { get; private set; }

    private readonly Dictionary<RedisChannel, ChannelMessageQueue> _pubsubChannels = new();

    /// <param name="channel">Target PubSub channel</param>
    /// <returns>Subscribed PubSub channel; creates a subscription if one doesn't already exist</returns>
    public ChannelMessageQueue this[string channel] => HandleIndexer(channel);

    public RedisConn(string host)
    {
        var conn = ConnectionMultiplexer.Connect(host);
        Log.Information("REDIS CONNECTED");
        Db = conn.GetDatabase();
        Sub = conn.GetSubscriber();
    }

    private ChannelMessageQueue HandleIndexer(string channel)
    {
        if (!_pubsubChannels.ContainsKey(channel))
        {
            Log.Debug($"NEW SUB CHANNEL: {channel}");
            ChannelMessageQueue queue = Sub.Subscribe(channel);
            _pubsubChannels.Add(channel, queue);
            return queue;
        }

        return _pubsubChannels[channel];
    }
}
