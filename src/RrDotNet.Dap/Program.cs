using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Draco.Dap.Adapter;
using Draco.Dap.Attributes;
using Draco.Dap.Model;
using Microsoft.Diagnostics.Runtime;
using DapStackFrame = Draco.Dap.Model.StackFrame;
using Thread = Draco.Dap.Model.Thread;

var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var pipe = new StreamDuplexPipe(input, output);
var client = DebugAdapter.Connect(pipe);
var adapter = new RrDotNetDebugAdapter(client);
await client.RunAsync(adapter);

internal interface IRrDotNetCapabilities
{
    [Capability(nameof(Capabilities.SupportsConfigurationDoneRequest))]
    bool SupportsConfigurationDoneRequest => false;

    [Capability(nameof(Capabilities.SupportsStepBack))]
    bool SupportsStepBack => true;

    [Capability(nameof(Capabilities.SupportsModulesRequest))]
    bool SupportsModulesRequest => false;
}

internal sealed class RrDotNetDebugAdapter(IDebugClient client) : IDebugAdapter, IRrDotNetCapabilities
{
    private readonly Dictionary<int, IReadOnlyList<Variable>> _variables = [];
    private RrInspectionSession? _session;
    private int _nextVariablesReference = 1;

    public Task InitializeAsync(InitializeRequestArguments args) => Task.CompletedTask;

    public async Task<LaunchResponse> LaunchAsync(LaunchRequestArguments args)
    {
        var trace = GetString(args.LaunchAttributes, "trace")
            ?? GetString(args.LaunchAttributes, "traceDir")
            ?? throw new InvalidOperationException("launch requires a 'trace' or 'traceDir' argument");
        var replayEvent = GetString(args.LaunchAttributes, "event") ?? "5000";

        _session?.Dispose();
        _session = await RrInspectionSession.StartAsync(trace, replayEvent);
        BuildVariableHandles();

        await client.ProcessStartedAsync(new ProcessEvent
        {
            Name = Path.GetFileName(trace),
            SystemProcessId = _session.ProcessId,
            IsLocalProcess = true,
            StartMethod = ProcessEvent.ProcessStartMethod.Launch,
            PointerSize = _session.Reader.PointerSize,
        });
        await client.SendOutputAsync(new OutputEvent
        {
            Category = OutputEvent.OutputCategory.Console,
            Output = $"rr trace loaded at event {replayEvent}: {Path.GetFullPath(trace)}{Environment.NewLine}",
        });
        await client.StoppedAsync(new StoppedEvent
        {
            Reason = StoppedEvent.StoppedReason.Entry,
            Description = "rr replay snapshot loaded",
            AllThreadsStopped = true,
            ThreadId = _session.ManagedThreads.FirstOrDefault()?.Id,
        });

        return new LaunchResponse();
    }

    public async Task<AttachResponse> AttachAsync(AttachRequestArguments args)
    {
        var launchArgs = new LaunchRequestArguments { LaunchAttributes = args.AttachAttributes };
        await LaunchAsync(launchArgs);
        return new AttachResponse();
    }

    public async Task<ContinueResponse> ContinueAsync(ContinueArguments args)
    {
        await client.TerminatedDebuggerAsync();
        return new ContinueResponse { AllThreadsContinued = true };
    }

    public async Task<PauseResponse> PauseAsync(PauseArguments args)
    {
        EnsureSession();
        await client.StoppedAsync(new StoppedEvent
        {
            Reason = StoppedEvent.StoppedReason.Pause,
            AllThreadsStopped = true,
            ThreadId = _session!.ManagedThreads.FirstOrDefault()?.Id,
        });
        return new PauseResponse();
    }

    public async Task<TerminateResponse> TerminateAsync(TerminateArguments args)
    {
        _session?.Dispose();
        _session = null;
        await client.DebuggerTerminatedAsync(new TerminatedEvent());
        return new TerminateResponse();
    }

    public async Task<StepInResponse> StepIntoAsync(StepInArguments args)
    {
        await SendStepStoppedAsync(args.ThreadId);
        return new StepInResponse();
    }

