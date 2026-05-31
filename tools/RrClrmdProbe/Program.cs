using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Runtime;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: RrClrmdProbe <rr-trace-dir> [event]");
    return 2;
}

var traceDir = Path.GetFullPath(args[0]);
var replayEvent = args.Length > 1 ? args[1] : "5000";

using var rr = await RrReplaySession.StartAsync(traceDir, replayEvent);
using var gdb = await GdbMiClient.ConnectAsync(rr.ExecutablePath, rr.Port);
using var reader = new RrGdbDataReader(gdb, rr.RecordedProcessId);
using var target = new DataTarget(reader, new DataTargetOptions());

Console.WriteLine($"trace: {traceDir}");
Console.WriteLine($"event: {replayEvent}");
Console.WriteLine($"rr gdb port: {rr.Port}");
Console.WriteLine($"recorded pid: {rr.RecordedProcessId}");

var modules = reader.Modules.ToArray();
Console.WriteLine($"modules: {modules.Length}");
foreach (var module in modules.Where(m => IsInterestingModule(m.FileName)).Take(20))
{
    Console.WriteLine($"  0x{module.ImageBase:x16} {module.FileName}");
}

Console.WriteLine($"clr versions: {target.ClrVersions.Length}");
foreach (var clr in target.ClrVersions)
{
    Console.WriteLine($"  module: {clr.ModuleInfo.FileName} @ 0x{clr.ModuleInfo.ImageBase:x16}");
    foreach (var library in clr.DebuggingLibraries)
    {
        Console.WriteLine($"  debug library: {library.FileName}");
    }
}

foreach (var clr in target.ClrVersions.Take(1))
{
    try
    {
        var dacPath = clr.DebuggingLibraries
            .Select(l => l.FileName)
            .FirstOrDefault(File.Exists)
            ?? "/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.8/libmscordaccore.so";

        var runtime = clr.CreateRuntime(dacPath, ignoreMismatch: true);
        Console.WriteLine($"runtime heap can walk heap: {runtime.Heap.CanWalkHeap}");
        Console.WriteLine($"managed threads: {runtime.Threads.Length}");

        foreach (var thread in runtime.Threads.Take(8))
        {
            Console.WriteLine($"  managed thread os=0x{thread.OSThreadId:x} managed=0x{thread.ManagedThreadId:x} alive={thread.IsAlive}");
        }

        var stringType = runtime.Heap.GetTypeByName("System.String");
        if (stringType is not null)
        {
            Console.WriteLine($"System.String MT: 0x{stringType.MethodTable:x16}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"runtime inspection failed: {ex.GetType().Name}: {ex.Message}");
    }
}

return 0;

static bool IsInterestingModule(string fileName)
{
    return fileName.Contains("RrSample", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("libcoreclr", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("libclrjit", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase);
}

sealed class RrReplaySession : IDisposable
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
            string? stdout;
            while ((stdout = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                lines.Add(stdout);
            }
        });
        _ = Task.Run(async () =>
        {
            string? stderr;
            while ((stderr = await process.StandardError.ReadLineAsync()) is not null)
            {
                lines.Add(stderr);
            }
        });

        var output = new StringBuilder();
        var timeout = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < timeout)
        {
            if (!lines.TryTake(out var line, TimeSpan.FromMilliseconds(250)))
            {
                if (process.HasExited)
                {
                    break;
                }

                continue;
            }

            output.AppendLine(line);
            Console.WriteLine($"rr: {line}");

            if (output.ToString().Contains("127.0.0.1:", StringComparison.Ordinal))
            {
                break;
            }
        }

        var text = output.ToString();
        var port = ParsePort(text);
        var executable = ParseQuotedAfter(text, "target extended-remote");
        if (executable is null)
        {
            executable = ParseLastQuotedArgument(text);
        }

        var pid = ParseRecordedPid(text);

        if (port == 0 || string.IsNullOrWhiteSpace(executable))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"failed to parse rr debugger launch text:{Environment.NewLine}{text}");
        }

        return new RrReplaySession(process, executable, port, pid);
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
    }

    private static int ParsePort(string text)
    {
        var marker = "127.0.0.1:";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return 0;
        }

        index += marker.Length;
        var end = index;
        while (end < text.Length && char.IsDigit(text[end]))
        {
            end++;
        }

        return int.TryParse(text[index..end], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 0;
    }

    private static int ParseRecordedPid(string text)
    {
        var marker = "Process id: ";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return 0;
        }

        index += marker.Length;
        var end = index;
        while (end < text.Length && char.IsDigit(text[end]))
        {
            end++;
        }

        return int.TryParse(text[index..end], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : 0;
    }

    private static string? ParseQuotedAfter(string text, string marker)
    {
        var markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        return ParseLastQuotedArgument(text[markerIndex..]);
    }

    private static string? ParseLastQuotedArgument(string text)
    {
        var parts = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var start = text.IndexOf('\'', index);
            if (start < 0)
            {
                break;
            }

            var end = text.IndexOf('\'', start + 1);
            if (end < 0)
            {
                break;
            }

            parts.Add(text[(start + 1)..end]);
            index = end + 1;
        }

        return parts.LastOrDefault(p => p.StartsWith("/", StringComparison.Ordinal));
    }
}

