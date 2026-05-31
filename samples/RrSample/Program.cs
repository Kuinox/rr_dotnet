using System.Diagnostics;

var runId = args.Length > 0 ? args[0] : "default";

Console.WriteLine($"rr .NET sample");
Console.WriteLine($"Run id: {runId}");
Console.WriteLine($"Process id: {Environment.ProcessId}");
Console.WriteLine($"Managed thread id: {Environment.CurrentManagedThreadId}");

var cts = new CancellationTokenSource();
var worker = Task.Run(() => BackgroundCounter(cts.Token));

try
{
    var inputs = new[] { 3, 5, 8, 13 };

    foreach (var input in inputs)
    {
        var result = ComputeScore(input);
        Console.WriteLine($"score({input}) = {result}");
        await Task.Delay(50);
    }

    await AsyncCheckpoint("before-exception");
    TriggerAndCatch();
    await AsyncCheckpoint("after-exception");
}
finally
{
    cts.Cancel();
    await worker;
}

Console.WriteLine("sample complete");

static int ComputeScore(int input)
{
    var adjusted = input + 2;
    var fibonacci = Fibonacci(input);
    var checksum = BuildChecksum(adjusted);

    Debugger.Log(0, "sample", $"ComputeScore input={input} adjusted={adjusted}\n");

    return fibonacci + checksum;
}

static int Fibonacci(int value)
{
    if (value <= 1)
    {
        return value;
    }

    return Fibonacci(value - 1) + Fibonacci(value - 2);
}

static int BuildChecksum(int count)
{
    var values = new List<int>(capacity: count);

    for (var i = 0; i < count; i++)
    {
        values.Add((i + 1) * 7);
    }

    return values.Sum() % 97;
}

static async Task AsyncCheckpoint(string name)
{
    Console.WriteLine($"checkpoint: {name}");
    await Task.Delay(75);
}

static void TriggerAndCatch()
{
    try
    {
        ThrowKnownFailure();
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine($"caught: {ex.Message}");
    }
}

static void ThrowKnownFailure()
{
    throw new InvalidOperationException("intentional sample exception");
}

static void BackgroundCounter(CancellationToken cancellationToken)
{
    var tick = 0;

    while (!cancellationToken.IsCancellationRequested)
    {
        tick++;

        if (tick % 10 == 0)
        {
            Console.WriteLine($"background tick: {tick}");
        }

        Thread.Sleep(10);
    }
}