    public async Task<NextResponse> StepOverAsync(NextArguments args)
    {
        await SendStepStoppedAsync(args.ThreadId);
        return new NextResponse();
    }

    public async Task<StepOutResponse> StepOutAsync(StepOutArguments args)
    {
        await SendStepStoppedAsync(args.ThreadId);
        return new StepOutResponse();
    }

    public Task<SetBreakpointsResponse> SetBreakpointsAsync(SetBreakpointsArguments args)
    {
        var breakpoints = (args.Breakpoints ?? [])
            .Select((bp, index) => new Breakpoint
            {
                Id = index + 1,
                Verified = false,
                Message = "source breakpoints are not implemented yet; this adapter currently exposes rr snapshots",
                Source = args.Source,
                Line = bp.Line,
                Column = bp.Column,
            })
            .ToArray();
        return Task.FromResult(new SetBreakpointsResponse { Breakpoints = breakpoints });
    }

    public Task<ThreadsResponse> GetThreadsAsync()
    {
        EnsureSession();
        return Task.FromResult(new ThreadsResponse
        {
            Threads = _session!.ManagedThreads
                .Select(t => new Thread { Id = t.Id, Name = t.Name })
                .ToArray(),
        });
    }

    public Task<StackTraceResponse> GetStackTraceAsync(StackTraceArguments args)
    {
        EnsureSession();
        var thread = _session!.ManagedThreads.FirstOrDefault(t => t.Id == args.ThreadId);
        var frame = new DapStackFrame
        {
            Id = args.ThreadId,
            Name = thread is null ? "rr replay snapshot" : $"managed thread {thread.ManagedThreadId}",
            Line = 1,
            Column = 1,
            PresentationHint = DapStackFrame.StackPresentationHint.Label,
        };
        return Task.FromResult(new StackTraceResponse
        {
            StackFrames = [frame],
            TotalFrames = 1,
        });
    }

    public Task<ScopesResponse> GetScopesAsync(ScopesArguments args)
    {
        EnsureSession();
        return Task.FromResult(new ScopesResponse
        {
            Scopes =
            [
                new Scope
                {
                    Name = "rr / CLRMD snapshot",
                    VariablesReference = 1,
                    Expensive = false,
                    PresentationHint = Scope.ScopePresentationHint.Locals,
                },
            ],
        });
    }

    public Task<VariablesResponse> GetVariablesAsync(VariablesArguments args)
    {
        EnsureSession();
        _variables.TryGetValue(args.VariablesReference, out var variables);
        return Task.FromResult(new VariablesResponse { Variables = variables?.ToArray() ?? [] });
    }

    public Task<SourceResponse> GetSourceAsync(SourceArguments args)
    {
        if (args.Source?.Path is { } path && File.Exists(path))
        {
            return Task.FromResult(new SourceResponse { Content = File.ReadAllText(path) });
        }

        return Task.FromResult(new SourceResponse { Content = "" });
    }

    public void Dispose()
    {
        _session?.Dispose();
    }

    private void BuildVariableHandles()
    {
        EnsureSession();
        _variables.Clear();
        _nextVariablesReference = 2;

        var root = new List<Variable>
        {
            Scalar("trace", _session!.TracePath, "string"),
            Scalar("event", _session.Event, "string"),
            Scalar("recordedPid", _session.ProcessId.ToString(CultureInfo.InvariantCulture), "int"),
            Scalar("clrVersions", _session.ClrVersionCount.ToString(CultureInfo.InvariantCulture), "int"),
            Scalar("heapCanWalk", _session.HeapCanWalk.ToString(), "bool"),
            Scalar("systemStringMethodTable", $"0x{_session.SystemStringMethodTable:x16}", "address"),
        };

        var modulesReference = AddVariables(_session.InterestingModules.Select(m => Scalar(Path.GetFileName(m.FileName), $"0x{m.ImageBase:x16} {m.FileName}", "module")));
        root.Add(new Variable
        {
            Name = "modules",
            Value = $"{_session.InterestingModules.Count} interesting modules",
            Type = "module[]",
            VariablesReference = modulesReference,
            NamedVariables = _session.InterestingModules.Count,
        });

        _variables[1] = root;
    }

