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
    private const int SnapshotVariablesReference = 1;
    private readonly Dictionary<int, IReadOnlyList<Variable>> _variables = [];
    private readonly Dictionary<int, int> _frameVariableReferences = [];
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
        var managedFrames = _session.GetStackFrames(args.ThreadId);
        DapStackFrame[] frames;
        if (managedFrames.Count > 0)
        {
            frames = managedFrames.Select((f, index) => new DapStackFrame
            {
                Id = unchecked(args.ThreadId * 1000 + index),
                Name = f.Name,
                Line = 1,
                Column = 1,
                InstructionPointerReference = $"0x{f.InstructionPointer:x16}",
                PresentationHint = DapStackFrame.StackPresentationHint.Normal,
            }).ToArray();
            foreach (var (frame, info) in frames.Zip(managedFrames))
            {
                _frameVariableReferences[frame.Id] = AddVariables(
                [
                    Scalar("method", info.Name, "string"),
                    Scalar("instructionPointer", $"0x{info.InstructionPointer:x16}", "address"),
                    Scalar("stackPointer", $"0x{info.StackPointer:x16}", "address"),
                    Scalar("locals", "not implemented yet: needs managed local-variable inspection on top of the CLRMD stack frame", "status"),
                ]);
            }
        }
        else
        {
            frames =
            [
                new DapStackFrame
                {
                    Id = args.ThreadId,
                    Name = thread is null ? "rr replay snapshot" : $"managed thread {thread.ManagedThreadId}",
                    Line = 1,
                    Column = 1,
                    PresentationHint = DapStackFrame.StackPresentationHint.Label,
                },
            ];
        }

        return Task.FromResult(new StackTraceResponse
        {
            StackFrames = frames,
            TotalFrames = frames.Length,
        });
    }

    public Task<ScopesResponse> GetScopesAsync(ScopesArguments args)
    {
        EnsureSession();
        var scopes = new List<Scope>();
        if (_frameVariableReferences.TryGetValue(args.FrameId, out var frameVariablesReference))
        {
            scopes.Add(new Scope
            {
                Name = "Frame",
                VariablesReference = frameVariablesReference,
                Expensive = false,
                PresentationHint = Scope.ScopePresentationHint.Locals,
            });
        }

        scopes.Add(new Scope
        {
            Name = "rr / CLRMD snapshot",
            VariablesReference = SnapshotVariablesReference,
            Expensive = false,
        });

        return Task.FromResult(new ScopesResponse
        {
            Scopes = scopes.ToArray(),
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
        _frameVariableReferences.Clear();
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

        _variables[SnapshotVariablesReference] = root;
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
        ManagedThreads = runtime?.Threads.Select((t, i) =>
            new ManagedThreadInfo(
                Id: t.OSThreadId == 0 ? i + 1 : checked((int)t.OSThreadId),
                ManagedThreadId: checked((ulong)t.ManagedThreadId),
                OSThreadId: t.OSThreadId,
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

    public IReadOnlyList<ManagedStackFrameInfo> GetStackFrames(int threadId)
    {
        var thread = _runtime?.Threads.FirstOrDefault(t => (t.OSThreadId == 0 ? checked((int)t.ManagedThreadId) : checked((int)t.OSThreadId)) == threadId);
        if (thread is null)
        {
            return [];
        }

        try
        {
            return thread.EnumerateStackTrace()
                .Take(64)
                .Select(f => new ManagedStackFrameInfo(
                    Name: f.ToString() ?? "<unknown managed frame>",
                    InstructionPointer: f.InstructionPointer,
                    StackPointer: f.StackPointer))
                .ToArray();
        }
        catch
        {
            return [];
        }
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

internal sealed record ManagedThreadInfo(int Id, ulong ManagedThreadId, uint OSThreadId, string Name);

internal sealed record ManagedStackFrameInfo(string Name, ulong InstructionPointer, ulong StackPointer);

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
    private readonly SemaphoreSlim _commandLock = new(1, 1);
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

    public async Task<string> RawCommandAsync(string command) => await CommandAsync(command);

    public async Task<byte[]> ReadMemoryAsync(ulong address, int count)
    {
        var result = await CommandAsync($"-data-read-memory-bytes 0x{address:x} {count}");
        var contents = ExtractMiString(result, "contents=");
        return contents is null ? [] : Convert.FromHexString(contents);
    }

    public async Task<string> CommandAsync(string command)
    {
        await _commandLock.WaitAsync();
        try
        {
            var token = Interlocked.Increment(ref _token);
            await _process.StandardInput.WriteLineAsync($"{token}{command}");
            await _process.StandardInput.FlushAsync();

            var builder = new StringBuilder();
            var timeout = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < timeout)
            {
                if (!TryTakeLine(timeout, out var line))
                {
                    continue;
                }

                builder.AppendLine(line);
                if (line.StartsWith($"{token}^done", StringComparison.Ordinal)
                    || line.StartsWith($"{token}^error", StringComparison.Ordinal)
                    || line.StartsWith($"{token}^connected", StringComparison.Ordinal))
                {
                    await WaitForPromptAsync();
                    return builder.ToString();
                }
            }

            throw new TimeoutException($"gdb/mi command timed out: {command}");
        }
        finally
        {
            _commandLock.Release();
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
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < timeout)
        {
            if (TryTakeLine(timeout, out var line) && line == "(gdb)") return Task.CompletedTask;
        }
        throw new TimeoutException("timed out waiting for gdb prompt");
    }

    private bool TryTakeLine(DateTime timeout, out string line)
    {
        var remaining = timeout - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            line = "";
            return false;
        }

        return _lines.TryTake(out line!, remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250));
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
    private const int PageSize = 4096;
    private readonly GdbMiClient _gdb;
    private readonly Dictionary<(ulong Address, int Size), byte[]> _memoryCache = [];
    private readonly Dictionary<uint, int> _gdbThreadsByOsThread = [];

    public RrGdbDataReader(GdbMiClient gdb, int processId)
    {
        _gdb = gdb;
        ProcessId = processId;
        Mappings = LoadMappingsAsync().GetAwaiter().GetResult();
        _gdbThreadsByOsThread = LoadThreadMapAsync().GetAwaiter().GetResult();
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
    public bool GetThreadContext(uint threadID, uint contextFlags, Span<byte> context)
    {
        if (context.Length < AMD64Context.Size || !_gdbThreadsByOsThread.TryGetValue(threadID, out var gdbThreadId))
        {
            return false;
        }

        try
        {
            _gdb.RawCommandAsync($"-thread-select {gdbThreadId}").GetAwaiter().GetResult();
            var registers = LoadRegistersAsync().GetAwaiter().GetResult();
            WriteAmd64Context(context, registers);
            return true;
        }
        catch
        {
            return false;
        }
    }
    public void FlushCachedData() => _memoryCache.Clear();

    public int Read(ulong address, Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        var total = 0;
        while (total < buffer.Length)
        {
            var current = address + checked((ulong)total);
            var pageStart = current & ~(ulong)(PageSize - 1);
            var pageOffset = checked((int)(current - pageStart));
            byte[] page;
            try
            {
                var key = (pageStart, PageSize);
                if (!_memoryCache.TryGetValue(key, out page!))
                {
                    page = _gdb.ReadMemoryAsync(pageStart, PageSize).GetAwaiter().GetResult();
                    _memoryCache[key] = page;
                }
            }
            catch
            {
                return total;
            }

            if (pageOffset >= page.Length)
            {
                return total;
            }

            var count = Math.Min(buffer.Length - total, page.Length - pageOffset);
            page.AsSpan(pageOffset, count).CopyTo(buffer[total..]);
            total += count;
        }

        return total;
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

    private async Task<Dictionary<uint, int>> LoadThreadMapAsync()
    {
        var result = await _gdb.RawCommandAsync("-thread-info");
        var map = new Dictionary<uint, int>();

        foreach (var thread in ExtractThreadObjects(result))
        {
            if (!TryReadMiInt(thread, "id", out var gdbId))
            {
                continue;
            }

            var targetId = TryReadMiString(thread, "target-id") ?? "";
            var match = System.Text.RegularExpressions.Regex.Match(targetId, @"Thread\s+\d+\.(\d+)");
            if (match.Success && uint.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var osThreadId))
            {
                map[osThreadId] = gdbId;
            }
        }

        return map;
    }

    private async Task<Dictionary<string, ulong>> LoadRegistersAsync()
    {
        var result = await _gdb.RawCommandAsync("-data-list-register-values x");
        var registers = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ExtractRegisterObjects(result))
        {
            if (!TryReadMiInt(item, "number", out var number))
            {
                continue;
            }

            var value = TryReadMiString(item, "value");
            if (value is null)
            {
                continue;
            }

            var name = RegisterName(number);
            if (name is not null && TryParseRegisterValue(value, out var parsed))
            {
                registers[name] = parsed;
            }
        }

        return registers;
    }

    private static void WriteAmd64Context(Span<byte> context, IReadOnlyDictionary<string, ulong> registers)
    {
        context.Clear();
        ref var amd64 = ref MemoryMarshal.AsRef<AMD64Context>(context);
        amd64.ContextFlags = AMD64Context.ContextControl | AMD64Context.ContextInteger | AMD64Context.ContextSegments;
        amd64.Rax = Get(registers, "rax");
        amd64.Rcx = Get(registers, "rcx");
        amd64.Rdx = Get(registers, "rdx");
        amd64.Rbx = Get(registers, "rbx");
        amd64.Rsp = Get(registers, "rsp");
        amd64.Rbp = Get(registers, "rbp");
        amd64.Rsi = Get(registers, "rsi");
        amd64.Rdi = Get(registers, "rdi");
        amd64.R8 = Get(registers, "r8");
        amd64.R9 = Get(registers, "r9");
        amd64.R10 = Get(registers, "r10");
        amd64.R11 = Get(registers, "r11");
        amd64.R12 = Get(registers, "r12");
        amd64.R13 = Get(registers, "r13");
        amd64.R14 = Get(registers, "r14");
        amd64.R15 = Get(registers, "r15");
        amd64.Rip = Get(registers, "rip");
        amd64.EFlags = unchecked((int)Get(registers, "eflags"));
        amd64.Cs = checked((ushort)Get(registers, "cs"));
        amd64.Ss = checked((ushort)Get(registers, "ss"));
        amd64.Ds = checked((ushort)Get(registers, "ds"));
        amd64.Es = checked((ushort)Get(registers, "es"));
        amd64.Fs = checked((ushort)Get(registers, "fs"));
        amd64.Gs = checked((ushort)Get(registers, "gs"));

        static ulong Get(IReadOnlyDictionary<string, ulong> values, string name)
            => values.TryGetValue(name, out var value) ? value : 0;
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

    private static IEnumerable<string> ExtractThreadObjects(string mi)
    {
        var marker = "threads=[";
        var start = mi.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            yield break;
        }

        start += marker.Length;
        var end = FindMatching(mi, start - 1, '[', ']');
        if (end < 0)
        {
            yield break;
        }

        foreach (var item in ExtractTopLevelObjects(mi[start..end]))
        {
            yield return item;
        }
    }

    private static IEnumerable<string> ExtractRegisterObjects(string mi)
    {
        var marker = "register-values=[";
        var start = mi.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            yield break;
        }

        start += marker.Length;
        var end = FindMatching(mi, start - 1, '[', ']');
        if (end < 0)
        {
            yield break;
        }

        foreach (var item in ExtractTopLevelObjects(mi[start..end]))
        {
            yield return item;
        }
    }

    private static IEnumerable<string> ExtractTopLevelObjects(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{')
            {
                continue;
            }

            var end = FindMatching(text, i, '{', '}');
            if (end < 0)
            {
                yield break;
            }

            yield return text[i..(end + 1)];
            i = end;
        }
    }

    private static int FindMatching(string text, int openIndex, char open, char close)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = openIndex; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == open)
            {
                depth++;
            }
            else if (ch == close && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryReadMiInt(string obj, string key, out int value)
    {
        var text = TryReadMiString(obj, key);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string? TryReadMiString(string obj, string key)
    {
        var marker = $"{key}=\"";
        var start = obj.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var builder = new StringBuilder();
        var escaped = false;
        for (var i = start; i < obj.Length; i++)
        {
            var ch = obj[i];
            if (escaped)
            {
                builder.Append(ch);
                escaped = false;
            }
            else if (ch == '\\')
            {
                escaped = true;
            }
            else if (ch == '"')
            {
                return builder.ToString();
            }
            else
            {
                builder.Append(ch);
            }
        }

        return null;
    }

    private static bool TryParseRegisterValue(string value, out ulong parsed)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);
        }

        return ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }

    private static string? RegisterName(int number) => number switch
    {
        0 => "rax",
        1 => "rbx",
        2 => "rcx",
        3 => "rdx",
        4 => "rsi",
        5 => "rdi",
        6 => "rbp",
        7 => "rsp",
        8 => "r8",
        9 => "r9",
        10 => "r10",
        11 => "r11",
        12 => "r12",
        13 => "r13",
        14 => "r14",
        15 => "r15",
        16 => "rip",
        17 => "eflags",
        18 => "cs",
        19 => "ss",
        20 => "ds",
        21 => "es",
        22 => "fs",
        23 => "gs",
        _ => null,
    };
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
