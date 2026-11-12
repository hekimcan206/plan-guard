using PlanGuard.Cli;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await new App(Console.Out, Console.Error).RunAsync(args, cts.Token);