    private int AddVariables(IEnumerable<Variable> variables)
    {
        var reference = _nextVariablesReference++;
        _variables[reference] = variables.ToArray();
        return reference;
    }

    private static Variable Scalar(string name, string value, string? type = null) => new()
    {
        Name = name,
        Value = value,
        Type = type,
        VariablesReference = 0,
    };

    private async Task SendStepStoppedAsync(int? threadId)
    {
        EnsureSession();
        await client.StoppedAsync(new StoppedEvent
        {
            Reason = StoppedEvent.StoppedReason.Step,
            Description = "execution control is not implemented yet",
            Text = "rr snapshot adapter: step request acknowledged without changing replay event",
            AllThreadsStopped = true,
            ThreadId = threadId ?? _session!.ManagedThreads.FirstOrDefault()?.Id,
        });
    }

    private void EnsureSession()
    {
        if (_session is null)
        {
            throw new InvalidOperationException("no rr trace is loaded");
        }
    }

    private static string? GetString(Dictionary<string, JsonElement>? values, string key)
    {
        return values is not null
            && values.TryGetValue(key, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

internal sealed class RrInspectionSession : IDisposable
{
    private readonly RrReplaySession _rr;
    private readonly GdbMiClient _gdb;
    private readonly DataTarget _target;
    private readonly ClrRuntime? _runtime;

    private RrInspectionSession(
        string tracePath,
        string replayEvent,
        RrReplaySession rr,
        GdbMiClient gdb,
        RrGdbDataReader reader,
        DataTarget target,
        ClrRuntime? runtime)
    {
        TracePath = tracePath;
        Event = replayEvent;
        _rr = rr;
        _gdb = gdb;
        Reader = reader;
        _target = target;
        _runtime = runtime;
        ProcessId = rr.RecordedProcessId;
        InterestingModules = reader.Modules.Where(m => IsInterestingModule(m.FileName)).ToArray();
        ClrVersionCount = target.ClrVersions.Length;
        HeapCanWalk = runtime?.Heap.CanWalkHeap ?? false;
        SystemStringMethodTable = runtime?.Heap.GetTypeByName("System.String")?.MethodTable ?? 0;
        ManagedThreads = runtime?.Threads.Select((t, i) => new ManagedThreadInfo(
            Id: t.OSThreadId == 0 ? i + 1 : checked((int)t.OSThreadId),
            ManagedThreadId: checked((ulong)t.ManagedThreadId),
            Name: $"managed {t.ManagedThreadId} / os 0x{t.OSThreadId:x}")).ToArray() ?? [];
    }

    public string TracePath { get; }
    public string Event { get; }
    public int ProcessId { get; }
    public RrGdbDataReader Reader { get; }
    public IReadOnlyList<ModuleInfo> InterestingModules { get; }
    public int ClrVersionCount { get; }
    public bool HeapCanWalk { get; }
    public ulong SystemStringMethodTable { get; }
    public IReadOnlyList<ManagedThreadInfo> ManagedThreads { get; }

    public static async Task<RrInspectionSession> StartAsync(string tracePath, string replayEvent)
    {
        var rr = await RrReplaySession.StartAsync(Path.GetFullPath(tracePath), replayEvent);
        var gdb = await GdbMiClient.ConnectAsync(rr.ExecutablePath, rr.Port);
        var reader = new RrGdbDataReader(gdb, rr.RecordedProcessId);
        var target = new DataTarget(reader, new DataTargetOptions());
        ClrRuntime? runtime = null;

        if (target.ClrVersions.Length > 0)
        {
            var clr = target.ClrVersions[0];
            var dacPath = clr.DebuggingLibraries.Select(l => l.FileName).FirstOrDefault(File.Exists)
                ?? "/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.8/libmscordaccore.so";
            runtime = clr.CreateRuntime(dacPath, ignoreMismatch: true);
        }

        return new RrInspectionSession(tracePath, replayEvent, rr, gdb, reader, target, runtime);
    }

    public void Dispose()
    {
        _runtime?.Dispose();
        _target.Dispose();
        Reader.Dispose();
        _gdb.Dispose();
        _rr.Dispose();
    }

    private static bool IsInterestingModule(string fileName)
    {
        return fileName.Contains("RrSample", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("libcoreclr", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("libclrjit", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record ManagedThreadInfo(int Id, ulong ManagedThreadId, string Name);

internal sealed class RrReplaySession : IDisposable
{
    private readonly Process _process;

    private RrReplaySession(Process process, string executablePath, int port, int recordedProcessId)
    {
        _process = process;
        ExecutablePath = executablePath;
        Port = port;
        RecordedProcessId = recordedProcessId;
    }

    public string ExecutablePath { get; }
    public int Port { get; }
    public int RecordedProcessId { get; }

    public static async Task<RrReplaySession> StartAsync(string traceDir, string replayEvent)
    {
        var start = new ProcessStartInfo
        {
            FileName = "rr",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("replay");
        start.ArgumentList.Add("-g");
        start.ArgumentList.Add(replayEvent);
        start.ArgumentList.Add("-s");
        start.ArgumentList.Add("0");
        start.ArgumentList.Add(traceDir);

        var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start rr replay");
        var lines = new BlockingCollection<string>();
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null) lines.Add(line);
        });
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null) lines.Add(line);
        });

        var output = new StringBuilder();
        var timeout = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < timeout)
        {
            if (!lines.TryTake(out var line, TimeSpan.FromMilliseconds(250)))
            {
                if (process.HasExited) break;
                continue;
            }
            output.AppendLine(line);
            if (output.ToString().Contains("127.0.0.1:", StringComparison.Ordinal)) break;
        }

        var text = output.ToString();
        var port = ParsePort(text);
        var executable = ParseLastQuotedArgument(text);
        var pid = ParseRecordedPid(text);
        if (port == 0 || executable is null)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"failed to parse rr output:{Environment.NewLine}{text}");
        }
        return new RrReplaySession(process, executable, port, pid);
    }

