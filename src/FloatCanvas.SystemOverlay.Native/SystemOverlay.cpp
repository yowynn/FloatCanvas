// Private Windows ABI from the standalone overlay research. See Docs/Research/System-Overlay.md.
// Owns the external-window/input backend. No target-process hooks.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <roapi.h>
#include <inspectable.h>
#include <winstring.h>
#include <msctf.h>
#include <atomic>
#include <new>
#include "InputSafetyWatchdog.h"

namespace {
constexpr UINT AckMessage = WM_APP + 0x461;
constexpr UINT ChangedMessage = WM_APP + 0x462;
constexpr UINT_PTR EntryKeysTimer = 0x4643;
// Observed WindowServer::ConfigureInputDelegationByProcess pair on build 26300.
// The mode's individual bit meanings are not assumed here.
constexpr UINT AcquireMode = 0x2e, ReleaseMode = 0x21;
constexpr wchar_t ClassName[] = L"FloatCanvas.SystemOverlay.v1";
using CreateBand = HWND(WINAPI*)(DWORD,LPCWSTR,LPCWSTR,DWORD,int,int,int,int,HWND,HMENU,HINSTANCE,void*,DWORD);
using GetBand = BOOL(WINAPI*)(HWND,DWORD*);
struct Ack { unsigned generation, pid; HRESULT result; boolean on; };
struct __declspec(uuid("368d645d-9230-5110-aeb1-17935b4909cd")) AckDelegate : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*,void*) = 0;
};
struct Handler final : AckDelegate {
    std::atomic<ULONG> references{1};
    std::atomic<HWND> window;
    const unsigned generation;
    Handler(HWND hwnd,unsigned epoch) : window(hwnd),generation(epoch) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** value) override {
        if(!value) return E_POINTER;
        *value=nullptr;
        if(iid!=__uuidof(IUnknown)&&iid!=__uuidof(AckDelegate)&&iid!=__uuidof(IAgileObject)) return E_NOINTERFACE;
        *value=static_cast<AckDelegate*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { auto count=--references; if(!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE Invoke(void*,void* args) override {
        auto ack=new(std::nothrow) Ack{generation,0,E_POINTER,false};
        if(!ack) return E_OUTOFMEMORY;
        if(args) {
            auto table=*reinterpret_cast<void***>(args);
            using Id=HRESULT(STDMETHODCALLTYPE*)(void*,UINT32*);
            using On=HRESULT(STDMETHODCALLTYPE*)(void*,boolean*);
            ack->result=reinterpret_cast<Id>(table[6])(args,&ack->pid);
            if(SUCCEEDED(ack->result)) ack->result=reinterpret_cast<On>(table[8])(args,&ack->on);
        }
        HWND receiver=window.load();
        if(!receiver||!PostMessageW(receiver,AckMessage,0,reinterpret_cast<LPARAM>(ack))) delete ack;
        return S_OK;
    }
};
struct Host {
    InputSafetyWatchdog safety;
    HWND window{},receiver{},target{},savedActive{},savedFocus{};
    DWORD thread{GetCurrentThreadId()},pid{};
    IInspectable *site{},*delivery{};
    IUnknown* delegation{};
    Handler* handler{};
    INT64 token{};
    unsigned generation{};
    HRESULT last{S_OK};
    bool initialized{},subscribed{},requested{},acknowledged{},local{},waiting{},focusSaved{};
    bool enabling{},cancelBegin{},destroyPending{};
    Ack* deferredAck{};
    HMODULE textServices{};
    ITfDocumentMgr* canvasDocument{};
};
void Destroy(Host* host);
HRESULT Factory(const wchar_t* name,REFIID iid,void** result) {
    HSTRING value{};
    HRESULT hr=WindowsCreateString(name,static_cast<UINT32>(wcslen(name)),&value);
    if(SUCCEEDED(hr)) hr=RoGetActivationFactory(value,iid,result);
    WindowsDeleteString(value); return hr;
}
HRESULT Initialize(Host& host) {
    HRESULT hr=RoInitialize(RO_INIT_SINGLETHREADED);
    if(FAILED(hr)) return hr;
    host.initialized=true;
    const GUID siteId{0x73f80911,0x7a51,0x5b0f,{0x90,0x6b,0xf6,0x40,0x2e,0xa6,0xc3,0xcb}};
    const GUID deliveryId{0x51344833,0xa92f,0x5989,{0x87,0xed,0xe1,0x28,0x25,0x00,0xd7,0xc8}};
    const GUID delegationId{0x9f03be69,0xe03a,0x55d5,{0x81,0xf0,0xe4,0xff,0x27,0x65,0xd7,0x5c}};
    IUnknown* factory{};
    hr=Factory(L"Windows.UI.Internal.Input.InputSite",siteId,reinterpret_cast<void**>(&factory));
    if(FAILED(hr)) return hr;
    using Site=HRESULT(STDMETHODCALLTYPE*)(void*,UINT64,IInspectable**);
    hr=reinterpret_cast<Site>((*reinterpret_cast<void***>(factory))[7])(factory,reinterpret_cast<UINT64>(host.window),&host.site);
    factory->Release(); if(FAILED(hr)||!host.site) return FAILED(hr)?hr:E_UNEXPECTED;
    hr=Factory(L"Windows.UI.Internal.Input.WindowsMessageDelivery",deliveryId,reinterpret_cast<void**>(&factory));
    if(FAILED(hr)) return hr;
    using Try=HRESULT(STDMETHODCALLTYPE*)(void*,IInspectable*,IInspectable**);
    using Create=HRESULT(STDMETHODCALLTYPE*)(void*,IInspectable*,UINT64,int,IInspectable**);
    auto table=*reinterpret_cast<void***>(factory);
    hr=reinterpret_cast<Try>(table[7])(factory,host.site,&host.delivery);
    if(SUCCEEDED(hr)&&!host.delivery) hr=reinterpret_cast<Create>(table[6])(factory,host.site,reinterpret_cast<UINT64>(host.window),0,&host.delivery);
    factory->Release(); if(FAILED(hr)||!host.delivery) return FAILED(hr)?hr:E_UNEXPECTED;
    hr=Factory(L"Windows.UI.Internal.Input.InputDelegationInputObject",delegationId,reinterpret_cast<void**>(&factory));
    if(FAILED(hr)) return hr;
    using Object=HRESULT(STDMETHODCALLTYPE*)(void*,void*,IUnknown**);
    hr=reinterpret_cast<Object>((*reinterpret_cast<void***>(factory))[6])(factory,host.site,&host.delegation);
    factory->Release(); return hr;
}
void Unsubscribe(Host& host) {
    if(host.handler) host.handler->window=nullptr;
    if(host.subscribed) {
        using Remove=HRESULT(STDMETHODCALLTYPE*)(void*,INT64);
        reinterpret_cast<Remove>((*reinterpret_cast<void***>(host.delegation))[11])(host.delegation,host.token);
        host.subscribed=false;
    }
    if(host.handler) { host.handler->Release(); host.handler=nullptr; }
}
HRESULT Release(Host& host) {
    if(GetCurrentThreadId()!=host.thread) return RPC_E_WRONG_THREAD;
    if(host.enabling) {host.cancelBegin=true;return S_OK;}
    if(!host.requested&&!host.subscribed&&!host.waiting) {host.safety.Disarm();return S_OK;}
    if(host.requested&&!host.local) {
        using Set=HRESULT(STDMETHODCALLTYPE*)(void*,UINT32,UINT32);
        HRESULT hr=reinterpret_cast<Set>((*reinterpret_cast<void***>(host.delegation))[6])(host.delegation,host.pid,ReleaseMode);
        if(FAILED(hr)) return host.last=hr;
    }
    KillTimer(host.window,EntryKeysTimer);host.waiting=false;
    host.requested=false; host.acknowledged=false; host.local=false;
    host.safety.Disarm();
    Unsubscribe(host); ++host.generation;
    if(host.focusSaved) {
        if(!host.savedActive||GetWindowThreadProcessId(host.savedActive,nullptr)==host.thread) SetActiveWindow(host.savedActive);
        if(!host.savedFocus||GetWindowThreadProcessId(host.savedFocus,nullptr)==host.thread) SetFocus(host.savedFocus);
    }
    host.focusSaved=false;
    host.savedActive=host.savedFocus=nullptr; host.target=nullptr; host.pid=0;
    return host.last=S_OK;
}
HRESULT Request(Host& host) {
    if(host.safety.IsEnabled() && !host.safety.Start(host.window)) {
        HRESULT error=HRESULT_FROM_WIN32(GetLastError());host.enabling=false;Release(host);return host.last=error;
    }
    host.safety.Arm();
    if(!host.enabling) {host.enabling=true;host.cancelBegin=false;}
    using Set=HRESULT(STDMETHODCALLTYPE*)(void*,UINT32,UINT32);
    auto table=*reinterpret_cast<void***>(host.delegation);
    HRESULT hr=host.cancelBegin?HRESULT_FROM_WIN32(ERROR_CANCELLED):
        reinterpret_cast<Set>(table[6])(host.delegation,host.pid,AcquireMode);
    host.requested=SUCCEEDED(hr);
    host.enabling=false;
    auto deferred=host.deferredAck;host.deferredAck=nullptr;
    if(FAILED(hr)||host.cancelBegin) {
        HRESULT released=Release(host);
        if(SUCCEEDED(hr))hr=FAILED(released)?released:HRESULT_FROM_WIN32(ERROR_CANCELLED);
        delete deferred;
    } else if(deferred&&!PostMessageW(host.window,AckMessage,0,reinterpret_cast<LPARAM>(deferred))) delete deferred;
    host.last=hr;
    if(host.destroyPending)Destroy(&host);
    return hr;
}
bool PhysicalKeysDown() {
    // Async queries can return zero across integrity levels. After the entry
    // message has returned, also retain any down state in this thread's queue.
    // Wait for the whole chord and mouse buttons without synthesizing releases.
    for(int key=1;key<256;++key) if(GetAsyncKeyState(key)<0||GetKeyState(key)<0)return true;
    return false;
}
HRESULT FocusAndRequest(Host& host) {
    if(!host.enabling) {host.enabling=true;host.cancelBegin=false;}
    if(!host.cancelBegin) {
        host.savedActive=GetActiveWindow();host.savedFocus=GetFocus();host.focusSaved=true;
        SetActiveWindow(host.window);SetFocus(host.receiver);
    }
    return Request(host);
}
HRESULT ContinueEntry(Host& host,HWND foreground,bool keysDown) {
    if(!host.waiting)return S_FALSE;
    DWORD pid{};GetWindowThreadProcessId(host.target,&pid);
    if(foreground!=host.target||pid!=host.pid) {
        Release(host);host.last=HRESULT_FROM_WIN32(ERROR_CANCELLED);
        if(host.receiver)PostMessageW(host.receiver,ChangedMessage,host.generation,FALSE);
        return host.last;
    }
    if(keysDown)return E_PENDING;
    KillTimer(host.window,EntryKeysTimer);host.waiting=false;
    return FocusAndRequest(host);
}
LRESULT CALLBACK Procedure(HWND hwnd,UINT message,WPARAM wparam,LPARAM lparam) {
    auto host=reinterpret_cast<Host*>(GetWindowLongPtrW(hwnd,GWLP_USERDATA));
    if(message==WM_NCCREATE) {
        host=static_cast<Host*>(reinterpret_cast<CREATESTRUCTW*>(lparam)->lpCreateParams);
        host->window=hwnd; SetWindowLongPtrW(hwnd,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(host));
    }
    if(!host) return DefWindowProcW(hwnd,message,wparam,lparam);
    if(message==InputSafetyWatchdog::EmergencyMessage) {
        Release(*host);
        if(host->receiver)PostMessageW(host->receiver,ChangedMessage,host->generation,FALSE);
        return 0;
    }
    if(message==WM_TIMER&&wparam==EntryKeysTimer) {
        ContinueEntry(*host,GetForegroundWindow(),PhysicalKeysDown());return 0;
    }
    if(message==AckMessage) {
        auto ack=reinterpret_cast<Ack*>(lparam);
        if(ack->generation==host->generation&&(host->requested||host->enabling)&&SUCCEEDED(ack->result)&&
           ((!ack->on)||ack->pid==host->pid)) {
            if(host->enabling) {delete host->deferredAck;host->deferredAck=ack;return 0;}
            const bool wasAcknowledged=host->acknowledged;
            host->acknowledged=ack->on!=0;
            if(host->acknowledged&&!wasAcknowledged) { SetActiveWindow(hwnd); if(host->receiver) SetFocus(host->receiver); }
            if(host->receiver) PostMessageW(host->receiver,ChangedMessage,host->generation,host->acknowledged);
        }
        delete ack; return 0;
    }
    if(message==WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    return DefWindowProcW(hwnd,message,wparam,lparam);
}
void Destroy(Host* host) {
    if(!host) return;
    if(host->enabling) {host->destroyPending=true;host->cancelBegin=true;return;}
    Release(*host); Unsubscribe(*host);
    host->safety.Stop();
    delete host->deferredAck;
    if(host->window) {
        MSG queued{};
        while(PeekMessageW(&queued,host->window,AckMessage,AckMessage,PM_REMOVE)) delete reinterpret_cast<Ack*>(queued.lParam);
        DestroyWindow(host->window);
    }
    if(host->delegation) host->delegation->Release();
    if(host->delivery) host->delivery->Release();
    if(host->site) host->site->Release();
    if(host->canvasDocument) host->canvasDocument->Release();
    if(host->textServices) FreeLibrary(host->textServices);
    if(host->initialized) RoUninitialize();
    delete host;
}
}

#define EXPORT extern "C" __declspec(dllexport)
EXPORT HRESULT WINAPI FcSystemCreate(int x,int y,int width,int height,Host** result,HWND* window) {
    if(!result||!window) return E_POINTER;
    *result=nullptr; *window=nullptr;
    // Test actual exports, band creation and COM initialization. An OS build
    // number alone establishes neither support nor failure of this backend.
    auto user=GetModuleHandleW(L"user32.dll");
    auto create=reinterpret_cast<CreateBand>(GetProcAddress(user,"CreateWindowInBand"));
    auto band=reinterpret_cast<GetBand>(GetProcAddress(user,"GetWindowBand"));
    if(!create||!band) return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
    auto host=new(std::nothrow) Host;
    if(!host) return E_OUTOFMEMORY;
    WNDCLASSW wc{}; wc.lpfnWndProc=Procedure; wc.hInstance=GetModuleHandleW(nullptr); wc.lpszClassName=ClassName;
    wc.hCursor=LoadCursorW(nullptr,IDC_ARROW);
    if(!RegisterClassW(&wc)&&GetLastError()!=ERROR_CLASS_ALREADY_EXISTS) { auto hr=HRESULT_FROM_WIN32(GetLastError()); delete host; return hr; }
    auto hwnd=create(WS_EX_TOPMOST|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE,ClassName,L"FloatCanvas System Overlay",
        WS_POPUP|WS_CLIPCHILDREN,x,y,width,height,nullptr,nullptr,wc.hInstance,host,16);
    if(!hwnd) { auto hr=HRESULT_FROM_WIN32(GetLastError()); delete host; return hr; }
    DWORD actual{};
    HRESULT hr=band(hwnd,&actual)&&actual==16?Initialize(*host):E_ACCESSDENIED;
    if(FAILED(hr)) { Destroy(host); return hr; }
    *result=host; *window=hwnd; return S_OK;
}
EXPORT HRESULT WINAPI FcSystemReceiver(Host* host,HWND receiver) {
    if(!host||GetCurrentThreadId()!=host->thread||GetWindowThreadProcessId(receiver,nullptr)!=host->thread) return E_INVALIDARG;
    host->receiver=receiver; return S_OK;
}
EXPORT HRESULT WINAPI FcSystemSafetyJournal(Host* host,const wchar_t* path) {
    if(!host||!path||GetCurrentThreadId()!=host->thread)return E_INVALIDARG;
    // Retain the existing ABI without creating heartbeat/termination journals.
    // The release monitor has no timeout or autonomous process-exit action.
    return S_FALSE;
}
EXPORT HRESULT WINAPI FcSystemSafetyPolicy(Host* host,BOOL enabled) {
    if(!host||GetCurrentThreadId()!=host->thread)return E_INVALIDARG;
    return host->safety.Configure(enabled!=FALSE)?S_OK:HRESULT_FROM_WIN32(ERROR_BUSY);
}
// WPF clears its managed focus on a native HWND handoff without necessarily
// clearing TSF's document focus. Drawing HWNDs have no text store: focus an empty
// document as WPF itself does for non-text controls. Never activate another TSF
// manager or change the user's input language, conversion mode or external HWND.
EXPORT HRESULT WINAPI FcSystemFocusCanvas(Host* host,HWND canvas) {
    if(!host||GetCurrentThreadId()!=host->thread||GetFocus()!=canvas||
        GetWindowThreadProcessId(canvas,nullptr)!=host->thread) return E_INVALIDARG;
    if(!host->textServices && !GetModuleHandleExW(0,L"msctf.dll",&host->textServices)) return S_FALSE;
    using GetManager=HRESULT(WINAPI*)(ITfThreadMgr**);
    auto get=reinterpret_cast<GetManager>(GetProcAddress(host->textServices,"TF_GetThreadMgr"));
    if(!get) return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
    ITfThreadMgr* manager{};
    HRESULT hr=get(&manager);
    if(SUCCEEDED(hr)&&manager) {
        if(!host->canvasDocument) hr=manager->CreateDocumentMgr(&host->canvasDocument);
        if(SUCCEEDED(hr)) hr=manager->SetFocus(host->canvasDocument);
    }
    if(manager) manager->Release();
    return hr;
}
static HRESULT Begin(Host* host,HWND target,HWND foreground) {
    if(!host||GetCurrentThreadId()!=host->thread) return E_INVALIDARG;
    if(host->requested||host->enabling||host->waiting) return HRESULT_FROM_WIN32(ERROR_BUSY);
    DWORD pid{}; GetWindowThreadProcessId(target,&pid);
    if(!pid||target!=foreground) return E_INVALIDARG;
    host->target=target; host->pid=pid; ++host->generation;
    // A tray menu makes its own NotifyIcon window foreground. Input is already
    // ours in that case: use ordinary local focus, never delegate to ourselves.
    if(pid==GetCurrentProcessId()) {
        host->savedActive=GetActiveWindow();host->savedFocus=GetFocus();host->focusSaved=true;
        host->local=true;host->requested=true;host->acknowledged=true;
        SetActiveWindow(host->window);SetFocus(host->receiver);
        if(host->receiver)PostMessageW(host->receiver,ChangedMessage,host->generation,TRUE);
        return host->last=S_OK;
    }
    host->handler=new(std::nothrow) Handler(host->window,host->generation);
    if(!host->handler) return E_OUTOFMEMORY;
    host->enabling=true;host->cancelBegin=false;
    using Add=HRESULT(STDMETHODCALLTYPE*)(void*,AckDelegate*,INT64*);
    auto table=*reinterpret_cast<void***>(host->delegation);
    HRESULT hr=reinterpret_cast<Add>(table[10])(host->delegation,host->handler,&host->token);
    if(FAILED(hr)) {
        host->enabling=false;Unsubscribe(*host);host->last=hr;
        delete host->deferredAck;host->deferredAck=nullptr;
        if(host->destroyPending)Destroy(host);
        return hr;
    }
    host->subscribed=true;
    // WM_HOTKEY can arrive before this thread's key-state snapshot is current.
    // Always return through the message pump before the first external acquire.
    if(!host->cancelBegin) {
        host->enabling=false;delete host->deferredAck;host->deferredAck=nullptr;
        host->waiting=true;host->last=E_PENDING;
        if(SetTimer(host->window,EntryKeysTimer,10,nullptr))return S_OK;
        HRESULT error=HRESULT_FROM_WIN32(GetLastError());Release(*host);return host->last=error;
    }
    return FocusAndRequest(*host);
}
EXPORT HRESULT WINAPI FcSystemBegin(Host* host,HWND target) {return Begin(host,target,GetForegroundWindow());}
EXPORT HRESULT WINAPI FcSystemEnd(Host* host) { return host?Release(*host):E_INVALIDARG; }
struct FcSystemStatus { unsigned generation,pid; BOOL requested,acknowledged,waiting; HRESULT last; };
static_assert(sizeof(FcSystemStatus)==24);
EXPORT HRESULT WINAPI FcSystemGetStatus(Host* host,FcSystemStatus* status) {
    if(!host||!status) return E_POINTER;
    if(GetCurrentThreadId()!=host->thread) return RPC_E_WRONG_THREAD;
    *status={host->generation,host->pid,host->waiting||host->requested||host->enabling,host->acknowledged,host->waiting,host->last}; return S_OK;
}
EXPORT void WINAPI FcSystemDestroy(Host* host) { if(host&&GetCurrentThreadId()==host->thread) Destroy(host); }
EXPORT HWND WINAPI FcSystemCreateHostedWindow(DWORD extended,const wchar_t* name,const wchar_t* title,
    DWORD style,int x,int y,int width,int height,void* parameter) {
    auto create=reinterpret_cast<CreateBand>(GetProcAddress(GetModuleHandleW(L"user32.dll"),"CreateWindowInBand"));
    if(!create) { SetLastError(ERROR_PROC_NOT_FOUND);return nullptr; }
    return create(extended,name,title,style,x,y,width,height,nullptr,nullptr,GetModuleHandleW(nullptr),parameter,16);
}
