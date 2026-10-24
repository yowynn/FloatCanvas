// Real release-monitor thread; no input delegation, hooks or foreground changes.
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
static unsigned long long busyMessages{},emergencyRequests{};
static HRESULT STDMETHODCALLTYPE FailRelease(void*,UINT32,UINT32) {return E_FAIL;}
static LRESULT CALLBACK TestProcedure(HWND window,UINT message,WPARAM wparam,LPARAM lparam) {
    if(message==BusyMessage) {++busyMessages;PostMessageW(window,BusyMessage,0,0);return 0;}
    if(message==InputSafetyWatchdog::EmergencyMessage)++emergencyRequests;
    return Procedure(window,message,wparam,lparam);
}

int wmain(int argc,wchar_t** argv) {
    if(argc>1) {
        Host owner;auto& guard=owner.safety;
        bool unprotected=wcscmp(argv[1],L"--unprotected")==0;
        if(unprotected&&!guard.Configure(false))return 8;
        bool emergency=wcscmp(argv[1],L"--emergency")==0;
        emergencyChord=emergency;
        bool busy=wcscmp(argv[1],L"--busy-message-pump")==0;
        bool pump=busy||wcscmp(argv[1],L"--message-pump")==0;
        if(pump||emergency||wcscmp(argv[1],L"--stalled-window")==0) {
            WNDCLASSW wc{};wc.lpfnWndProc=TestProcedure;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"FloatCanvas.ReleaseMonitor.Test";
            RegisterClassW(&wc);
            owner.window=CreateWindowExW(WS_EX_NOACTIVATE,wc.lpszClassName,L"",WS_POPUP,-32000,-32000,1,1,nullptr,nullptr,wc.hInstance,&owner);
            if(!owner.window)return 6;
        }
        if(!guard.Start(owner.window))return 2;
        if(argc>2&&FAILED(FcSystemSafetyJournal(&owner,argv[2])))return 5;
        guard.Arm();
        if(wcscmp(argv[1],L"--indefinitely-stalled")==0){Sleep(INFINITE);return 12;}
        if(busy)PostMessageW(owner.window,BusyMessage,0,0);
        void* releaseMethods[12]{};releaseMethods[6]=reinterpret_cast<void*>(FailRelease);
        auto releaseTable=releaseMethods;
        if(wcscmp(argv[1],L"--release-failed")==0) {
            owner.delegation=reinterpret_cast<IUnknown*>(&releaseTable);owner.requested=true;owner.pid=42;
            if(Release(owner)!=E_FAIL||!owner.requested||owner.last!=E_FAIL)return 11;
        }
        DWORD interval=emergency||wcscmp(argv[1],L"--release-failed")==0?1500:6000;
        auto until=GetTickCount64()+interval;
        while(GetTickCount64()<until) {
            if(pump){MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){DispatchMessageW(&message);if(GetTickCount64()>=until)break;}}
            Sleep(10); // A blocked owner must remain alive past the former five-second deadline.
        }
        if(emergency) {
            MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE))DispatchMessageW(&message);
            if(emergencyRequests!=1)return 10;
        }
        guard.Disarm();guard.Stop();
        if(owner.window)DestroyWindow(owner.window);
        std::printf("OWNER_SURVIVED mode=%ls busy_messages=%llu emergency_requests=%llu\n",argv[1],busyMessages,emergencyRequests);
        return 0;
    }
    bool passed=true;
    wchar_t executable[MAX_PATH]{};GetModuleFileNameW(nullptr,executable,MAX_PATH);
    for(const auto* mode:{L"--stalled",L"--stalled-window",L"--indefinitely-stalled",L"--release-failed",L"--emergency",L"--message-pump",L"--busy-message-pump",L"--unprotected"}) {
        wchar_t temporary[MAX_PATH]{},journal[MAX_PATH]{};GetTempPathW(MAX_PATH,temporary);GetTempFileNameW(temporary,L"FCR",0,journal);
        std::wstring command=L"\""+std::wstring(executable)+L"\" "+mode+L" \""+journal+L"\"";
        STARTUPINFOW startup{sizeof(startup)};PROCESS_INFORMATION process{};
        auto started=GetTickCount64();
        if(!CreateProcessW(executable,command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,nullptr,&startup,&process))return 4;
        bool indefinite=wcscmp(mode,L"--indefinitely-stalled")==0;
        bool completed=WaitForSingleObject(process.hProcess,indefinite?6500:8000)==WAIT_OBJECT_0;
        DWORD code{};GetExitCodeProcess(process.hProcess,&code);
        bool cleaned=true;
        if(!completed) { // Only clean up the exact test child this supervisor created.
            TerminateProcess(process.hProcess,99);
            cleaned=WaitForSingleObject(process.hProcess,1000)==WAIT_OBJECT_0;
        }
        auto elapsed=GetTickCount64()-started;
        bool shortCase=wcscmp(mode,L"--release-failed")==0||wcscmp(mode,L"--emergency")==0;
        bool okay=indefinite?!completed&&code==STILL_ACTIVE&&cleaned&&elapsed>=6500:
            completed&&code==0&&elapsed>=(shortCase?1500ULL:6000ULL);
        std::ifstream file(journal);std::string record((std::istreambuf_iterator<char>(file)),{});file.close();
        okay=okay&&record.find("\"exitCode\":")==std::string::npos&&record.find("ui-heartbeat-timeout")==std::string::npos;
        std::printf("NO_SELF_TERMINATION %ls passed=%s exit=%lu elapsed_ms=%llu\n",mode,okay?"true":"false",code,elapsed);
        passed=passed&&okay;CloseHandle(process.hThread);CloseHandle(process.hProcess);DeleteFileW(journal);
    }
    return passed?0:1;
}