    public void Dispose()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        _process.Dispose();
    }

    private static int ParsePort(string text)
    {
        var marker = "127.0.0.1:";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return 0;
        index += marker.Length;
        var end = index;
        while (end < text.Length && char.IsDigit(text[end])) end++;
        return int.TryParse(text[index..end], CultureInfo.InvariantCulture, out var port) ? port : 0;
    }

    private static int ParseRecordedPid(string text)
    {
        var marker = "Process id: ";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return 0;
        index += marker.Length;
        var end = index;
        while (end < text.Length && char.IsDigit(text[end])) end++;
        return int.TryParse(text[index..end], CultureInfo.InvariantCulture, out var pid) ? pid : 0;
    }

    private static string? ParseLastQuotedArgument(string text)
    {
        var parts = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var start = text.IndexOf('\'', index);
            if (start < 0) break;
            var end = text.IndexOf('\'', start + 1);
            if (end < 0) break;
            parts.Add(text[(start + 1)..end]);
            index = end + 1;
        }
        return parts.LastOrDefault(p => p.StartsWith("/", StringComparison.Ordinal));
    }
}

internal sealed class GdbMiClient : IDisposable
{
    private readonly Process _process;
    private readonly BlockingCollection<string> _lines = [];
    private int _token;

    private GdbMiClient(Process process)
    {
        _process = process;
        _ = Task.Run(ReadOutputLoop);
        _ = Task.Run(ReadErrorLoop);
    }

