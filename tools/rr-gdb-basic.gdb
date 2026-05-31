set pagination off
set confirm off
printf "=== inferior ===\n"
info inferiors
printf "=== shared libraries containing coreclr/sos/jit ===\n"
info sharedlibrary libcoreclr
info sharedlibrary libclrjit
info sharedlibrary libsos
printf "=== threads ===\n"
info threads
printf "=== native backtrace current thread ===\n"
bt 20
quit
