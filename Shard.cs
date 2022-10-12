using System.Text.Json;
using _26listeners.Models;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Events;
using IntervalTimer = System.Timers.Timer;

namespace _26listeners;
internal sealed class Shard : AShard, IDisposable
{
    private TwitchClient client;
    private IntervalTimer timer;

    public Shard(string name, int id, TwitchChannel[] channels)
    {
        Log.Verbose($"CTOR {name}&{id}");
        Name = name;
        Id = id;
        SpawnTime = DateTime.Now;
        Channels = channels;
        State = ShardState.Initializing;

        client = Create();
        var creditentials = new ConnectionCredentials($"justinfan{Random.Shared.Next(10, 1000)}", "something");
        client.Initialize(creditentials);

        // Method order doesn't match, this is just better to look at
        client.OnFailureToReceiveJoinConfirmation += OnFailureToReceiveJoinConfirmation;
        client.OnConnectionError += OnConnectionError;
        client.OnMessageReceived += OnMessageReceived;
        client.OnJoinedChannel += OnJoinedChannel;
        client.OnDisconnected += OnDisconnected;
        client.OnReconnected += OnReconnected;
        client.OnLeftChannel += OnLeftChannel;
        client.OnConnected += OnConnected;
        client.OnError += OnError;
        client.OnLog += OnLog;

        if (!client.Connect()) Program.Manager.RespawnShard(this);
        timer = new IntervalTimer
        {
            Interval = TimeSpan.FromMinutes(15).TotalMilliseconds, // too often makes it write too much into verbose logs
            AutoReset = true,
            Enabled = true
        };
        timer.Elapsed += (_, _) => CheckState(); // Check shard state to avoid bad state exceptions & free memory
    }
    private TwitchClient Create()
    {
        Log.Verbose($"CREATE {Name}&{Id}");
        var wsClient = new WebSocketClient();
        return new TwitchClient(wsClient);
    }

    #region Events
    #region Connection
    private async void OnConnected(object? sender, OnConnectedArgs e)
    {
        Log.Information($"{Name}&{Id} CONNECTED");
        _ = await Program.Redis.Sub.PublishAsync("shard:updates", $"{Name}&{Id} CONNECTED");
        State = ShardState.Connected;
        await JoinChannels();
    }

    private async void OnReconnected(object? sender, OnReconnectedEventArgs e)
    {
        Log.Information($"{Name}&{Id} RECONNECTED");
        _ = await Program.Redis.Sub.PublishAsync("shard:updates", $"{Name}&{Id} RECONNECTED 🔄 ");
        await RejoinOrRespawn();
    }

    private async void OnConnectionError(object? sender, OnConnectionErrorArgs e)
    {
        Log.Error($"{Name}&{Id} CONNECTION_ERROR");
        _ = await Program.Redis.Sub.PublishAsync("shard:updates", $"{Name}&{Id} CONNECTION_ERROR ⚠ ");
        State = ShardState.Faulted;
        Program.Manager.RespawnShard(this);
    }

    private async void OnDisconnected(object? sender, OnDisconnectedEventArgs e)
    {
        Log.Error($"{Name}&{Id} DISCONNECTED");
        _ = await Program.Redis.Sub.PublishAsync("shard:updates", $"{Name}&{Id} DISCONNECTED ⚠ ");
        State = ShardState.Disconnected;
        Program.Manager.RespawnShard(this);
    }
    #endregion

    #region Channels
    private async void OnJoinedChannel(object? sender, OnJoinedChannelArgs e)
    {
        if (State == ShardState.Active)
        {
            Log.Information($"{Name}&{Id} JOINED {e.Channel}");
            _ = await Program.Redis.Sub.PublishAsync("twitch:channels:updates", $"{Name}&{Id} JOINED {e.Channel}");
            return;
        }
        Log.Debug($"{Name}&{Id} JOINED {e.Channel}");
    }

    private async void OnLeftChannel(object? sender, OnLeftChannelArgs e)
    {
        Log.Information($"{Name}&{Id} PARTED {e.Channel}");
        _ = await Program.Redis.Sub.PublishAsync("twitch:channels:updates", $"{Name}&{Id} PARTED {e.Channel}");
    }

