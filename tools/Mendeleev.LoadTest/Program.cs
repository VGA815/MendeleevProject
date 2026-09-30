using Mendeleev.LoadTest;

// ТЗ 50, «Нагрузочные»: a wave of new users against staging (docs/staging.md, «Нагрузочный тест»).
//   --secret <Telegram__WebhookSecret>  --users 300  --minutes 60  --latency-ms 300  [--target http://app:8080]
// The service must call this stub instead of Telegram: Telegram__ApiBaseUrl=http://loadtest:8081.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("LOADTEST_").AddCommandLine(args);
LoadOptions options = builder.Configuration.Get<LoadOptions>() ?? new LoadOptions();
if (string.IsNullOrWhiteSpace(options.Secret))
{
    Console.Error.WriteLine("--secret (LOADTEST_SECRET) is required: the service's Telegram webhook secret.");
    return 2;
}

builder.WebHost.UseUrls(options.Listen);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var stub = new BotApiStub(TimeSpan.FromMilliseconds(options.LatencyMs));
await using WebApplication app = builder.Build();
stub.Map(app);
await app.StartAsync();

// On start-up the service registers its webhook — with the stub, if it is switched over.
Console.WriteLine($"Bot API stub on {options.Listen}; waiting for the service to call it...");
if (await Task.WhenAny(stub.Connected, Task.Delay(TimeSpan.FromMinutes(5))) != stub.Connected)
{
    Console.Error.WriteLine("The service never called the stub: restart it with Telegram__ApiBaseUrl pointing here.");
    return 2;
}

long firstId = options.FirstId > 0 ? options.FirstId : 8_000_000_000 + Random.Shared.NextInt64(0, 900_000_000);
Console.WriteLine($"{options.Users} users over {options.Minutes} min, Bot API latency {options.LatencyMs} ms, Telegram IDs from {firstId}");

using var http = new HttpClient { BaseAddress = new Uri(options.Target.TrimEnd('/') + "/") };
IReadOnlyList<UserResult> results = await new LoadRun(options, stub, http).RunAsync(firstId, CancellationToken.None);

bool passed = LoadReport.Print(results, Console.Out);
await app.StopAsync();
return passed ? 0 : 1;
