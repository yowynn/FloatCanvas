// Exercise the production ACK procedure with a reentrant COM-call double.
// Uses hidden own-process windows; never delegates real input or changes global foreground.
#include "../../src/FloatCanvas.SystemOverlay.Native/SystemOverlay.cpp"
#include <cstdio>

namespace {
void Pump(HWND window) {
    MSG msg{};
    while(PeekMessageW(&msg,window,AckMessage,AckMessage,PM_REMOVE))DispatchMessageW(&msg);
}
struct Delivery {
    void** table;
    Host* host;
    bool cancel{},destroy{};
    unsigned releases{},acquires{};
    static ULONG STDMETHODCALLTYPE Drop(void*) {return 1;}
    static HRESULT STDMETHODCALLTYPE Set(void* self,UINT32,UINT32 mode) {
        auto& delivery=*static_cast<Delivery*>(self);
        if(mode==0x2e) {
            ++delivery.acquires;
            auto ack=new Ack{delivery.host->generation,delivery.host->pid,S_OK,true};
            PostMessageW(delivery.host->window,AckMessage,0,reinterpret_cast<LPARAM>(ack));
            // STA RPC can pump this callback before SetByProcess returns.
            Pump(delivery.host->window);
            if(delivery.cancel)Release(*delivery.host);
            if(delivery.destroy)Destroy(delivery.host);
        }
        if(mode==0x21)++delivery.releases;
        if(mode!=0x2e&&mode!=0x21)return E_INVALIDARG;
        return S_OK;
    }
};
}
int wmain() {
    HWND foreground=GetForegroundWindow();
    Host host;
    WNDCLASSW wc{};wc.lpfnWndProc=Procedure;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=ClassName;
    RegisterClassW(&wc);
    HWND window=CreateWindowExW(WS_EX_NOACTIVATE,ClassName,L"FloatCanvas native ACK test",WS_POPUP,-32000,-32000,1,1,
        nullptr,nullptr,wc.hInstance,&host);
    if(!window)return 2;
    void* methods[12]{};methods[6]=reinterpret_cast<void*>(Delivery::Set);methods[2]=reinterpret_cast<void*>(Delivery::Drop);
    Delivery delivery{methods,&host};host.delegation=reinterpret_cast<IUnknown*>(&delivery);
    host.pid=42;host.generation=1;host.receiver=window;
    HRESULT result=Request(host);Pump(window);
    bool passed=SUCCEEDED(result)&&host.acknowledged;
    std::printf("EARLY_ACK acknowledged=%s expected=true\n",host.acknowledged?"true":"false");
    HWND editor=CreateWindowExW(WS_EX_NOACTIVATE,L"STATIC",L"Other local focus",WS_POPUP,-32000,-32000,1,1,nullptr,nullptr,wc.hInstance,nullptr);
    SetActiveWindow(editor);SetFocus(editor);
    PostMessageW(window,AckMessage,0,reinterpret_cast<LPARAM>(new Ack{1,42,S_OK,true}));Pump(window);
    bool retained=GetFocus()==editor;
    std::printf("REPEATED_ACK retained_editor_focus=%s expected=true\n",retained?"true":"false");passed=passed&&retained;
    Release(host);
    unsigned beforeAcquire=delivery.acquires;
    host.pid=GetCurrentProcessId();host.target=window;host.waiting=true;
    FcSystemStatus waitingStatus{};FcSystemGetStatus(&host,&waitingStatus);
    bool deferred=ContinueEntry(host,window,true)==E_PENDING&&host.waiting&&!host.requested&&!host.acknowledged&&
        delivery.acquires==beforeAcquire&&GetFocus()==editor&&waitingStatus.waiting&&waitingStatus.requested;
    result=ContinueEntry(host,window,false);Pump(window);
    bool cleanEntry=SUCCEEDED(result)&&!host.waiting&&host.requested&&host.acknowledged&&delivery.acquires==beforeAcquire+1;
    Release(host);
    std::printf("HELD_KEYS deferred=%s acquire_after_release=%s expected=true\n",deferred?"true":"false",cleanEntry?"true":"false");
    passed=passed&&deferred&&cleanEntry;
    unsigned beforeRelease=delivery.releases;
    host.pid=GetCurrentProcessId();host.target=window;host.waiting=true;
    result=ContinueEntry(host,editor,false);
    bool cancelledWait=result==HRESULT_FROM_WIN32(ERROR_CANCELLED)&&!host.waiting&&!host.requested&&
        delivery.releases==beforeRelease&&delivery.acquires==beforeAcquire+1&&GetFocus()==editor;
    std::printf("HELD_KEYS_FOREGROUND_CHANGE cancelled=%s no_delegate_call=true expected=true\n",cancelledWait?"true":"false");
    passed=passed&&cancelledWait;
    host.pid=GetCurrentProcessId();host.target=window;host.waiting=true;
    Release(host);
    bool explicitCancel=!host.waiting&&ContinueEntry(host,window,false)==S_FALSE&&delivery.releases==beforeRelease&&delivery.acquires==beforeAcquire+1;
    std::printf("HELD_KEYS_END cancelled=%s no_late_acquire=true expected=true\n",explicitCancel?"true":"false");
    passed=passed&&explicitCancel;
    HRESULT localResult=Begin(&host,window,window);
    bool localReady=SUCCEEDED(localResult)&&host.requested&&host.acknowledged&&host.pid==GetCurrentProcessId();
    std::printf("TRAY_OWN_FOREGROUND ready=%s hr=%08lx expected=true\n",localReady?"true":"false",localResult);
    passed=passed&&localReady;
    bool localBusy=Begin(&host,window,window)==HRESULT_FROM_WIN32(ERROR_BUSY);
    unsigned previousReleases=delivery.releases;
    Release(host);
    bool localReleased=!host.requested&&!host.acknowledged&&!host.local&&delivery.releases==previousReleases;
    bool rejectsStale=Begin(&host,window,editor)==E_INVALIDARG&&!host.requested;
    std::printf("TRAY_LOCAL_RELEASE clean=%s busy=%s stale_rejected=%s expected=true\n",localReleased?"true":"false",localBusy?"true":"false",rejectsStale?"true":"false");
    passed=passed&&localReleased&&localBusy&&rejectsStale;
    beforeRelease=delivery.releases;delivery.cancel=true;host.pid=42;
    result=Request(host);Pump(window);
    bool cancelled=result==HRESULT_FROM_WIN32(ERROR_CANCELLED)&&!host.requested&&!host.acknowledged&&delivery.releases==beforeRelease+1;
    std::printf("REENTRANT_END cancelled=%s expected=true\n",cancelled?"true":"false");passed=passed&&cancelled;
    auto doomed=new Host;
    HWND doomedWindow=CreateWindowExW(WS_EX_NOACTIVATE,ClassName,L"Destroy during ACK test",WS_POPUP,-32000,-32000,1,1,nullptr,nullptr,wc.hInstance,doomed);
    Delivery closing{methods,doomed,false,true};doomed->delegation=reinterpret_cast<IUnknown*>(&closing);doomed->pid=42;
    result=Request(*doomed);
    bool closed=result==HRESULT_FROM_WIN32(ERROR_CANCELLED)&&!IsWindow(doomedWindow)&&closing.releases==1;
    std::printf("REENTRANT_DESTROY closed=%s expected=true\n",closed?"true":"false");passed=passed&&closed;
    auto pending=new Host;
    HWND pendingWindow=CreateWindowExW(WS_EX_NOACTIVATE,ClassName,L"Destroy waiting entry",WS_POPUP,-32000,-32000,1,1,nullptr,nullptr,wc.hInstance,pending);
    pending->waiting=true;SetTimer(pendingWindow,EntryKeysTimer,10,nullptr);Destroy(pending);
    bool pendingClosed=!IsWindow(pendingWindow);
    std::printf("HELD_KEYS_DESTROY closed=%s expected=true\n",pendingClosed?"true":"false");passed=passed&&pendingClosed;
    passed=passed&&GetForegroundWindow()==foreground;
    host.delegation=nullptr;DestroyWindow(editor);DestroyWindow(window);
    return passed?0:1;
}
