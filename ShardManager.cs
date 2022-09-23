using System.Text.Json;
using _26listeners.Models;
using IntervalTimer = System.Timers.Timer;

namespace _26listeners;
// TODO: listen to controls from PubSub
internal sealed class ShardManager
{
    public static TwitchChannel[] Channels { get; private set; } = Array.Empty<TwitchChannel>();

    private List<Shard> Shards { get; set; } = new();
    private int ChannelsPerShard { get; set; } = 25;
    private int ShardsSpawned { get; set; }
    private int ShardsKilled { get; set; }
    private readonly IntervalTimer _timer = new();

    public ShardManager(string channelsJson)
    {
        Channels = JsonSerializer.Deserialize<TwitchChannel[]>(channelsJson)
            ?? throw new JsonException("Channel deserialization error");

        // Determine main channel by top priority
        TwitchChannel mainChannel = Channels.First(x => Channels.Max(y => y.Priority) == x.Priority);
        Log.Information($"MAIN CHANNEL:{mainChannel}");
        // Chunk the rest of the channels by ChannelsPerShard
        TwitchChannel[][] chunks = Channels
            .Where(x => Channels.Max(y => y.Priority) != x.Priority)    // Exclude main channel
            .Where(x => x.Priority > -10)                               // Exclude channels with -10 or less priority
            .OrderByDescending(x => x.Priority)                         // Prioritize channels with higher priority
            .Chunk(ChannelsPerShard).ToArray();                         // Convert to array to prevent double iteration
        Log.Information($"CHUNKS:{chunks.Length}");

        // Main channel is always the first to be joined
        AddShard(new Shard("MAIN", ShardsSpawned, new[] { mainChannel }));
        foreach (TwitchChannel[] channelBatch in chunks) // Spawn shard for each chunk
        {
            AddShard(new Shard("LISTENER", ShardsSpawned, channelBatch));
        }

        _timer.Interval = TimeSpan.FromHours(1).TotalMilliseconds;
        _timer.AutoReset = true;
        _timer.Enabled = true;
        _timer.Elapsed += (_, _) => CheckShardStates();
    }

    public string Ping()
    {
        return $"{Shards.Count} active, {ShardsSpawned} spawned, {ShardsKilled} killed " +
            $"| uptime avg: {Shards.Average(x => new DateTimeOffset(x.SpawnTime).ToUnixTimeSeconds())}";
    }

    public void SetChannelsPerShard(int count)
    {
        ChannelsPerShard = count;
    }

    public void RespawnShard(Shard shard)
    {
        AddShard(new Shard(shard.Name, ShardsSpawned, shard.Channels));
        RemoveShard(shard);
        shard.Dispose();
    }

    private void AddShard(Shard shard)
    {
        Log.Information($"+SHARD:{shard.Name}#{shard.Id}");
        Shards.Add(shard);
        ++ShardsSpawned;
    }

    private void RemoveShard(Shard shard)
    {
        Log.Information($"-SHARD:{shard.Name}#{shard.Id}");
        _ = Shards.Remove(shard);
        ++ShardsKilled;
    }

    private void CheckShardStates()
    {
        foreach (Shard shard in Shards)
        {
            if (shard.State is ShardState.Idle or ShardState.Faulted) RemoveShard(shard);
        }
    }
}
