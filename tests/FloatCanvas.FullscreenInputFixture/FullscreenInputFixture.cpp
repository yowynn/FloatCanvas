// Self-owned input client: never inspects FloatCanvas or changes behavior for an overlay.
// D3D11/DXGI setup follows the previously exercised gamebartest FullscreenProbe.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
using Clock=std::chrono::steady_clock;

struct Fixture {
    HWND window{};
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGISwapChain> swap;
    ComPtr<ID3D11RenderTargetView> target;
    ComPtr<IDXGISurface1> surface;
    HFONT font{};
    std::ofstream log;
    std::filesystem::path logPath;
    Clock::time_point start=Clock::now(),lastSample=start;
    bool quit{},resizePending{},exclusivePending{},suspendPending{},wasForeground{},windowed{true},lockEnabled{},clipOwned{},locked{};
    bool legacyAlt{},rawAlt{},rawRegistered{},elevated{};
    DWORD integrityRid{},rawError{},clipError{},warpError{};
    uint64_t frames{},rawKeyboard{},rawMouse{},rawButtons{},rawReadErrors{},keys{},clicks{};
    uint64_t legacyAltDown{},legacyAltUp{},rawAltDown{},rawAltUp{},clipCalls{},warpCalls{};
    int64_t cameraX{},cameraY{};
    HRESULT presentResult=S_OK,exclusiveResult=E_PENDING;
    std::wstring status=L"选择 F5–F8 开始对照；当前是窗口、不锁鼠。";
    RECT savedRect{80,80,1360,900};
    double seconds{}; // Optional bounded unattended smoke only; interactive launch has no timeout.
    unsigned caseId{};
    static constexpr int EmergencyHotkey=0x4641;
    const char* caseName() const {return windowed?(lockEnabled?"windowed-locked":"windowed-free"):(lockEnabled?"fullscreen-locked":"fullscreen-free");}

