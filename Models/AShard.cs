namespace _26listeners.Models;
internal abstract class AShard
{
    public string Name { get; protected set; } = default!;
    public int Id { get; protected set; }
    public ShardState State { get; protected set; } = ShardState.Uninitialized;
    public DateTime SpawnTime { get; protected set; }
    public TwitchChannel[] Channels { get; set; } = default!;
}
