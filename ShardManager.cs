﻿using System.Text.Json;
using _26listeners.Models;
using StackExchange.Redis;
using IntervalTimer = System.Timers.Timer;

namespace _26listeners;
internal sealed class ShardManager
{
    public static TwitchChannel[] Channels { get; private set; } = Array.Empty<TwitchChannel>();

    private List<Shard> Shards { get; set; } = new();
    private int ChannelsPerShard { get; set; }
    private int ShardsSpawned { get; set; }
    private int ShardsKilled { get; set; }
    private readonly IntervalTimer _timer = new();

    public ShardManager(string channelsJson, int channelsPerShard = 25)
    {
        ChannelsPerShard = channelsPerShard;
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
        _timer.Elapsed += async (_, _) => await CheckShardStates();

        Program.Redis["shard:manage"].OnMessage(async message => await ManageShard(message));
    }

    #region Shards
    public string Ping()
    {
        return $"{Shards.Count} active, {ShardsSpawned} spawned, {ShardsKilled} killed " +
            $"| uptime avg: {Shards.Average(x => new DateTimeOffset(x.SpawnTime).ToUnixTimeSeconds())}";
    }

    /// <summary>
    /// Adds a new identical shard then removes the passed one
    /// </summary>
    public void RespawnShard(Shard shard)
    {
        _ = Program.Redis.Sub.Publish("shard:status:all", $"{shard.Name}#{shard.Id} RESPAWN");
        _ = Program.Redis.Sub.Publish($"shard:status:{shard.Id}", $"{shard.Name}#{shard.Id} RESPAWN");
        AddShard(new Shard(shard.Name, ShardsSpawned, shard.Channels));
        RemoveShard(shard);

    }

    /// <summary>
    /// Adds a new shard
    /// </summary>
    private void AddShard(Shard shard)
    {
        Log.Information($"+SHARD:{shard.Name}#{shard.Id}");
        _ = Program.Redis.Sub.Publish("shard:status:all", $"{shard.Name}#{shard.Id} ADD");
        _ = Program.Redis.Sub.Publish($"shard:status:{shard.Id}", $"{shard.Name}#{shard.Id} ADD");
        Shards.Add(shard);
        ++ShardsSpawned;
    }

    /// <summary>
    /// Removes a shard then disposes it
    /// </summary>
    private void RemoveShard(Shard shard)
    {
        Log.Information($"-SHARD:{shard.Name}#{shard.Id}");
        _ = Program.Redis.Sub.Publish("shard:status:all", $"{shard.Name}#{shard.Id} REMOVE");
        _ = Program.Redis.Sub.Publish($"shard:status:{shard.Id}", $"{shard.Name}#{shard.Id} REMOVE");
        _ = Shards.Remove(shard);
        shard.Dispose();
        ++ShardsKilled; // this is not the active amount. stop decrementing it retard
    }

    /// <summary>
    /// Removes or respawns inactive shards
    /// </summary>
    private async Task CheckShardStates()
    {
        foreach (Shard shard in Shards)
        {
            if (shard.State is ShardState.Idle or ShardState.Faulted)
            {
                if (shard.Channels.Length == 0) RemoveShard(shard);
                else RespawnShard(shard);
            }
        }
        await RecalibrateTwitchChannels();
    }

    /// <summary>
    /// shard:manage method
    /// </summary>
    private async Task ManageShard(ChannelMessage channelMessage)
    {
        await Task.Run(async () =>
        {
            Log.Verbose(channelMessage.Message!);
            try
            {
                // id REMOVE, id RESPAWN or PING
                string[] content = channelMessage.Message.ToString().Split(' ');
                if (content[0] == "PING")                                               // PING
                {
                    _ = await Program.Redis.Sub.PublishAsync("shard:manage", Ping());
                    return;
                }
                int id = int.Parse(content[0]);
                bool remove = content[1] == "REMOVE";
                Shard target = Shards.First(x => x.Id == id);

                if (remove)
                {
                    RemoveShard(target);
                    return;
                }
                RespawnShard(target);
            }
            catch
            {
                Log.Error($"unrecognized shard:manage command: {channelMessage.Message}");
            }
        });
    }
    #endregion

    #region Twitch channels
    /// <summary>
    /// Loads twitch channels again from twitch:channels and checks for changes to be made to shards
    /// </summary>
    private async Task RecalibrateTwitchChannels()
    {
        RedisValue channelsRedis = await Program.Redis.Db.StringGetAsync("twitch:channels");
        while (!channelsRedis.HasValue)
        {
            Log.Warning("twitch:channels not found in Redis. Unable to update");
            await Task.Delay(60000);
            channelsRedis = await Program.Redis.Db.StringGetAsync("twitch:channels");
        }

        TwitchChannel[]? _channels = JsonSerializer.Deserialize<TwitchChannel[]>(channelsRedis!)?
            .Where(x => Channels.Max(y => y.Priority) != x.Priority)
            .Where(x => x.Priority > -10)
            .OrderByDescending(x => x.Priority)
            .ToArray();
        if (_channels is null) return;

        IEnumerable<TwitchChannel> removedChannels = Channels.ExceptBy(_channels.Select(x => x.Username), x => x.Username);
        IEnumerable<TwitchChannel> addedChannels = _channels.ExceptBy(Channels.Select(x => x.Username), x => x.Username);
        foreach (TwitchChannel? channel in removedChannels)
        {
            if (channel is null) return;
            Shards.FirstOrDefault(x => x.Channels.Any(x => x.Username == channel.Username))?.PartChannel(channel);
            await Task.Delay(1000);
        }
        foreach (TwitchChannel? channel in addedChannels)
        {
            if (channel is null) return;
            Shards.FirstOrDefault(x => x.Channels.Any(x => x.Username == channel.Username))?.JoinChannel(channel);
            await Task.Delay(1000);
        }
    }
    #endregion
}
