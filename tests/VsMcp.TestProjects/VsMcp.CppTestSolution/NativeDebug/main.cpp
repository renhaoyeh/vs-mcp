#include <Windows.h>
__declspec(noinline) int Run() {
    int counter = 41;
    ++counter;
    Sleep(30000);
    return counter;
}

static DWORD WINAPI Worker(void* ready) {
    SetEvent(static_cast<HANDLE>(ready));
    Sleep(30000);
    return 0;
}
int main() {
    HANDLE ready = CreateEvent(nullptr, TRUE, FALSE, nullptr);
    HANDLE worker = CreateThread(nullptr, 0, Worker, ready, 0, nullptr);
    WaitForSingleObject(ready, INFINITE);
    volatile int callerValue = 17;
    int result = Run();
    CloseHandle(worker);
    CloseHandle(ready);
    return result + callerValue;
}
