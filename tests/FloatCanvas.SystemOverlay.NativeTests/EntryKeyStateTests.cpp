// Replay captured entry boundaries without hardware input or a real delegation call.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cstring>

namespace {
SHORT queuedKeys[256]{};
int asynchronousHeldKey{};
ULONGLONG replayNow{10000};
SHORT WINAPI ReplayAsync(int key) {return key==asynchronousHeldKey?-32768:0;}
SHORT WINAPI ReplayQueue(int key) {return key>=0&&key<256?queuedKeys[key]:0;}
ULONGLONG WINAPI ReplayTickCount64() {return replayNow;}
}
#define GetAsyncKeyState ReplayAsync
#define GetKeyState ReplayQueue
#define GetTickCount64 ReplayTickCount64
#include "../../src/FloatCanvas.SystemOverlay.Native/SystemOverlay.cpp"
#undef GetAsyncKeyState
#undef GetKeyState
#undef GetTickCount64

namespace {
struct Calls {
    void** table;
    unsigned acquires{},releases{};
    static HRESULT STDMETHODCALLTYPE Set(void* value,UINT32,UINT32 mode) {
        auto& self=*static_cast<Calls*>(value);
        if(mode==AcquireMode)++self.acquires;
        if(mode==ReleaseMode)++self.releases;
        return S_OK;
    }
    static HRESULT STDMETHODCALLTYPE Add(void*,AckDelegate*,INT64* token) {*token=1;return S_OK;}
    static HRESULT STDMETHODCALLTYPE Remove(void*,INT64) {return S_OK;}
};
void PumpAcks(HWND window) {
    MSG message{};
    while(PeekMessageW(&message,window,AckMessage,AckMessage,PM_REMOVE))DispatchMessageW(&message);
}
bool TakeTimeoutNotice(HWND window,unsigned generation) {
    MSG message{};bool found=false;
    while(PeekMessageW(&message,window,ChangedMessage,ChangedMessage,PM_REMOVE))
        if(message.wParam==generation&&message.lParam==FALSE)found=true;
    return found;
}
bool EntryBlockersMatch(Host& host,const BYTE* expected,HRESULT result=S_FALSE) {
    BYTE actual[32]{};
    return FcSystemGetEntryBlockers(&host,actual,sizeof(actual))==result&&std::memcmp(actual,expected,sizeof(actual))==0;
}
}
int main() {
    bool passed=true;
    const int keys[]={VK_MENU,VK_CONTROL,VK_SHIFT,VK_LWIN,VK_RWIN,VK_F8,VK_LBUTTON,VK_RBUTTON};
    for(int key:keys) {
        ZeroMemory(queuedKeys,sizeof(queuedKeys));queuedKeys[key]=-32768;
        bool held=PhysicalKeysDown();
        std::printf("ASYNC_UNAVAILABLE_QUEUE_KEY_%02X waits=%s expected=true\n",key,held?"true":"false");
        passed=passed&&held;
    }
    ZeroMemory(queuedKeys,sizeof(queuedKeys));queuedKeys[VK_CONTROL]=queuedKeys[VK_SHIFT]=-32768;
    bool remaining=PhysicalKeysDown();passed=passed&&remaining;
    std::printf("ALT_RELEASED_CTRL_SHIFT_HELD waits=%s expected=true\n",remaining?"true":"false");
    ZeroMemory(queuedKeys,sizeof(queuedKeys));queuedKeys[VK_CAPITAL]=1;
    bool clear=!PhysicalKeysDown();passed=passed&&clear;
    std::printf("UP_WITH_CAPS_TOGGLED proceeds=%s expected=true\n",clear?"true":"false");
    asynchronousHeldKey=VK_CONTROL;bool async=PhysicalKeysDown();passed=passed&&async;asynchronousHeldKey=0;
    std::printf("ASYNC_HELD_QUEUE_UP waits=%s expected=true\n",async?"true":"false");

    HWND foreground=GetForegroundWindow();
    Host host;WNDCLASSW wc{};wc.lpfnWndProc=Procedure;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=ClassName;
    RegisterClassW(&wc);
    host.window=CreateWindowExW(WS_EX_NOACTIVATE,ClassName,L"Entry boundary replay",WS_POPUP,-32000,-32000,1,1,
        nullptr,nullptr,wc.hInstance,&host);
    if(!host.window)return 2;
    void* table[12]{};
    table[6]=reinterpret_cast<void*>(Calls::Set);table[10]=reinterpret_cast<void*>(Calls::Add);table[11]=reinterpret_cast<void*>(Calls::Remove);
    Calls calls{table};host.delegation=reinterpret_cast<IUnknown*>(&calls);host.receiver=host.window;
    // Only window identity is read. The COM double never configures this process's input.
    HWND external=GetShellWindow();DWORD externalPid{};GetWindowThreadProcessId(external,&externalPid);
    if(!externalPid||externalPid==GetCurrentProcessId())return 3;
    HRESULT result=Begin(&host,external,external);
    bool deferred=SUCCEEDED(result)&&host.waiting&&!host.requested&&calls.acquires==0;
    std::printf("HOTKEY_QUEUE_NOT_UPDATED deferred_before_pump=%s expected=true\n",deferred?"true":"false");passed=passed&&deferred;
    ContinueEntry(host,external,true);
    bool held=host.waiting&&calls.acquires==0;passed=passed&&held;
    ContinueEntry(host,external,false);
    bool acquired=host.requested&&calls.acquires==1;passed=passed&&acquired;
    std::printf("NEXT_PUMP_HELD_THEN_RELEASE waits=%s acquire_once=%s expected=true\n",held?"true":"false",acquired?"true":"false");
    Release(host);
    ZeroMemory(queuedKeys,sizeof(queuedKeys));asynchronousHeldKey='2';
    queuedKeys[VK_CONTROL]=queuedKeys[VK_RBUTTON]=-32768;
    BYTE expected[32]{};
    const int unionKeys[]={'2',VK_CONTROL,VK_RBUTTON};
    for(int key:unionKeys)expected[key/8]|=static_cast<BYTE>(1u<<(key%8));
    BYTE physicalMask[32];FillMemory(physicalMask,sizeof(physicalMask),0xFF);
    bool unionCaptured=PhysicalKeysDown(physicalMask)&&std::memcmp(physicalMask,expected,sizeof(expected))==0;
    result=Begin(&host,external,external);
    ContinueEntry(host,external,PhysicalKeysDown(host.entryBlockers));
    BYTE guarded[34];FillMemory(guarded,sizeof(guarded),0xA5);
    bool rejected=FcSystemGetEntryBlockers(nullptr,guarded+1,32)==E_INVALIDARG&&
        FcSystemGetEntryBlockers(&host,nullptr,32)==E_INVALIDARG&&
        FcSystemGetEntryBlockers(&host,guarded+1,0)==E_INVALIDARG&&
        FcSystemGetEntryBlockers(&host,guarded+1,31)==E_INVALIDARG&&
        FcSystemGetEntryBlockers(&host,guarded+1,33)==E_INVALIDARG;
    for(BYTE value:guarded)rejected=rejected&&value==0xA5;
    bool exactCopy=SUCCEEDED(result)&&FcSystemGetEntryBlockers(&host,guarded+1,32)==S_FALSE&&
        guarded[0]==0xA5&&guarded[33]==0xA5&&std::memcmp(guarded+1,expected,32)==0;
    HRESULT pendingLast=host.last;host.last=HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    bool backendTimeoutNotKeyTimeout=EntryBlockersMatch(host,expected,S_FALSE);
    host.last=pendingLast;
    Release(host);
    bool releaseRetained=EntryBlockersMatch(host,expected);
    BYTE empty[32]{};
    result=Begin(&host,external,external);
    bool resetOnBegin=SUCCEEDED(result)&&EntryBlockersMatch(host,empty);
    Release(host);
    asynchronousHeldKey=0;ZeroMemory(queuedKeys,sizeof(queuedKeys));
    std::printf("BLOCKER_SNAPSHOT physical_queue_union=%s invalid_arguments_rejected=%s exact_32_bytes=%s expected=true\n",
        unionCaptured?"true":"false",rejected?"true":"false",exactCopy?"true":"false");
    std::printf("BLOCKER_SNAPSHOT release_retains=%s fresh_begin_resets=%s expected=true\n",
        releaseRetained?"true":"false",resetOnBegin?"true":"false");
    std::printf("BLOCKER_SNAPSHOT backend_timeout_not_key_timeout=%s expected=true\n",backendTimeoutNotKeyTimeout?"true":"false");
    passed=passed&&unionCaptured&&rejected&&exactCopy&&releaseRetained&&resetOnBegin&&backendTimeoutNotKeyTimeout;
    const auto timeout=HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    auto timeoutAndRetry=[&](const char* name,int physicalKey,int queuedKey) {
        ZeroMemory(queuedKeys,sizeof(queuedKeys));asynchronousHeldKey=physicalKey;
        if(queuedKey)queuedKeys[queuedKey]=-32768;
        replayNow+=10000;
        unsigned beforeAcquire=calls.acquires,beforeRelease=calls.releases;
        TakeTimeoutNotice(host.window,host.generation);
        HRESULT began=Begin(&host,external,external);unsigned oldGeneration=host.generation;
        BYTE expectedBlockers[32]{};
        const int expectedKeys[]={physicalKey,queuedKey};
        for(int key:expectedKeys)if(key)expectedBlockers[key/8]|=static_cast<BYTE>(1u<<(key%8));
        replayNow+=1999;
        HRESULT pending=ContinueEntry(host,external,PhysicalKeysDown(host.entryBlockers));
        bool protectedWait=SUCCEEDED(began)&&pending==E_PENDING&&host.waiting&&!host.requested&&
            !host.acknowledged&&calls.acquires==beforeAcquire;
        replayNow+=2;
        HRESULT expired=ContinueEntry(host,external,PhysicalKeysDown(host.entryBlockers));
        FcSystemStatus status{};FcSystemGetStatus(&host,&status);
        bool cancelled=expired==timeout&&status.last==timeout&&!status.waiting&&!status.requested&&
            !status.acknowledged&&host.generation!=oldGeneration&&calls.acquires==beforeAcquire&&calls.releases==beforeRelease;
        bool notified=TakeTimeoutNotice(host.window,host.generation);
        bool timeoutRetained=EntryBlockersMatch(host,expectedBlockers,S_OK);
        std::printf("%s bounded_wait=%s cancelled=%s timeout_notice=%s expected=true\n",name,
            protectedWait?"true":"false",cancelled?"true":"false",notified?"true":"false");
        passed=passed&&protectedWait&&cancelled&&notified&&timeoutRetained;

        asynchronousHeldKey=0;ZeroMemory(queuedKeys,sizeof(queuedKeys));
        Procedure(host.window,WM_TIMER,EntryKeysTimer,0);
        bool lateTimerRetained=EntryBlockersMatch(host,expectedBlockers,S_OK);
        std::printf("%s timeout_snapshot_retained=%s late_timer_retains_snapshot=%s expected=true\n",name,
            timeoutRetained?"true":"false",lateTimerRetained?"true":"false");
        passed=passed&&lateTimerRetained;
        HRESULT late=ContinueEntry(host,external,PhysicalKeysDown());
        PostMessageW(host.window,AckMessage,0,reinterpret_cast<LPARAM>(new Ack{oldGeneration,externalPid,S_OK,true}));
        PumpAcks(host.window);
        bool noLateAcquire=late==S_FALSE&&!host.waiting&&!host.requested&&!host.acknowledged&&calls.acquires==beforeAcquire;
        std::printf("%s late_release_and_old_ack_ignored=%s expected=true\n",name,noLateAcquire?"true":"false");
        passed=passed&&noLateAcquire;
        Release(host);
        bool timeoutReleaseRetained=EntryBlockersMatch(host,expectedBlockers,S_OK);
        std::printf("%s explicit_release_retains_timeout_snapshot=%s expected=true\n",name,timeoutReleaseRetained?"true":"false");
        passed=passed&&timeoutReleaseRetained;

        HRESULT retried=Begin(&host,external,external);
        HRESULT resumed=ContinueEntry(host,external,PhysicalKeysDown());
        bool retry=SUCCEEDED(retried)&&SUCCEEDED(resumed)&&!host.waiting&&host.requested&&calls.acquires==beforeAcquire+1;
        PostMessageW(host.window,AckMessage,0,reinterpret_cast<LPARAM>(new Ack{oldGeneration,externalPid,S_OK,true}));
        PumpAcks(host.window);
        bool staleIgnored=!host.acknowledged;
        PostMessageW(host.window,AckMessage,0,reinterpret_cast<LPARAM>(new Ack{host.generation,externalPid,S_OK,true}));
        PumpAcks(host.window);
        bool freshAcknowledged=host.acknowledged;
        PostMessageW(host.window,AckMessage,0,reinterpret_cast<LPARAM>(new Ack{oldGeneration,externalPid,S_OK,false}));
        PumpAcks(host.window);
        bool freshRetained=host.acknowledged;
        std::printf("%s retry_acquires_once=%s old_ack_ignored=%s fresh_ack_retained=%s expected=true\n",name,
            retry?"true":"false",staleIgnored?"true":"false",freshAcknowledged&&freshRetained?"true":"false");
        passed=passed&&retry&&staleIgnored&&freshAcknowledged&&freshRetained;
        Release(host);
    };
    timeoutAndRetry("GHOST_DIGIT_2",'2',0);
    timeoutAndRetry("STALE_QUEUED_CONTROL",0,VK_CONTROL);
    replayNow+=10000;
    unsigned beforeDelayed=calls.acquires;
    result=Begin(&host,external,external);
    replayNow+=2001;
    HRESULT delayed=ContinueEntry(host,external,PhysicalKeysDown(host.entryBlockers));
    bool delayedRelease=SUCCEEDED(result)&&SUCCEEDED(delayed)&&!host.waiting&&host.requested&&host.last==S_OK&&calls.acquires==beforeDelayed+1;
    std::printf("DELAYED_TIMER_RELEASED_KEYS acquires_without_false_timeout=%s expected=true\n",delayedRelease?"true":"false");
    passed=passed&&delayedRelease;
    Release(host);
    host.delegation=nullptr;DestroyWindow(host.window);
    return passed&&GetForegroundWindow()==foreground?0:1;
}
