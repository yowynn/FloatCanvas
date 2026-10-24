// Real watchdog thread and isolated self-termination; no input delegation, hooks or foreground changes.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <string>
#include <atomic>
static std::atomic<bool> emergencyChord{};
static SHORT WINAPI ReplayAsync(int key) {
    return emergencyChord.load()&&(key==VK_CONTROL||key==VK_MENU||key==VK_SHIFT||key==VK_F12)?-32768:0;
}
#define GetAsyncKeyState ReplayAsync
#include "../../src/FloatCanvas.SystemOverlay.Native/SystemOverlay.cpp"
#undef GetAsyncKeyState

int wmain(int argc,wchar_t** argv) {
    if(argc>1) {
        Host owner;auto& guard=owner.safety;
        emergencyChord=wcscmp(argv[1],L"--emergency")==0;
        bool pump=wcscmp(argv[1],L"--message-pump")==0;
        if(pump) {
            WNDCLASSW wc{};wc.lpfnWndProc=Procedure;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"FloatCanvas.Watchdog.Test";
            RegisterClassW(&wc);
            owner.window=CreateWindowExW(WS_EX_NOACTIVATE,wc.lpszClassName,L"",WS_POPUP,-32000,-32000,1,1,nullptr,nullptr,wc.hInstance,&owner);
            if(!owner.window||!SetTimer(owner.window,SafetyTimer,100,nullptr))return 6;
        }
        if(!guard.Start(owner.window))return 2;
        if(argc>2&&!guard.SetJournal(argv[2]))return 5;
        guard.Arm();
        if(wcscmp(argv[1],L"--responsive")==0||pump) {
            auto until=GetTickCount64()+5500;
            while(GetTickCount64()<until) {
                if(pump){MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE))DispatchMessageW(&message);}
                else guard.Heartbeat();
                Sleep(10);
            }
            guard.Disarm();if(owner.window)DestroyWindow(owner.window);return 0;
        }
        if(wcscmp(argv[1],L"--release-failed")==0)guard.ReleaseFailed();
        Sleep(INFINITE);return 3;
    }
    bool passed=true;
    InputSafetyWatchdog guard;
    auto now=GetTickCount64();
    passed=passed&&!guard.Expired(now+6000);
    guard.Arm();passed=passed&&!guard.Expired(GetTickCount64()+4000)&&guard.Expired(GetTickCount64()+5001);
    guard.Disarm();passed=passed&&!guard.Expired(GetTickCount64()+6000);
    std::printf("UNARMED_AND_RELEASED_NEVER_TERMINATE passed=%s\n",passed?"true":"false");
    wchar_t executable[MAX_PATH]{};GetModuleFileNameW(nullptr,executable,MAX_PATH);
    for(const auto* mode:{L"--stalled",L"--release-failed",L"--emergency",L"--responsive",L"--message-pump"}) {
        std::wstring command=L"\""+std::wstring(executable)+L"\" "+mode;
        STARTUPINFOW startup{sizeof(startup)};PROCESS_INFORMATION process{};
        auto started=GetTickCount64();
        if(!CreateProcessW(executable,command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,nullptr,&startup,&process))return 4;
        bool completed=WaitForSingleObject(process.hProcess,7000)==WAIT_OBJECT_0;
        DWORD code{};GetExitCodeProcess(process.hProcess,&code);
        if(!completed)TerminateProcess(process.hProcess,99);
        auto elapsed=GetTickCount64()-started;
        bool responsive=wcscmp(mode,L"--responsive")==0||wcscmp(mode,L"--message-pump")==0;
        bool okay=completed&&code==(responsive?0:InputSafetyWatchdog::ExitCode)&&elapsed<6500;
        if(wcscmp(mode,L"--release-failed")==0||wcscmp(mode,L"--emergency")==0)okay=okay&&elapsed<2000;
        std::printf("WATCHDOG %ls bounded=%s exit=%lu elapsed_ms=%llu\n",mode,okay?"true":"false",code,elapsed);
        passed=passed&&okay;CloseHandle(process.hThread);CloseHandle(process.hProcess);
    }
    return passed?0:1;
}
