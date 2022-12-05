using System.Text.Json;
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
    private bool Recalibrating { get; set; }
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
        Log.Information($"[M] CHUNKS:{chunks.Length}");

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
        _timer.Elapsed += async (_, _) =>
        {
            _ = await Program.Redis.Db.StringSetAsync("shards:ping", Ping(), TimeSpan.FromHours(1));
        };

        Program.Redis["shard:manage"].OnMessage(async message => await ManageShard(message));
    }

    #region Shards
    public string Ping()
    {
        Log.Verbose("[M] shard status pinged");
        return $"\"{ShardsSpawned - ShardsKilled} active, {ShardsSpawned} spawned, {ShardsKilled} killed\"";
    }

    /// <summary>
    /// Adds a new identical shard then removes the passed one
    /// </summary>
    public void RespawnShard(Shard shard)
    {
        if (!string.IsNullOrEmpty(shard.Name) || shard.Name.Length > 2)
        {
            _ = Program.Redis.Sub.Publish("shard:updates", $"{shard.Name}&{shard.Id}|{shard.State} RESPAWN ♻ ");
            _ = Program.Redis.Sub.Publish($"shard:updates:{shard.Id}", $"{shard.Name}&{shard.Id}|{shard.State} RESPAWN ♻ ");
            AddShard(new Shard(shard.Name, ShardsSpawned, shard.Channels));
        }
        else
            AddShard(new Shard("NONAME", ShardsSpawned, shard.Channels));
        RemoveShard(shard);
    }

    /// <summary>
    /// Adds a new shard
    /// </summary>
    private void AddShard(Shard shard)
    {
        Log.Information($"[M] +SHARD:{shard.Name}#{shard.Id}");
        _ = Program.Redis.Sub.Publish("shard:updates", $"{shard.Name}&{shard.Id} ADD 🥤 ");
        _ = Program.Redis.Sub.Publish($"shard:updates:{shard.Id}", $"{shard.Name}&{shard.Id} ADD 🥤 ");
        Shards.Add(shard);
        ++ShardsSpawned;
    }

    /// <summary>
    /// Removes a shard then disposes it
    /// </summary>
    private void RemoveShard(Shard shard)
    {
        Log.Information($"[M] -SHARD:{shard.Name}#{shard.Id}");
        _ = Program.Redis.Sub.Publish("shard:updates", $"{shard.Name}&{shard.Id}|{shard.State} REMOVE ⛔ ");
        _ = Program.Redis.Sub.Publish($"shard:updates:{shard.Id}", $"{shard.Name}&{shard.Id}|{shard.State} REMOVE ⛔ ");
        _ = Shards.Remove(shard);
        shard.Dispose();
        ++ShardsKilled; // this is not the active amount. stop decrementing it retard
    }

    /// <summary>
    /// Removes or respawns inactive shards
    /// </summary>
    private async Task CheckShardStates()
    {
        Log.Verbose($"[M] {nameof(CheckShardStates)} invoked");
        try
        {
            await ReloadTwitchChannels();
            while (Recalibrating) await Task.Delay(1000); // don't proceed until the method finishes
            List<(bool remove, Shard shard)> temp = new(); // do this to avoid reading and writing at the same time
            foreach (Shard shard in Shards)
            {
                Log.Verbose($"[M] {nameof(CheckShardStates)}: {shard.Name}&{shard.Id}:{shard.State} ➜{shard.SpawnTime}");
                if (shard.State is ShardState.Idle or ShardState.Faulted)
                {
                    if (shard.Channels.Length == 0) temp.Add((true, shard)); // No channels left; shard has no use
                    else temp.Add((false, shard));
                }
            }
            Log.Verbose($"[M] {nameof(CheckShardStates)} taking action on {temp.Count} shards");
            foreach ((bool remove, Shard shard) in temp)
            {
                if (remove)
                {
                    RemoveShard(shard);
                    continue;
                }
                RespawnShard(shard);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[M] Error checking shard states");
        }
    }

    /// <summary>
    /// shard:manage method
    /// </summary>
    private async Task ManageShard(ChannelMessage channelMessage)
    {
        Log.Verbose($"[M] {channelMessage.Channel} message: {channelMessage.Message}");
        await Task.Run(() =>
        {
            Log.Verbose("[M] incoming message:" + channelMessage.Message!);
            try
            {
                // id REMOVE, id RESPAWN
                string[] content = channelMessage.Message.ToString().Split(' ');
                int id = int.Parse(content[0][1..]); // id of shard to modify
                bool remove = content[1].Contains("REMOVE"); // action
                Shard target = Shards.First(x => x.Id == id); // find shard with id

                if (remove)
                {
                    RemoveShard(target);
                    return;
                }
                RespawnShard(target);
            }
            catch
            {
                if (channelMessage.Message.ToString().Contains("active,")) return;
                Log.Error($"[M] unrecognized shard:manage command: {channelMessage.Message}");
            }
        });
    }
    #endregion

    #region Twitch channels
    /// <summary>
    /// Loads twitch channels again from twitch:channels and checks for changes to be made to shards
    /// </summary>
    private async Task ReloadTwitchChannels()
    {
        Log.Verbose($"{nameof(ReloadTwitchChannels)} invoked");
        Recalibrating = true;
        RedisValue channelsRedis = await Program.Redis.Db.StringGetAsync("twitch:channels");
        while (!channelsRedis.HasValue)
        {
            Log.Warning("twitch:channels not found in Redis. Unable to update");
            await Task.Delay(60000);
            channelsRedis = await Program.Redis.Db.StringGetAsync("twitch:channels");
        }

        TwitchChannel[]? _channels = JsonSerializer.Deserialize<TwitchChannel[]>(channelsRedis!)?
            .Where(x => x.Priority > -10)
            .OrderByDescending(x => x.Priority)
            .ToArray();
        if (_channels is null) return;
        Log.Debug($"[M] Loaded {_channels.Length} channels");

        IEnumerable<TwitchChannel> removedChannels = Channels.ExceptBy(_channels.Select(x => x.Username), x => x.Username);
        IEnumerable<TwitchChannel> addedChannels = _channels.ExceptBy(Channels.Select(x => x.Username), x => x.Username);
        if (removedChannels.Any() || addedChannels.Any())
        {
            Channels = Channels.Where(x => !removedChannels.Contains(x)).Concat(addedChannels).ToArray();
        }

        Log.Debug($"[M] channels to be removed: {string.Join(", ", removedChannels)}");
        Log.Debug($"[M] channels to be added: {string.Join(", ", addedChannels)}");
        foreach (TwitchChannel? channel in removedChannels)
        {
            if (channel is null) return;
            Shards.FirstOrDefault(x => x.Channels.Any(x => x.Username == channel.Username))?.PartChannel(channel);
            await Task.Delay(1000);
        }
        foreach (TwitchChannel? channel in addedChannels)
        {
            if (channel is null) return;
            Shard? targetChannel = Shards.FirstOrDefault(x => x.Name != "MAIN" && x.Channels.Length < ChannelsPerShard);
            if (targetChannel is null)
            {
                AddShard(new Shard("TEMP", ShardsSpawned, new[] { channel }));
                return;
            }
            targetChannel.JoinChannel(channel);
            await Task.Delay(1000);
        }

        Log.Debug("[M] Validating channel connections...");
        TwitchChannel[] unjoined = Channels.Where(x => //channels where no shard contains them
                            !Shards.Any(y =>
                                y.Channels.Any(z => z.Username == x.Username)))
                                    .ToArray();
        Log.Debug($"[M] unjoined = {unjoined.Length}");

        IEnumerable<TwitchChannel[]> chunks = Channels.Chunk(ChannelsPerShard);
        if (chunks.Any(x => x.Length == unjoined.Length))
        {
            Log.Information("[M] Creating missing shard...");
            AddShard(new Shard("TEMP", ShardsSpawned, unjoined));
        }

        Log.Debug("[M] Checking for duplicates...");
        for (int i = 0; i < Shards.Count; i++)
        {
            Shard shard = Shards[i];
            if (Shards.Any(x => x.Name != shard.Name && x.Id != shard.Id
            && Enumerable.SequenceEqual(x.Channels.Select(x => x.Username), shard.Channels.Select(x => x.Username))))
            {
                Log.Debug($"Found duplicate shard: {shard.Name}&{shard.Id}");
                RemoveShard(shard);
            }
        }

        Recalibrating = false;
    }
    #endregion
}
