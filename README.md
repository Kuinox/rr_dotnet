# rr_dotnet

Small .NET 10 sample program for manual rr investigation.

## Build

```bash
dotnet build samples/RrSample/RrSample.csproj -c Debug
```

## Record with rr

Build first so the rr trace focuses on the sample execution instead of the SDK build.

```bash
cc -shared -fPIC -O2 -Wall -Wextra \
  -o tools/rr-glibc-guard-workaround.so \
  tools/rr-glibc-guard-workaround.c \
  -pthread -ldl

rr record \
  --env=DOTNET_PerfMapEnabled=1 \
  --env=LD_PRELOAD="$PWD/tools/rr-glibc-guard-workaround.so" \
  --print-trace-dir=1 \
  dotnet samples/RrSample/bin/Debug/net10.0/RrSample.dll manual-run
```

`DOTNET_PerfMapEnabled=1` keeps the runtime's normal ReadyToRun and tiered compilation behavior, while asking .NET to emit perf maps and jit dumps for managed/JIT symbolization during manual investigation.

The preload helper is a local compatibility workaround for rr 5.9 with glibc 2.43/Linux 7.0, where glibc uses `MADV_GUARD_INSTALL` for pthread stack guard pages and rr 5.9 aborts on that `madvise` advice.

## Replay

```bash
rr replay /path/to/trace
```

On Zen CPUs, rr may also require disabling the hardware SpecLockMap optimization. Without that system-level workaround, recording can complete but replay can fail with a tick mismatch.

## CLRMD over rr/gdb

The `tools/RrClrmdProbe` prototype treats an rr replay point like a dump-like data source. It starts `rr replay`, connects GDB/MI to rr's remote debug server, implements CLRMD's `IDataReader`, and lets CLRMD inspect the replayed .NET runtime through rr-backed memory reads.

```bash
dotnet run --project tools/RrClrmdProbe/RrClrmdProbe.csproj -- \
  /home/kuinox/.local/share/rr/dotnet-5 \
  5000
```

Current proof points:

- CLRMD finds `libcoreclr.so` in the replayed process.
- CLRMD locates the .NET 10 DAC, `libmscordaccore.so`.
- `ClrRuntime` can be created from rr/gdb-backed memory.
- The GC heap reports `CanWalkHeap = true`.
- Managed threads are enumerated.
- Managed metadata such as the `System.String` MethodTable can be resolved.

Current limitations:

- Stack walking depends on GDB register reads for the rr replay thread context.
- Managed local variables are not implemented yet. The DAP currently exposes frame metadata, not CLR local-variable values.
- Memory reads go through GDB/MI and are page-cached, but are still not optimized for broad heap scans.

## DAP adapter

`src/RrDotNet.Dap` is an initial DAP adapter using `Draco.Dap`. It exposes the rr replay point as a stopped debug session backed by the CLRMD-over-rr/gdb reader.

Build:

```bash
dotnet build src/RrDotNet.Dap/RrDotNet.Dap.csproj -c Debug
```

Launch arguments:

```json
{
  "type": "rr-dotnet",
  "request": "launch",
  "name": "rr .NET snapshot",
  "program": "dotnet",
  "args": [
    "src/RrDotNet.Dap/bin/Debug/net10.0/RrDotNet.Dap.dll"
  ],
  "trace": "/home/kuinox/.local/share/rr/dotnet-5",
  "event": "5000"
}
```

Current DAP support:

- `initialize`
- `launch` / `attach`
- `threads`
- `stackTrace` with best-effort managed frames from CLRMD
- `scopes`
- `variables` with trace/runtime/module summary and per-frame method/IP/SP metadata
- `setBreakpoints` returns unverified placeholders
- `stepIn`, `next`, and `stepOut` acknowledge without moving the rr event
- `terminate`

The adapter is currently a snapshot inspector, not an execution-control debugger. The next important steps are managed local-variable inspection and wiring rr event movement for continue/step/reverse-step.

## VS Code extension

The local debugger contribution lives in `vscode-extension/rr-dotnet`.

For this workstation it has also been installed into:

```text
/home/kuinox/.vscode/extensions/kuinox.rr-dotnet-0.0.1
```

If VS Code still says the `rr-dotnet` debug type is unsupported, refresh that install:

```bash
mkdir -p /home/kuinox/.vscode/extensions/kuinox.rr-dotnet-0.0.1
cp vscode-extension/rr-dotnet/package.json \
  /home/kuinox/.vscode/extensions/kuinox.rr-dotnet-0.0.1/package.json
```

Then reload VS Code and use a launch config like:

```json
{
  "name": "rr .NET snapshot",
  "type": "rr-dotnet",
  "request": "launch",
  "trace": "/home/kuinox/.local/share/rr/dotnet-5",
  "event": "5000"
}
```

Useful manual landmarks in the sample:

- `ComputeScore`
- `Fibonacci`
- `BuildChecksum`
- `AsyncCheckpoint`
- `ThrowKnownFailure`
- `BackgroundCounter`
