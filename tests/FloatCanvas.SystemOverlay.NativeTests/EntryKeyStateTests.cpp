// Replay captured entry boundaries without hardware input or a real delegation call.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>

namespace {
SHORT queuedKeys[256]{};
int asynchronousHeldKey{};
SHORT WINAPI ReplayAsync(int key) {return key==asynchronousHeldKey?-32768:0;}
SHORT WINAPI ReplayQueue(int key) {return key>=0&&key<256?queuedKeys[key]:0;}
}
#define GetAsyncKeyState ReplayAsync
#define GetKeyState ReplayQueue
#include "../../src/FloatCanvas.SystemOverlay.Native/SystemOverlay.cpp"
#undef GetAsyncKeyState
#undef GetKeyState

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
    Release(host);host.delegation=nullptr;DestroyWindow(host.window);
    return passed&&GetForegroundWindow()==foreground?0:1;
}
