#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <filesystem>
#include <string>
#include <vector>

namespace fs = std::filesystem;

static bool Exists(const fs::path& path) { return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES; }
static void Fail(const wchar_t* message) { MessageBoxW(nullptr, message, L"FloatCanvas", MB_OK | MB_ICONERROR); }
static bool Start(const fs::path& executable, const std::wstring& arguments, DWORD timeout, DWORD* exitCode = nullptr, bool useExecutableDirectory = false)
{
    std::wstring command = L"\"" + executable.wstring() + L"\" " + arguments;
    STARTUPINFOW startup{ sizeof(startup) }; PROCESS_INFORMATION process{};
    auto directory = executable.parent_path().wstring();
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, useExecutableDirectory ? directory.c_str() : nullptr, &startup, &process)) return false;
    CloseHandle(process.hThread);
    // Recovery is idempotent and owns an OS file lease. Never kill an updater on a timeout.
    bool result = timeout == 0 || WaitForSingleObject(process.hProcess, timeout) == WAIT_OBJECT_0;
    if (result && exitCode) GetExitCodeProcess(process.hProcess, exitCode);
    CloseHandle(process.hProcess); return result;
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR arguments, int)
{
    std::vector<wchar_t> module(32768);
    DWORD count = GetModuleFileNameW(nullptr, module.data(), static_cast<DWORD>(module.size()));
    if (!count || count >= module.size()) return 2;
    fs::path directory = fs::path(module.data()).parent_path();
    std::wstring launchArguments(arguments);
    const std::wstring recoveryPrefix = L"--recovery-root \"";
    bool recoveryCopy = launchArguments.starts_with(recoveryPrefix);
    if (recoveryCopy)
    {
        auto end = launchArguments.find(L'\"', recoveryPrefix.size());
        if (end == std::wstring::npos) return 2;
        directory = launchArguments.substr(recoveryPrefix.size(), end - recoveryPrefix.size());
        launchArguments = end + 1 < launchArguments.size() ? launchArguments.substr(end + 2) : L"";
        if (!directory.is_absolute()) return 2;
    }
    const auto marker = directory / L".floatcanvas-update";
    if (Exists(marker))
    {
        HANDLE file = CreateFileW(marker.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) { Fail(L"An update is being recovered. Please wait and start FloatCanvas again."); return 2; }
        LARGE_INTEGER size{}; DWORD read = 0;
        bool valid = GetFileSizeEx(file, &size) && size.QuadPart > 0 && size.QuadPart <= 32768;
        std::string bytes(valid ? static_cast<size_t>(size.QuadPart) : 0, '\0');
        valid = valid && ReadFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr) && read == bytes.size();
        CloseHandle(file);
        int length = valid ? MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes.data(), static_cast<int>(bytes.size()), nullptr, 0) : 0;
        std::wstring plan(static_cast<size_t>(length), L'\0');
        if (length) MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes.data(), static_cast<int>(bytes.size()), plan.data(), length);
        const fs::path planPath(plan);
        valid = length > 0 && planPath.is_absolute() && planPath.filename() == L"plan.json" &&
            plan.find_first_of(L"\"\r\n") == std::wstring::npos && plan.find(L'\0') == std::wstring::npos &&
            planPath.parent_path().parent_path().filename() == L"jobs";
        if (!valid) { Fail(L"The update recovery record is invalid. Keep the update backup and reinstall a complete official package."); return 2; }
        // The installed executable cannot replace itself while mapped. A private copy
        // waits for recovery; the installed launcher exits before the worker mutates files.
        if (!recoveryCopy)
        {
            auto copy = planPath.parent_path() / L"worker" / (L"recovery-launcher-" + std::to_wstring(GetCurrentProcessId()) + L".exe");
            if (!CopyFileW(module.data(), copy.c_str(), TRUE) || !Start(copy, recoveryPrefix + directory.wstring() + L"\" " + launchArguments, 0))
            { Fail(L"The recovery launcher could not start. Keep the update backup and try again."); return 2; }
            return 0;
        }
        auto worker = planPath.parent_path() / L"worker" / L"FloatCanvas.Host.exe";
        if (!Exists(worker)) worker = planPath.parent_path() / L"worker" / L"FloatCanvas.exe";
        if (!Start(worker, L"--apply-update \"" + plan + L"\"", 120000, nullptr, true) || Exists(marker))
        { Fail(L"The interrupted update could not yet be recovered. Keep the backup in the update job directory. No incomplete application was started."); return 2; }
    }
    DWORD exitCode = 2;
    auto host = directory / L"FloatCanvas.Host.exe";
    // An interrupted first upgrade can restore a package predating this launcher.
    if (recoveryCopy && !Exists(host)) host = directory / L"FloatCanvas.exe";
    if (!Start(host, launchArguments, INFINITE, &exitCode))
    { Fail(L"FloatCanvas.Host.exe could not start. Extract the complete portable package before launching FloatCanvas."); return 2; }
    return static_cast<int>(exitCode);
}
