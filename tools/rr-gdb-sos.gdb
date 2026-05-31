set pagination off
set confirm off
set auto-load safe-path /
printf "=== loading SOS plugin ===\n"
sharedlibrary libcoreclr
sharedlibrary libclrjit
set environment SOS_ROOT /home/kuinox/.dotnet/tools/.store/dotnet-dump/9.0.661903/dotnet-dump/9.0.661903/tools/net8.0/any/linux-x64
plugin load /home/kuinox/.dotnet/tools/.store/dotnet-dump/9.0.661903/dotnet-dump/9.0.661903/tools/net8.0/any/linux-x64/libsosplugin.so
printf "=== SOS help ===\n"
soshelp
printf "=== CLR threads ===\n"
clrthreads
printf "=== managed stack ===\n"
clrstack
printf "=== heap stat excerpt ===\n"
dumpheap -stat
quit
