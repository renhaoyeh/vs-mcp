#include <windows.h>
#include <cstdio>
#include <string>
#include <vector>

// Launcher contract v1: UTF-8 current.txt contains one safe directory name under versions/.
// Keep this small, runtime-independent entry point stable across Bridge releases.
static int Fail(const wchar_t* message)
{
    fwprintf(stderr, L"VS MCP: %ls (Windows error %lu). Run Bridge Setup to repair the installation.\n", message, GetLastError());
    return 1;
}

int wmain(int argc, wchar_t**)
{
    if (argc != 1) { fputs("Usage: VsMcp.Launcher (MCP over stdin/stdout)\n", stderr); return 1; }
    std::vector<wchar_t> module(32768);
    const DWORD length = GetModuleFileNameW(nullptr, module.data(), static_cast<DWORD>(module.size()));
    if (!length || length >= module.size()) return Fail(L"Cannot resolve launcher path");
    const std::wstring path(module.data(), length);
    const auto root = path.substr(0, path.find_last_of(L"\\/") + 1);
    HANDLE pointer = CreateFileW((root + L"current.txt").c_str(), GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (pointer == INVALID_HANDLE_VALUE) return Fail(L"No installed Bridge version");
    char buffer[256]{};
    DWORD read = 0;
    const bool ok = ReadFile(pointer, buffer, sizeof(buffer), &read, nullptr) != FALSE;
    CloseHandle(pointer);
    if (!ok || read == 0 || read >= sizeof(buffer)) return Fail(L"Invalid Bridge version pointer");
    while (read && (buffer[read - 1] == '\n' || buffer[read - 1] == '\r')) --read;
    std::wstring version;
    for (DWORD i = 0; i < read; ++i)
    {
        const char c = buffer[i];
        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || c == '.' || c == '-'))
            return Fail(L"Unsafe Bridge version pointer");
        version += static_cast<wchar_t>(c);
    }
    if (version.empty() || version.front() == L'.' || version.find(L"..") != std::wstring::npos)
        return Fail(L"Unsafe Bridge version pointer");
    const auto directory = root + L"versions\\" + version;
    const auto executable = directory + L"\\VsMcp.Bridge.exe";
    std::wstring command = L"\"" + executable + L"\"";

    // A child shares the MCP pipes directly: no text decoding, buffering or protocol proxy.
    // Restrict inheritance to stdio, and kill only our child if this launcher is terminated.
    HANDLE handles[3]{};
    for (int i = 0; i < 3; ++i)
    {
        const DWORD ids[] = { STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE };
        if (!DuplicateHandle(GetCurrentProcess(), GetStdHandle(ids[i]), GetCurrentProcess(),
            &handles[i], 0, TRUE, DUPLICATE_SAME_ACCESS))
        {
            for (const auto handle : handles) if (handle) CloseHandle(handle);
            return Fail(L"Cannot inherit MCP streams");
        }
    }
    SIZE_T bytes = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
    std::vector<unsigned char> storage(bytes);
    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = handles[0];
    startup.StartupInfo.hStdOutput = handles[1];
    startup.StartupInfo.hStdError = handles[2];
    startup.lpAttributeList = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
    const bool initialized = InitializeProcThreadAttributeList(startup.lpAttributeList, 1, 0, &bytes) != FALSE;
    const bool attributes = initialized && UpdateProcThreadAttribute(startup.lpAttributeList, 0,
        PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, sizeof(handles), nullptr, nullptr);
    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    const bool configured = job && SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits));
    PROCESS_INFORMATION child{};
    const bool started = attributes && configured && CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE,
        EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | CREATE_NO_WINDOW, nullptr, directory.c_str(), &startup.StartupInfo, &child);
    if (initialized) DeleteProcThreadAttributeList(startup.lpAttributeList);
    for (const auto handle : handles) CloseHandle(handle);
    if (!started) { if (job) CloseHandle(job); return Fail(L"Cannot start installed Bridge"); }
    if (!AssignProcessToJobObject(job, child.hProcess) || ResumeThread(child.hThread) == static_cast<DWORD>(-1))
    {
        TerminateProcess(child.hProcess, 1);
        CloseHandle(child.hThread); CloseHandle(child.hProcess); CloseHandle(job);
        return Fail(L"Cannot manage Bridge lifetime");
    }
    CloseHandle(child.hThread);
    const DWORD waited = WaitForSingleObject(child.hProcess, INFINITE);
    DWORD exitCode = 1;
    if (waited == WAIT_OBJECT_0) GetExitCodeProcess(child.hProcess, &exitCode);
    CloseHandle(child.hProcess);
    CloseHandle(job);
    return static_cast<int>(exitCode);
}