    public static async Task<GdbMiClient> ConnectAsync(string executablePath, int port)
    {
        var start = new ProcessStartInfo
        {
            FileName = "gdb",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--quiet");
        start.ArgumentList.Add("--interpreter=mi2");
        start.ArgumentList.Add(executablePath);

        var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start gdb");
        var client = new GdbMiClient(process);
        await client.WaitForPromptAsync();
        await client.CommandAsync("-gdb-set pagination off");
        await client.CommandAsync("-gdb-set confirm off");
        await client.CommandAsync("-gdb-set sysroot /");
        await client.CommandAsync($"-target-select extended-remote 127.0.0.1:{port}");
        return client;
    }

    public async Task<string> ConsoleCommandAsync(string command)
    {
        var escaped = command.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return await CommandAsync($"-interpreter-exec console \"{escaped}\"");
    }

    public async Task<byte[]> ReadMemoryAsync(ulong address, int count)
    {
        var result = await CommandAsync($"-data-read-memory-bytes 0x{address:x} {count}");
        var contents = ExtractMiString(result, "contents=");
        return contents is null ? [] : Convert.FromHexString(contents);
    }

    public async Task<string> CommandAsync(string command)
    {
        var token = Interlocked.Increment(ref _token);
        await _process.StandardInput.WriteLineAsync($"{token}{command}");
        await _process.StandardInput.FlushAsync();

        var builder = new StringBuilder();
        while (true)
        {
            var line = _lines.Take();
            builder.AppendLine(line);
            if (line.StartsWith($"{token}^done", StringComparison.Ordinal)
                || line.StartsWith($"{token}^error", StringComparison.Ordinal)
                || line.StartsWith($"{token}^connected", StringComparison.Ordinal))
            {
                await WaitForPromptAsync();
                return builder.ToString();
            }
        }
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.StandardInput.WriteLine("-gdb-exit");
                _process.StandardInput.Flush();
            }
            catch
            {
            }
            if (!_process.WaitForExit(1000)) _process.Kill(entireProcessTree: true);
        }
        _process.Dispose();
        _lines.Dispose();
    }

    private Task WaitForPromptAsync()
    {
        while (true)
        {
            if (_lines.Take() == "(gdb)") return Task.CompletedTask;
        }
    }

    private async Task ReadOutputLoop()
    {
        var builder = new StringBuilder();
        var buffer = new char[1];
        while (await _process.StandardOutput.ReadAsync(buffer, 0, 1) == 1)
        {
            var ch = buffer[0];
            builder.Append(ch);
            if (ch == '\n')
            {
                _lines.Add(builder.ToString().TrimEnd('\r', '\n'));
                builder.Clear();
            }
            else if (builder.ToString().EndsWith("(gdb) ", StringComparison.Ordinal))
            {
                var text = builder.ToString();
                var prefix = text[..^6];
                if (!string.IsNullOrWhiteSpace(prefix)) _lines.Add(prefix.TrimEnd('\r', '\n'));
                _lines.Add("(gdb)");
                builder.Clear();
            }
        }
    }

    private async Task ReadErrorLoop()
    {
        string? line;
        while ((line = await _process.StandardError.ReadLineAsync()) is not null) _lines.Add($"stderr:{line}");
    }

    private static string? ExtractMiString(string text, string key)
    {
        var index = text.IndexOf(key, StringComparison.Ordinal);
        if (index < 0) return null;
        index += key.Length;
        if (index >= text.Length || text[index] != '"') return null;
        index++;
        var end = text.IndexOf('"', index);
        return end < 0 ? null : text[index..end];
    }
}

internal sealed class RrGdbDataReader : IDataReader, IDisposable
{
    private readonly GdbMiClient _gdb;
    private readonly Dictionary<(ulong Address, int Size), byte[]> _memoryCache = [];

    public RrGdbDataReader(GdbMiClient gdb, int processId)
    {
        _gdb = gdb;
        ProcessId = processId;
        Mappings = LoadMappingsAsync().GetAwaiter().GetResult();
        Modules = LoadModules();
    }

    public string DisplayName => "rr replay via gdb";
    public bool IsThreadSafe => false;
    public OSPlatform TargetPlatform => OSPlatform.Linux;
    public Architecture Architecture => Architecture.X64;
    public int ProcessId { get; }
    public int PointerSize => 8;
    public IReadOnlyList<ModuleInfo> Modules { get; }
    public IReadOnlyList<Mapping> Mappings { get; }