    private async void OnFailureToReceiveJoinConfirmation(object? sender, OnFailureToReceiveJoinConfirmationArgs e)
    {
        Log.Warning($"{Name}&{Id} JOIN_ERROR {e.Exception.Channel}");
        _ = await Program.Redis.Sub.PublishAsync("twitch:channels:updates", $"{Name}&{Id} JOIN_ERROR {e.Exception.Channel}");
    }
    #endregion

    #region Chat related
    private async void OnMessageReceived(object? sender, OnMessageReceivedArgs e)
    {
        ChatMessage message = e.ChatMessage;
        string json = JsonSerializer.Serialize(message);
        _ = await Program.Redis.Sub.PublishAsync("twitch:messages", json);
    }
    #endregion

    #region Other
    private void OnLog(object? sender, OnLogArgs e)
    {
        switch (e.Data)
        {
            case "RECONNECT :tmi.twitch.tv":
            case ":tmi.twitch.tv RECONNECT":
                Log.Error($"{Name}&{Id} {nameof(OnLog)}");
                State = ShardState.Disconnected;
                Program.Manager.RespawnShard(this);
                break;
        }
    }

    private void OnError(object? sender, OnErrorEventArgs e)
    {
        Log.Error($"{Name}&{Id} {nameof(OnError)}");
        State = ShardState.Faulted;
        Program.Manager.RespawnShard(this);
    }
    #endregion
    #endregion

    #region Channel management
    private async Task JoinChannels()
    {
        try
        {
            foreach (string channel in Channels.Select(x => x.Username))
            {
                client.JoinChannel(channel);
                Log.Verbose($"{Name}&{Id} ATTEMPTING_JOIN {channel}");
                await Task.Delay(1000);
            }
            Log.Information($"{Name}&{Id} READY");
            _ = await Program.Redis.Sub.PublishAsync("shard:updates", $"{Name}&{Id} READY ✅ ");
        }
        catch (Exception)
        {
            _ = await Program.Redis.Sub.PublishAsync("shard:updates", $"{Name}&{Id} FAULTED");
            Log.Error($"{Name}&{Id} FAULTED");
            State = ShardState.Faulted;
            return;
        }
        State = ShardState.Active;
    }

    public void JoinChannel(TwitchChannel channel)
    {
        if (Channels.Any(x => x.Username == channel.Username)) return;
        client.JoinChannel(channel.Username);
        Channels = Channels.Concat(new[] { channel }).ToArray();
    }

    public void PartChannel(TwitchChannel channel)
    {
        if (!Channels.Any(x => x.Username == channel.Username)) return;
        client.LeaveChannel(channel.Username);
        Channels = Channels.Where(x => x.Username != channel.Username).ToArray();
        if (Channels.Length == 0)
        {
            Log.Error($"{Name}&{Id} NO_CHANNELS");
            State = ShardState.Idle;
        }
    }
    #endregion

    #region Shard management
    private async Task RejoinOrRespawn()
    {
        Log.Information($"{Name}&{Id} {nameof(RejoinOrRespawn)}");
        try
        {
            State = ShardState.Connected;
            if (client.JoinedChannels.Count == 0)
            {
                await JoinChannels();
            }
            State = ShardState.Active;
        }
        catch
        {
            Program.Manager.RespawnShard(this);
        }
    }

    private void CheckState()
    {
        try
        {
            Log.Verbose($"{Name}&{Id} is connected to {client.JoinedChannels.Count} channels");
            client.SendRaw("PING");
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"{Name}&{Id}:{client.JoinedChannels.Count} BAD_STATE");
            State = ShardState.Idle;
        }
        if (Channels.Length == 0
        || client.JoinedChannels.Count == 0
        || State is ShardState.Faulted or ShardState.Uninitialized or ShardState.Disconnected)
        {
            Log.Error($"{Name}&{Id} BAD_STATE");
            State = ShardState.Idle;
        }
    }
    #endregion

    #region IDisposable
    private bool disposedValue;

    private void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            Log.Warning($"{Name}&{Id} DISPOSING");
            _ = Program.Redis.Sub.Publish("shard:updates", $"{Name}&{Id} DISPOSING ⚠ ");
            if (disposing)
            {
                Name = default!;
                client = default!;
                timer = default!;
            }

            Id = default!;
            SpawnTime = default!;
            State = ShardState.Killed;

            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    ~Shard()
    {
        Dispose(disposing: false);
    }
    #endregion
}
