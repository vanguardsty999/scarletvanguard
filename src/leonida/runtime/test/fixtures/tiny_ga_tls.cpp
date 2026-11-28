// Tiny fixture EXE for the /GA TLS path. It is built with /GA, so its __declspec(thread) reads go through
// TEB->ThreadLocalStoragePointer[0] directly, as the retail executable's do. It is never executed; rt::Loader maps
// it and the test calls its exports from threads it chooses.
#include <windows.h>

__declspec(thread) int g_ga_tls_value = 7;

extern "C" __declspec(dllexport) int ga_tls_get() { return g_ga_tls_value; }
extern "C" __declspec(dllexport) void ga_tls_set(int value) { g_ga_tls_value = value; }
// Where this code looks for the variable on the calling thread: ThreadLocalStoragePointer[0] plus the variable's
// offset, with no read of the variable itself. The test uses it to tell whose block index 0 resolved to.
extern "C" __declspec(dllexport) void* ga_tls_address() { return &g_ga_tls_value; }

int main() {
    return 0;
}