    void readToken() {
        HANDLE token{};
        if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token))return;
        TOKEN_ELEVATION elevation{};DWORD bytes{};
        if(GetTokenInformation(token,TokenElevation,&elevation,sizeof(elevation),&bytes))elevated=elevation.TokenIsElevated!=0;
        GetTokenInformation(token,TokenIntegrityLevel,nullptr,0,&bytes);
        std::vector<BYTE> buffer(bytes);
        if(bytes&&GetTokenInformation(token,TokenIntegrityLevel,buffer.data(),bytes,&bytes)) {
            auto label=reinterpret_cast<TOKEN_MANDATORY_LABEL*>(buffer.data());
            auto count=*GetSidSubAuthorityCount(label->Label.Sid);
            if(count)integrityRid=*GetSidSubAuthority(label->Label.Sid,count-1);
        }
        CloseHandle(token);
    }
    void record(const char* event) {
        if(!log)return;
        BOOL full=FALSE;HRESULT query=swap?swap->GetFullscreenState(&full,nullptr):E_PENDING;
        RECT client{},clip{};POINT cursor{};
        GetClientRect(window,&client);GetClipCursor(&clip);GetCursorPos(&cursor);
        SYSTEMTIME utc{};GetSystemTime(&utc);char stamp[40]{};
        sprintf_s(stamp,"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",utc.wYear,utc.wMonth,utc.wDay,utc.wHour,utc.wMinute,utc.wSecond,utc.wMilliseconds);
        log<<"{\"utc\":\""<<stamp<<"\",\"event\":\""<<event<<"\",\"pid\":"<<GetCurrentProcessId()
            <<",\"elapsed_s\":"<<std::fixed<<std::setprecision(3)<<std::chrono::duration<double>(Clock::now()-start).count()
            <<",\"case_id\":"<<caseId<<",\"case\":\""<<caseName()<<"\",\"lock_enabled\":"<<(lockEnabled?"true":"false")
            <<",\"foreground\":"<<(GetForegroundWindow()==window?"true":"false")
            <<",\"dxgi_fullscreen\":"<<(full?"true":"false")<<",\"fullscreen_query_hr\":"<<static_cast<uint32_t>(query)
            <<",\"exclusive_hr\":"<<static_cast<uint32_t>(exclusiveResult)<<",\"present_hr\":"<<static_cast<uint32_t>(presentResult)
            <<",\"minimized\":"<<(IsIconic(window)?"true":"false")<<",\"width\":"<<client.right<<",\"height\":"<<client.bottom
            <<",\"elevated\":"<<(elevated?"true":"false")<<",\"integrity_rid\":"<<integrityRid
            <<",\"locked\":"<<(locked?"true":"false")<<",\"legacy_alt\":"<<(legacyAlt?"true":"false")<<",\"raw_alt\":"<<(rawAlt?"true":"false")
            <<",\"legacy_alt_down\":"<<legacyAltDown<<",\"legacy_alt_up\":"<<legacyAltUp<<",\"raw_alt_down\":"<<rawAltDown<<",\"raw_alt_up\":"<<rawAltUp
            <<",\"async_alt\":"<<(GetAsyncKeyState(VK_MENU)<0?"true":"false")
            <<",\"async_ctrl\":"<<(GetAsyncKeyState(VK_CONTROL)<0?"true":"false")
            <<",\"async_shift\":"<<(GetAsyncKeyState(VK_SHIFT)<0?"true":"false")
            <<",\"queue_alt\":"<<(GetKeyState(VK_MENU)<0?"true":"false")
            <<",\"keys\":"<<keys<<",\"clicks\":"<<clicks<<",\"raw_keyboard\":"<<rawKeyboard<<",\"raw_mouse\":"<<rawMouse<<",\"raw_buttons\":"<<rawButtons
            <<",\"raw_registered\":"<<(rawRegistered?"true":"false")<<",\"raw_error\":"<<rawError<<",\"raw_read_errors\":"<<rawReadErrors
            <<",\"camera_x\":"<<cameraX<<",\"camera_y\":"<<cameraY<<",\"clip_calls\":"<<clipCalls<<",\"clip_error\":"<<clipError<<",\"warp_calls\":"<<warpCalls<<",\"warp_error\":"<<warpError
            <<",\"clip\":["<<clip.left<<','<<clip.top<<','<<clip.right<<','<<clip.bottom<<"],\"cursor\":["<<cursor.x<<','<<cursor.y<<"],\"frames\":"<<frames<<"}\n";
        log.flush();
    }
    void openLog() {
        wchar_t exe[32768]{};GetModuleFileNameW(nullptr,exe,static_cast<DWORD>(std::size(exe)));
        auto folder=std::filesystem::path(exe).parent_path()/L"logs";
        std::filesystem::create_directories(folder);
        logPath=folder/(L"fullscreen-input-"+std::to_wstring(GetCurrentProcessId())+L".jsonl");
        log.open(logPath,std::ios::binary);
    }
    void unlock() {if(clipOwned){ClipCursor(nullptr);clipOwned=false;}locked=false;}
    void updatePointer() {
        locked=lockEnabled&&GetForegroundWindow()==window&&!IsIconic(window)&&!legacyAlt&&!rawAlt;
        if(!locked){unlock();return;}
        RECT client{};GetClientRect(window,&client);POINT center{client.right/2,client.bottom/2};ClientToScreen(window,&center);
        RECT clip{center.x,center.y,center.x+1,center.y+1};
        ++clipCalls;SetLastError(0);
        if(ClipCursor(&clip)){clipOwned=true;clipError=0;}else clipError=GetLastError();
        ++warpCalls;SetLastError(0);
        warpError=SetCursorPos(center.x,center.y)?0:GetLastError();
    }
    void rawInput(HRAWINPUT handle) {
        RAWINPUT input{};UINT bytes=sizeof(input);
        if(GetRawInputData(handle,RID_INPUT,&input,&bytes,sizeof(RAWINPUTHEADER))==static_cast<UINT>(-1)){++rawReadErrors;return;}
        bool foreground=GetForegroundWindow()==window;
        if(input.header.dwType==RIM_TYPEKEYBOARD) {
            ++rawKeyboard;
            auto key=input.data.keyboard.VKey;
            if(foreground&&(key==VK_MENU||key==VK_LMENU||key==VK_RMENU)) {
                bool down=(input.data.keyboard.Flags&RI_KEY_BREAK)==0;
                if(down!=rawAlt){rawAlt=down;if(down)++rawAltDown;else ++rawAltUp;record("raw_alt");}
            }
        } else if(input.header.dwType==RIM_TYPEMOUSE) {
            auto& mouse=input.data.mouse;
            if(mouse.usButtonFlags)++rawButtons;
            if(mouse.lLastX||mouse.lLastY)++rawMouse;
            if(foreground&&!legacyAlt&&!rawAlt&&!(mouse.usFlags&MOUSE_MOVE_ABSOLUTE)) {cameraX+=mouse.lLastX;cameraY+=mouse.lLastY;}
        }
    }
    HRESULT resize() {
        resizePending=false;RECT client{};GetClientRect(window,&client);
        if(!swap||IsIconic(window)||!client.right||!client.bottom)return S_FALSE;
        context->OMSetRenderTargets(0,nullptr,nullptr);target.Reset();surface.Reset();context->Flush();
        HRESULT hr=swap->ResizeBuffers(1,client.right,client.bottom,DXGI_FORMAT_B8G8R8A8_UNORM,
            DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH|DXGI_SWAP_CHAIN_FLAG_GDI_COMPATIBLE);
        if(SUCCEEDED(hr)) {
            ComPtr<ID3D11Texture2D> buffer;hr=swap->GetBuffer(0,IID_PPV_ARGS(&buffer));
            if(SUCCEEDED(hr))hr=device->CreateRenderTargetView(buffer.Get(),nullptr,&target);
            if(SUCCEEDED(hr))hr=buffer.As(&surface);
        }
        if(FAILED(hr))status=L"ResizeBuffers failed: "+std::to_wstring(static_cast<uint32_t>(hr));
        record("resize");return hr;
    }
    HRESULT initialize() {
        DXGI_SWAP_CHAIN_DESC desc{};desc.BufferDesc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count=1;desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.BufferCount=1;
        desc.OutputWindow=window;desc.Windowed=TRUE;desc.SwapEffect=DXGI_SWAP_EFFECT_DISCARD;
        desc.Flags=DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH|DXGI_SWAP_CHAIN_FLAG_GDI_COMPATIBLE;
        HRESULT hr=D3D11CreateDeviceAndSwapChain(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr,0,D3D11_SDK_VERSION,&desc,&swap,&device,nullptr,&context);
        if(FAILED(hr))return hr;
        ComPtr<IDXGIFactory> factory;hr=swap->GetParent(IID_PPV_ARGS(&factory));
        if(FAILED(hr))return hr;
        hr=factory->MakeWindowAssociation(window,DXGI_MWA_NO_ALT_ENTER);if(FAILED(hr))return hr;
        RAWINPUTDEVICE inputs[]={{1,6,0,window},{1,2,0,window}};
        rawRegistered=RegisterRawInputDevices(inputs,2,sizeof(RAWINPUTDEVICE))!=FALSE;rawError=rawRegistered?0:GetLastError();
        if(!RegisterHotKey(window,EmergencyHotkey,MOD_CONTROL|MOD_SHIFT|MOD_NOREPEAT,VK_F12))return HRESULT_FROM_WIN32(GetLastError());
        GetWindowRect(window,&savedRect);return resize();
    }
    void enterExclusive() {
        exclusivePending=false;if(!swap||GetForegroundWindow()!=window)return;
        BOOL full=FALSE;swap->GetFullscreenState(&full,nullptr);if(full)return;
        MONITORINFO monitor{sizeof(monitor)};GetMonitorInfoW(MonitorFromWindow(window,MONITOR_DEFAULTTONEAREST),&monitor);
        SetWindowLongPtrW(window,GWL_STYLE,WS_POPUP|WS_VISIBLE);
        SetWindowPos(window,HWND_NOTOPMOST,monitor.rcMonitor.left,monitor.rcMonitor.top,
            monitor.rcMonitor.right-monitor.rcMonitor.left,monitor.rcMonitor.bottom-monitor.rcMonitor.top,SWP_FRAMECHANGED|SWP_NOACTIVATE);
        // SetFullscreenState selects its mode from the swap-chain buffer size.
        // Update that size before requesting fullscreen to retain the desktop's
        // current resolution instead of selecting the initial window's size.
        HRESULT sized=resize();
        if(FAILED(sized)){exclusiveResult=sized;status=L"全屏缓冲区尺寸设置失败；F9 重试。";record("exclusive_resize_failed");return;}
        exclusiveResult=swap->SetFullscreenState(TRUE,nullptr);swap->GetFullscreenState(&full,nullptr);
        status=SUCCEEDED(exclusiveResult)&&full?L"DXGI TRUE；传统独占还需对照 PresentMon 分类。":L"DXGI 全屏申请未成功；F9 重试。";
        resizePending=true;record("enter_exclusive");
    }
    void restoreWindow() {
        windowed=true;exclusivePending=false;suspendPending=false;unlock();if(swap)swap->SetFullscreenState(FALSE,nullptr);
        SetWindowLongPtrW(window,GWL_STYLE,WS_OVERLAPPEDWINDOW|WS_VISIBLE);
        SetWindowPos(window,HWND_NOTOPMOST,savedRect.left,savedRect.top,savedRect.right-savedRect.left,savedRect.bottom-savedRect.top,SWP_FRAMECHANGED|SWP_NOACTIVATE);
        resizePending=true;status=L"已恢复窗口；F9 再次申请独占，Esc 关闭。";record("restore_window");
    }
    void selectCase(bool fullscreen,bool capture) {
        unlock();lockEnabled=capture;++caseId;
        if(fullscreen) {windowed=false;exclusivePending=true;status=L"等待前台申请独占。";}
        else restoreWindow();
        record("case_selected");
    }
    void suspendExclusive() {
        suspendPending=false;
        if(swap&&!windowed) {exclusiveResult=swap->SetFullscreenState(FALSE,nullptr);record("exclusive_suspended_on_deactivation");}
    }
    void observeForeground() {
        bool foreground=GetForegroundWindow()==window;
        if(foreground==wasForeground)return;
        wasForeground=foreground;
        if(foreground){if(!windowed)exclusivePending=true;}
        else{legacyAlt=rawAlt=false;unlock();if(!windowed)suspendPending=true;}
        record(foreground?"foreground_gained":"foreground_lost_reset");
    }
    void render() {
        if(resizePending)resize();if(!target||!surface||IsIconic(window)){Sleep(10);return;}
        const float color[]={0.025f,0.075f,0.085f,1};context->ClearRenderTargetView(target.Get(),color);context->Flush();
        HDC dc{};HRESULT hr=surface->GetDC(FALSE,&dc);
        if(SUCCEEDED(hr)) {
            RECT area{};GetClientRect(window,&area);BOOL full=FALSE;swap->GetFullscreenState(&full,nullptr);
            SelectObject(dc,font);SetBkMode(dc,TRANSPARENT);SetTextColor(dc,full?RGB(215,245,230):RGB(255,190,100));
            std::wostringstream text;
            text<<L"FloatCanvas Fullscreen Relative Input Fixture\n\n"
                <<L"DXGI 全屏："<<(full?L"TRUE":L"FALSE")<<L"    前台："<<(GetForegroundWindow()==window?L"TRUE":L"FALSE")
                <<L"    分辨率："<<area.right<<L" × "<<area.bottom<<L"\n"
                <<L"普通权限："<<(!elevated?L"是":L"否")<<L"    Integrity RID："<<integrityRid<<L"\n\n"
                <<L"对照 #"<<caseId<<L"："<<(windowed?L"窗口":L"独占全屏请求")<<L" + "<<(lockEnabled?L"锁鼠标":L"不锁鼠标")<<L"\n"
                <<L"F5 窗口不锁鼠    F6 窗口锁鼠    F7 独占不锁鼠    F8 独占锁鼠\n"
                <<L"锁鼠模式下按住 Alt 解锁，松开后恢复；四种模式使用同一输入逻辑。\n"
                <<L"Alt 只由收到的按下／松开更新，不根据物理状态自动纠正。\n"
                <<L"使用现有浮绘快捷键或头像进入；鼠标／快捷键退出后观察恢复。\n"
                <<L"F9 重试独占    Esc 恢复窗口且解锁，再按关闭    Ctrl+Shift+F12 随时关闭本测试\n\n"
                <<L"收到 Alt（WM / Raw）："<<legacyAlt<<L" / "<<rawAlt<<L"    本进程读到物理 Alt："<<(GetAsyncKeyState(VK_MENU)<0)<<L"\n"
                <<L"WM Alt 按下／松开："<<legacyAltDown<<L" / "<<legacyAltUp<<L"    Raw Alt："<<rawAltDown<<L" / "<<rawAltUp<<L"\n"
                <<L"锁定请求："<<locked<<L"    Raw 鼠标移动："<<rawMouse<<L"    视角："<<cameraX<<L", "<<cameraY<<L"\n"
                <<L"键盘 WM / Raw："<<keys<<L" / "<<rawKeyboard<<L"    鼠标按键 WM / Raw："<<clicks<<L" / "<<rawButtons<<L"\n"
                <<L"Raw 注册："<<rawRegistered<<L"    读取错误："<<rawReadErrors<<L"    Clip/SetCursorPos 错误："<<clipError<<L" / "<<warpError<<L"\n"
                <<L"Present 次数："<<frames<<L"    HRESULT：0x"<<std::hex<<static_cast<uint32_t>(presentResult)<<std::dec<<L"\n\n"
                <<status<<L"\n\nDXGI TRUE 是接口全屏状态；严格硬件呈现路径需另行 ETW 验证。\n日志："<<logPath.wstring();
            auto value=text.str();RECT content{32,26,area.right-28,area.bottom-90};
            DrawTextW(dc,value.c_str(),static_cast<int>(value.size()),&content,DT_LEFT|DT_TOP|DT_NOPREFIX|DT_WORDBREAK);
            int x=static_cast<int>((cameraX%1600+1600)%1600),y=static_cast<int>((cameraY%150+150)%150);
            HBRUSH brush=CreateSolidBrush(RGB(80,220,170));RECT marker{40+x,area.bottom-180+y,80+x,area.bottom-160+y};FillRect(dc,&marker,brush);DeleteObject(brush);
            surface->ReleaseDC(nullptr);
        }
        presentResult=swap->Present(1,0);++frames;
        if(presentResult==DXGI_STATUS_OCCLUDED)Sleep(10);
        if(FAILED(presentResult)){record("present_failed");quit=true;}
    }
    void close() {UnregisterHotKey(window,EmergencyHotkey);unlock();if(swap)swap->SetFullscreenState(FALSE,nullptr);record("closed");if(font)DeleteObject(font);}
};

