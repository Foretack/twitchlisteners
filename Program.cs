global using Serilog;
using _26listeners.Models;
using Config.Net;
using Serilog.Events;
using IntervalTimer = System.Timers.Timer;

namespace _26listeners;
internal static class Program
{
    public static RedisConn Redis { get; private set; } = default!;
    public static ShardManager Manager { get; set; } = default!;
    private static readonly IntervalTimer _timer = new();

    private static async Task Main()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File("logs.txt", LogEventLevel.Debug, "{Timestamp:HH:mm:ss zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}", flushToDiskInterval: TimeSpan.FromMinutes(10), rollingInterval: RollingInterval.Day)
            .WriteTo.File("verbose.txt", LogEventLevel.Verbose, "{Timestamp:HH:mm:ss zzz}-----[{Level}]➜ {Message:lj}{NewLine}", flushToDiskInterval: TimeSpan.FromMinutes(10), rollingInterval: RollingInterval.Day)
            .WriteTo.Console(LogEventLevel.Information)
            .CreateLogger();

        var config = new ConfigurationBuilder<IAppConfig>()
            .UseJsonFile("config.json")
            .Build();

        Redis = new RedisConn($"{config.RedisHost},password={config.RedisPass}");

        var channels = await Redis.Cache.GetObjectAsync<TwitchChannel[]>("twitch:channels");

        Log.Debug("starting ShardManager");
        Manager = new ShardManager(channels);
    }
}