sealed class GdbMiClient : IDisposable
{
    private readonly Process _process;
    private readonly BlockingCollection<string> _lines = new();
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
        return contents is null ? Array.Empty<byte>() : Convert.FromHexString(contents);
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
                || line.StartsWith($"{token}^connected", StringComparison.Ordinal)
                || line.StartsWith($"{token}^running", StringComparison.Ordinal))
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
                // Best effort shutdown.
            }

            if (!_process.WaitForExit(1000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        _process.Dispose();
        _lines.Dispose();
    }

    private async Task WaitForPromptAsync()
    {
        while (true)
        {
            var line = _lines.Take();
            if (line == "(gdb)")
            {
                return;
            }
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
                continue;
            }

            if (builder.ToString().EndsWith("(gdb) ", StringComparison.Ordinal))
            {
                var text = builder.ToString();
                var prefix = text[..^6];
                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    _lines.Add(prefix.TrimEnd('\r', '\n'));
                }

                _lines.Add("(gdb)");
                builder.Clear();
            }
        }
    }

    private async Task ReadErrorLoop()
    {
        string? line;
        while ((line = await _process.StandardError.ReadLineAsync()) is not null)
        {
            _lines.Add($"stderr:{line}");
        }
    }

    private static string? ExtractMiString(string text, string key)
    {
        var index = text.IndexOf(key, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        index += key.Length;
        if (index >= text.Length || text[index] != '"')
        {
            return null;
        }

        index++;
        var end = text.IndexOf('"', index);
        return end < 0 ? null : text[index..end];
    }
}

sealed class RrGdbDataReader : IDataReader, IDisposable
{
    private readonly GdbMiClient _gdb;
    private readonly Dictionary<(ulong Address, int Size), byte[]> _memoryCache = new();

    public RrGdbDataReader(GdbMiClient gdb, int processId)
    {
        _gdb = gdb;
        ProcessId = processId;
        Modules = LoadModulesAsync().GetAwaiter().GetResult();
        Mappings = LoadMappingsAsync().GetAwaiter().GetResult();
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
        return false;
    }

    public void FlushCachedData()
    {
        _memoryCache.Clear();
    }

    public int Read(ulong address, Span<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

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
    {
        return Read<T>(address, out var value) ? value : throw new InvalidOperationException($"Could not read {typeof(T).Name} at 0x{address:x}");
    }

    public bool ReadPointer(ulong address, out ulong value)
    {
        return Read(address, out value);
    }

    public ulong ReadPointer(ulong address)
    {
        return Read<ulong>(address);
    }

    public IEnumerable<ulong> FindUtf16(string value)
    {
        var needle = Encoding.Unicode.GetBytes(value);
        foreach (var mapping in Mappings.Where(m => m.Perms.Contains('r') && m.Perms.Contains('w') && m.Size <= 1024 * 1024))
        {
            var bytes = _gdb.ReadMemoryAsync(mapping.Start, checked((int)mapping.Size)).GetAwaiter().GetResult();
            var index = bytes.AsSpan().IndexOf(needle);
            if (index >= 0)
            {
                yield return mapping.Start + (ulong)index;
            }
        }
    }

    public void Dispose()
    {
    }

    private async Task<IReadOnlyList<ModuleInfo>> LoadModulesAsync()
    {
        var mappings = await LoadMappingsAsync();
        var modules = new List<ModuleInfo>();

        foreach (var group in mappings.Where(m => !string.IsNullOrEmpty(m.FileName) && m.FileName.StartsWith("/", StringComparison.Ordinal)).GroupBy(m => m.FileName))
        {
            var ordered = group.OrderBy(m => m.Start).ToArray();
            var first = ordered.FirstOrDefault(m => m.Offset == 0);
            if (first == default)
            {
                first = ordered[0];
            }
            var module = ModuleInfo.TryCreate(this, first.Start, first.FileName);
            if (module is not null)
            {
                modules.Add(module);
            }
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
            if (parts.Length < 5 || !parts[0].StartsWith("0x", StringComparison.Ordinal))
            {
                continue;
            }

            var start = ParseHex(parts[0]);
            var end = ParseHex(parts[1]);
            var offset = ParseHex(parts[3]);
            var perms = parts[4];
            var fileName = parts.Length > 5 ? string.Join(' ', parts.Skip(5)) : "";
            mappings.Add(new Mapping(start, end, offset, perms, fileName));
        }

        return mappings;
    }

    private static ulong ParseHex(string value)
    {
        return ulong.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static string DecodeMiConsoleOutput(string mi)
    {
        var builder = new StringBuilder();
        foreach (var line in mi.Split('\n'))
        {
            if (!line.StartsWith("~\"", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[2..^1];
            builder.Append(DecodeCString(payload));
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
                _ => next
            });
        }

        return builder.ToString();
    }
}

readonly record struct Mapping(ulong Start, ulong End, ulong Offset, string Perms, string FileName)
{
    public ulong Size => End - Start;
}
