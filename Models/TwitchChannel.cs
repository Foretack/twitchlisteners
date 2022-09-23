namespace _26listeners.Models;
internal sealed record TwitchChannel(
    string DisplayName,
    string Username,
    string ID,
    string AvatarUrl,
    DateTime DateJoined,
    int Priority,
    bool Logged
    );