    public IEnumerable<ModuleInfo> EnumerateModules() => Modules;
    public bool GetThreadContext(uint threadID, uint contextFlags, Span<byte> context) => false;
    public void FlushCachedData() => _memoryCache.Clear();

    public int Read(ulong address, Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        try
        {
            var key = (address, buffer.Length);
            if (!_memoryCache.TryGetValue(key, out var data))
            {
                data = _gdb.ReadMemoryAsync(address, buffer.Length).GetAwaiter().GetResult();
                _memoryCache[key] = data;
            }
            var count = Math.Min(buffer.Length, data.Length);
            data.AsSpan(0, count).CopyTo(buffer);
            return count;
        }
        catch
        {
            return 0;
        }
    }

    public bool Read<T>(ulong address, out T value) where T : unmanaged
    {
        Span<byte> buffer = stackalloc byte[Marshal.SizeOf<T>()];
        if (Read(address, buffer) != buffer.Length)
        {
            value = default;
            return false;
        }
        value = MemoryMarshal.Read<T>(buffer);
        return true;
    }

    public T Read<T>(ulong address) where T : unmanaged
        => Read<T>(address, out var value) ? value : throw new InvalidOperationException($"Could not read {typeof(T).Name} at 0x{address:x}");

    public bool ReadPointer(ulong address, out ulong value) => Read(address, out value);
    public ulong ReadPointer(ulong address) => Read<ulong>(address);
    public void Dispose() { }

    private IReadOnlyList<ModuleInfo> LoadModules()
    {
        var modules = new List<ModuleInfo>();
        foreach (var group in Mappings.Where(m => !string.IsNullOrEmpty(m.FileName) && m.FileName.StartsWith("/", StringComparison.Ordinal)).GroupBy(m => m.FileName))
        {
            var ordered = group.OrderBy(m => m.Start).ToArray();
            var first = ordered.FirstOrDefault(m => m.Offset == 0);
            if (first == default) first = ordered[0];
            var module = ModuleInfo.TryCreate(this, first.Start, first.FileName);
            if (module is not null) modules.Add(module);
        }
        return modules.OrderBy(m => m.ImageBase).ToArray();
    }

    private async Task<IReadOnlyList<Mapping>> LoadMappingsAsync()
    {
        var text = await _gdb.ConsoleCommandAsync("info proc mappings");
        var console = DecodeMiConsoleOutput(text);
        var mappings = new List<Mapping>();
        foreach (var line in console.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !parts[0].StartsWith("0x", StringComparison.Ordinal)) continue;
            mappings.Add(new Mapping(ParseHex(parts[0]), ParseHex(parts[1]), ParseHex(parts[3]), parts[4], parts.Length > 5 ? string.Join(' ', parts.Skip(5)) : ""));
        }
        return mappings;
    }

    private static ulong ParseHex(string value) => ulong.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static string DecodeMiConsoleOutput(string mi)
    {
        var builder = new StringBuilder();
        foreach (var line in mi.Split('\n'))
        {
            if (!line.StartsWith("~\"", StringComparison.Ordinal)) continue;
            builder.Append(DecodeCString(line[2..^1]));
        }
        return builder.ToString();
    }

    private static string DecodeCString(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                builder.Append(value[i]);
                continue;
            }
            var next = value[++i];
            builder.Append(next switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '\\' => '\\',
                '"' => '"',
                _ => next,
            });
        }
        return builder.ToString();
    }
}

internal readonly record struct Mapping(ulong Start, ulong End, ulong Offset, string Perms, string FileName)
{
    public ulong Size => End - Start;
}

internal sealed class StreamDuplexPipe(Stream input, Stream output) : IDuplexPipe
{
    public PipeReader Input { get; } = PipeReader.Create(input);
    public PipeWriter Output { get; } = PipeWriter.Create(output);
}

internal static class DebugClientExtensions
{
    public static Task TerminatedDebuggerAsync(this IDebugClient client)
        => client.DebuggerTerminatedAsync(new TerminatedEvent());
}
