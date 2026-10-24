// Real watchdog thread and isolated self-termination; no input delegation, hooks or foreground changes.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <string>
#include <atomic>
#include <fstream>
#include <iterator>
static std::atomic<bool> emergencyChord{};
static SHORT WINAPI ReplayAsync(int key) {
    return emergencyChord.load()&&(key==VK_CONTROL||key==VK_MENU||key==VK_SHIFT||key==VK_F12)?-32768:0;
}
#define GetAsyncKeyState ReplayAsync
#include "../../src/FloatCanvas.SystemOverlay.Native/SystemOverlay.cpp"
#undef GetAsyncKeyState

static constexpr UINT BusyMessage=WM_APP+0x600;
static unsigned long long busyMessages{};
static LRESULT CALLBACK BusyProcedure(HWND window,UINT message,WPARAM wparam,LPARAM lparam) {
    if(message==BusyMessage) {++busyMessages;PostMessageW(window,BusyMessage,0,0);return 0;}
    return Procedure(window,message,wparam,lparam);
}

int wmain(int argc,wchar_t** argv) {
    if(argc>1) {
        Host owner;auto& guard=owner.safety;
        bool unprotected=wcscmp(argv[1],L"--unprotected")==0;
        if(unprotected&&!guard.Configure(false))return 8;
        emergencyChord=wcscmp(argv[1],L"--emergency")==0;
        bool busy=wcscmp(argv[1],L"--busy-message-pump")==0;
        bool pump=busy||wcscmp(argv[1],L"--message-pump")==0;
        if(pump||wcscmp(argv[1],L"--stalled-window")==0) {
            WNDCLASSW wc{};wc.lpfnWndProc=busy?BusyProcedure:Procedure;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"FloatCanvas.Watchdog.Test";
            RegisterClassW(&wc);
            owner.window=CreateWindowExW(WS_EX_NOACTIVATE,wc.lpszClassName,L"",WS_POPUP,-32000,-32000,1,1,nullptr,nullptr,wc.hInstance,&owner);
            if(!owner.window||!SetTimer(owner.window,SafetyTimer,100,nullptr))return 6;
        }
        if(!guard.Start(owner.window))return 2;
        if(argc>2&&!guard.SetJournal(argv[2]))return 5;
        guard.Arm();
        if(busy)PostMessageW(owner.window,BusyMessage,0,0);
        if(wcscmp(argv[1],L"--responsive")==0||pump||unprotected) {
            auto until=GetTickCount64()+5500;
            while(GetTickCount64()<until) {
                if(pump){MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){DispatchMessageW(&message);if(GetTickCount64()>=until)break;}}
                else if(!unprotected)guard.Heartbeat();
                Sleep(10);
            }
            guard.Disarm();if(owner.window)DestroyWindow(owner.window);
            if(busy)std::printf("BUSY_UI handled_messages=%llu\n",busyMessages);
            return 0;
        }
        if(wcscmp(argv[1],L"--release-failed")==0)guard.ReleaseFailed();
        Sleep(INFINITE);return 3;
    }
    bool passed=true;
    InputSafetyWatchdog guard;
    auto now=InputSafetyWatchdog::Now();
    passed=passed&&!guard.Expired(now+6000);
    guard.Arm();passed=passed&&!guard.Expired(InputSafetyWatchdog::Now()+4000)&&guard.Expired(InputSafetyWatchdog::Now()+5001);
    guard.Disarm();passed=passed&&!guard.Expired(InputSafetyWatchdog::Now()+6000);
    std::printf("UNARMED_AND_RELEASED_NEVER_TERMINATE passed=%s\n",passed?"true":"false");
    InputSafetyWatchdog forced;
    bool disabled=forced.Configure(false)&&forced.Start(nullptr)&&!forced.IsEnabled();forced.Arm();
    disabled=disabled&&!forced.Expired(InputSafetyWatchdog::Now()+60000);
    std::printf("FORCED_MODE_NEVER_ARMS_TIMEOUT_PROTECTION passed=%s\n",disabled?"true":"false");passed=passed&&disabled;
    wchar_t executable[MAX_PATH]{};GetModuleFileNameW(nullptr,executable,MAX_PATH);
    for(const auto* mode:{L"--stalled",L"--stalled-window",L"--release-failed",L"--emergency",L"--responsive",L"--message-pump",L"--busy-message-pump",L"--unprotected"}) {
        wchar_t temporary[MAX_PATH]{},journal[MAX_PATH]{};GetTempPathW(MAX_PATH,temporary);GetTempFileNameW(temporary,L"FCW",0,journal);
        std::wstring command=L"\""+std::wstring(executable)+L"\" "+mode+L" \""+journal+L"\"";
        STARTUPINFOW startup{sizeof(startup)};PROCESS_INFORMATION process{};
        auto started=GetTickCount64();
        if(!CreateProcessW(executable,command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,nullptr,&startup,&process))return 4;
        bool completed=WaitForSingleObject(process.hProcess,7000)==WAIT_OBJECT_0;
        DWORD code{};GetExitCodeProcess(process.hProcess,&code);
        if(!completed)TerminateProcess(process.hProcess,99);
        auto elapsed=GetTickCount64()-started;
        bool responsive=wcscmp(mode,L"--responsive")==0||wcscmp(mode,L"--message-pump")==0||wcscmp(mode,L"--busy-message-pump")==0||wcscmp(mode,L"--unprotected")==0;
        bool okay=completed&&code==(responsive?0:InputSafetyWatchdog::ExitCode)&&elapsed<6500;
        if(wcscmp(mode,L"--release-failed")==0||wcscmp(mode,L"--emergency")==0)okay=okay&&elapsed<2000;
        if(!responsive) {
            std::ifstream file(journal);std::string record((std::istreambuf_iterator<char>(file)),{});
            okay=okay&&record.find("\"processId\":")!=std::string::npos&&record.find("\"heartbeatAgeMs\":")!=std::string::npos&&record.find("\"probePosts\":")!=std::string::npos;
        }
        std::printf("WATCHDOG %ls bounded=%s exit=%lu elapsed_ms=%llu\n",mode,okay?"true":"false",code,elapsed);
        passed=passed&&okay;CloseHandle(process.hThread);CloseHandle(process.hProcess);DeleteFileW(journal);
    }
    return passed?0:1;
}
