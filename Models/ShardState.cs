namespace _26listeners.Models;
internal enum ShardState : byte
{
    Uninitialized,
    Initializing,
    Connected,
    Disconnected,
    Active,
    Idle,
    Faulted,
    Killed
}