static LRESULT CALLBACK Procedure(HWND window,UINT message,WPARAM wp,LPARAM lp) {
    auto f=reinterpret_cast<Fixture*>(GetWindowLongPtrW(window,GWLP_USERDATA));
    if(message==WM_NCCREATE){f=static_cast<Fixture*>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams);f->window=window;SetWindowLongPtrW(window,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(f));}
    if(!f)return DefWindowProcW(window,message,wp,lp);
    switch(message) {
    case WM_ACTIVATEAPP:
        // Local activation can change during delegation while the real global
        // foreground remains ours. Only observeForeground changes game state.
        f->record(wp?"wm_activateapp_true":"wm_activateapp_false");break;
    case WM_SIZE:f->resizePending=true;f->record(wp==SIZE_MINIMIZED?"minimized":"size");return 0;
    case WM_HOTKEY:if(wp==Fixture::EmergencyHotkey){f->record("emergency_exit");f->quit=true;return 0;}break;
    case WM_SYSKEYDOWN:case WM_SYSKEYUP:case WM_KEYDOWN:case WM_KEYUP: {
        bool down=message==WM_KEYDOWN||message==WM_SYSKEYDOWN;
        if(down)++f->keys;
        if(wp==VK_MENU) {if(down!=f->legacyAlt){f->legacyAlt=down;if(down)++f->legacyAltDown;else ++f->legacyAltUp;f->record("legacy_alt");}return 0;}
        if(down&&!(lp&(1LL<<30))) {
            if(wp>=VK_F5&&wp<=VK_F8){f->selectCase(wp>=VK_F7,wp==VK_F6||wp==VK_F8);return 0;}
            if(wp==VK_ESCAPE){if(!f->windowed||f->lockEnabled)f->selectCase(false,false);else f->quit=true;return 0;}
            if(wp==VK_F9){f->windowed=false;f->exclusivePending=true;return 0;}
        }
        break;
    }
    case WM_INPUT:f->rawInput(reinterpret_cast<HRAWINPUT>(lp));return DefWindowProcW(window,message,wp,lp);
    case WM_LBUTTONDOWN:case WM_RBUTTONDOWN:case WM_MBUTTONDOWN:++f->clicks;return 0;
    case WM_ERASEBKGND:return 1;
    case WM_CLOSE:f->quit=true;return 0;
    case WM_DESTROY:PostQuitMessage(0);return 0;
    }
    return DefWindowProcW(window,message,wp,lp);
}

