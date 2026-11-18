using Amethyst;
using Amethyst.Shards;

string path = args.Length > 0 ? args[0] : "server.yaml";
ServerOptions options = ServerOptions.Load(path);
Protocol.TracePackets = Environment.GetEnvironmentVariable("AMETHYST_TRACE_PACKETS") == "1";
using var shutdown = new CancellationTokenSource();
void OnCancel(object? sender, ConsoleCancelEventArgs eventArgs)
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
}
Console.CancelKeyPress += OnCancel;
try
{
    await using var terminal = new ServerConsole(options.ConsoleMode, options.ConsoleHistorySize);
    Console.WriteLine("Amethyst Core — Java 26.3 / protocol 777 / .NET 10");
    await using var shards = new ShardHost(options, options.ShardsDirectory,
        options.ShardsDataDirectory);
    await shards.LoadAsync(shutdown.Token);
    await new AmethystNetworkServer(options, shards).RunAsync(shutdown.Token, terminal);
}
finally
{
    Console.CancelKeyPress -= OnCancel;
}
