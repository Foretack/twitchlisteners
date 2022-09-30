global using Serilog;
using CliWrap;
using CliWrap.Buffered;
using Serilog.Events;
using StackExchange.Redis;
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
            .WriteTo.File("verbose.txt", LogEventLevel.Verbose, "{Timestamp:HH:mm:ss zzz}-----[{Level}]➜ {Message:lj}{NewLine}", flushToDiskInterval: TimeSpan.FromMinutes(10), rollingInterval: RollingInterval.Day)
            .WriteTo.File("logs.txt", LogEventLevel.Debug, "{Timestamp:HH:mm:ss zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}", flushToDiskInterval: TimeSpan.FromMinutes(10), rollingInterval: RollingInterval.Day)
            .WriteTo.Console(LogEventLevel.Information)
            .CreateLogger();
        Redis = new RedisConn("localhost");

        RedisValue channelsRedis = await Redis.Db.StringGetAsync("twitch:channels");
        while (!channelsRedis.HasValue)
        {
            Log.Error("twitch:channels not found in Redis");
            await Task.Delay(5000);
            channelsRedis = await Redis.Db.StringGetAsync("twitch:channels");
        }
        Log.Information("got twitch:channels");

        Log.Debug("starting ShardManager");
        Manager = new ShardManager(channelsRedis.ToString());

        _timer.Interval = TimeSpan.FromHours(12).TotalMilliseconds;
        _timer.AutoReset = true;
        _timer.Enabled = true;
        _timer.Elapsed += async (_, _) => await GitPull();
        _ = Console.ReadLine();
    }

    private static async Task GitPull()
    {
        Log.Information($"Executing {nameof(GitPull)}");
        try
        {
            BufferedCommandResult pullResults = await Cli.Wrap("git").WithArguments("pull").ExecuteBufferedAsync();
            Log.Verbose(pullResults.StandardOutput);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"{nameof(GitPull)} failed");
        }
        Log.Verbose($"Finished {nameof(GitPull)}");
    }
}