int WINAPI wWinMain(HINSTANCE instance,HINSTANCE,PWSTR,int show) {
    Fixture f;int argc{};auto args=CommandLineToArgvW(GetCommandLineW(),&argc);
    if(argc==3&&std::wstring(args[1])==L"--seconds") {wchar_t* end{};f.seconds=wcstod(args[2],&end);if(!end||*end||f.seconds<=0){LocalFree(args);return 2;}}
    else if(argc!=1){LocalFree(args);return 2;}LocalFree(args);
    f.readToken();try{f.openLog();}catch(...){return 3;}if(!f.log)return 3;
    WNDCLASSW wc{};wc.lpfnWndProc=Procedure;wc.hInstance=instance;wc.lpszClassName=L"FloatCanvas.FullscreenInputFixture";wc.hCursor=LoadCursorW(nullptr,IDC_ARROW);
    if(!RegisterClassW(&wc))return 4;
    auto window=CreateWindowExW(0,wc.lpszClassName,L"FloatCanvas Fullscreen Relative Input Fixture",WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT,CW_USEDEFAULT,1280,820,nullptr,nullptr,instance,&f);
    if(!window)return 5;
    f.font=CreateFontW(-26,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Microsoft YaHei UI");
    ShowWindow(window,show);UpdateWindow(window);HRESULT hr=f.initialize();f.record("startup");
    if(FAILED(hr)){f.close();DestroyWindow(window);return 6;}
    MSG message{};
    while(!f.quit) {
        while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){if(message.message==WM_QUIT){f.quit=true;break;}TranslateMessage(&message);DispatchMessageW(&message);}
        if(f.seconds>0&&std::chrono::duration<double>(Clock::now()-f.start).count()>=f.seconds)f.quit=true;
        if(f.quit)break;
        f.observeForeground();
        if(f.suspendPending)f.suspendExclusive();
        if(f.exclusivePending)f.enterExclusive();
        f.updatePointer();f.render();
        if(Clock::now()-f.lastSample>=std::chrono::milliseconds(250)){f.lastSample=Clock::now();f.record("sample");}
    }
    f.close();DestroyWindow(window);return FAILED(f.presentResult)?7:0;
}
