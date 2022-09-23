using Serilog;
using StackExchange.Redis;

namespace _26listeners;
// TODO: set up file logger
// TODO: do more logging
internal static class Program
{
    public static RedisConn Redis { get; private set; } = default!;
    public static ShardManager Manager { get; set; } = default!;

    private static async Task Main()
    {
        Redis = new RedisConn("localhost");

        RedisValue channelsRedis = await Redis.Db.StringGetAsync("twitch:channels");
        while (!channelsRedis.HasValue)
        {
            Log.Error("twitch:channels not found in Redis");
            await Task.Delay(5000);
            channelsRedis = await Redis.Db.StringGetAsync("twitch:channels");
        }

        Manager = new ShardManager(channelsRedis.ToString());
        Console.WriteLine("Hello, World!");
    }
}
