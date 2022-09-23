using System.Text.Json;
using _26listeners.Models;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Events;

namespace _26listeners;
// TODO: logging
internal sealed class Shard : AShard, IDisposable
{
    private TwitchClient client;

    public Shard(string name, int id, TwitchChannel[] channels)
    {
        Name = name;
        Id = id;
        SpawnTime = DateTime.UtcNow;
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
    }
    private TwitchClient Create()
    {
        var wsClient = new WebSocketClient();
        return new TwitchClient(wsClient);
    }

    #region Events
    #region Connection
    private async void OnConnected(object? sender, OnConnectedArgs e)
    {
        _ = await Program.Redis.Sub.PublishAsync("shard:updates", $"{Name}&{Id} CONNECTED");
        State = ShardState.Connected;
        await JoinChannels();
    }

    private async void OnReconnected(object? sender, OnReconnectedEventArgs e)
    {
        _ = await Program.Redis.Sub.PublishAsync("shard:updates", $"{Name}&{Id} RECONNECTED");
        await RejoinOrRespawn();
    }

    private async void OnConnectionError(object? sender, OnConnectionErrorArgs e)
    {
        _ = await Program.Redis.Sub.PublishAsync("shard:updates", $"{Name}&{Id} CONNECTION_ERROR");
        State = ShardState.Faulted;
        Program.Manager.RespawnShard(this);
    }

    private async void OnDisconnected(object? sender, OnDisconnectedEventArgs e)
    {
        _ = await Program.Redis.Sub.PublishAsync("shard:updates", $"{Name}&{Id} DISCONNECTED");
        State = ShardState.Disconnected;
        Program.Manager.RespawnShard(this);
    }
    #endregion

    #region Channels
    private async void OnJoinedChannel(object? sender, OnJoinedChannelArgs e)
    {
        _ = await Program.Redis.Sub.PublishAsync("twitch:channels:updates", $"{Name}&{Id} JOINED {e.Channel}");
    }

    private async void OnLeftChannel(object? sender, OnLeftChannelArgs e)
    {
        _ = await Program.Redis.Sub.PublishAsync("twitch:channels:updates", $"{Name}&{Id} PARTED {e.Channel}");
    }

    private async void OnFailureToReceiveJoinConfirmation(object? sender, OnFailureToReceiveJoinConfirmationArgs e)
    {
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
        if (e.Data == ":tmi.twitch.tv RECONNECT")
        {
            State = ShardState.Connected;
            Program.Manager.RespawnShard(this);
        }
    }

    private void OnError(object? sender, OnErrorEventArgs e)
    {
        State = ShardState.Faulted;
        Program.Manager.RespawnShard(this);
    }
    #endregion
    #endregion

    #region Channel management
    public async Task JoinChannels()
    {
        try
        {
            foreach (TwitchChannel channel in Channels)
            {
                client.JoinChannel(channel.Username);
                await Task.Delay(1000);
            }
        }
        catch (Exception)
        {
            _ = await Program.Redis.Sub.PublishAsync("shard:updates", $"{Name}&{Id} CONNECTED");
            State = ShardState.Faulted;
            return;
        }
        State = ShardState.Active;
    }
    #endregion

    #region Shard management
    private async Task RejoinOrRespawn()
    {
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
    #endregion

    #region IDisposable
    private bool disposedValue;

    private void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                Name = default!;
                client = default!;
            }

            Id = default!;
            SpawnTime = default!;
            State = default!;

            _ = Program.Redis.Sub.Publish("shard:updates", $"{Name}&{Id} DISPOSED");
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
