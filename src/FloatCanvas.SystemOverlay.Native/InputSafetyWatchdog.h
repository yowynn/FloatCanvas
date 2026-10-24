#pragma once
#include <windows.h>
#include <atomic>

// Watches only the user's explicit emergency release chord. No health deadline
// may terminate the application: Windows and the user own unresponsive-app handling.
class InputSafetyWatchdog final {
public:
    static constexpr UINT EmergencyMessage = WM_APP + 0x463;
    InputSafetyWatchdog() = default;
    InputSafetyWatchdog(const InputSafetyWatchdog&) = delete;
    ~InputSafetyWatchdog() { Stop(); }
    bool Configure(bool value) {if(thread||armed.load())return false;enabled=value;return true;}
    bool IsEnabled() const {return enabled;}
    bool Start(HWND owner) {
        if(!enabled)return true;
        if(thread)return true;
        window=owner;
        stop=CreateEventW(nullptr,TRUE,FALSE,nullptr);
        if(!stop)return false;
        thread=CreateThread(nullptr,0,Run,this,0,nullptr);
        if(thread)return true;
        CloseHandle(stop);stop=nullptr;return false;
    }
    void Arm() {if(enabled){emergencyDown=false;armed=true;}}
    void Disarm() {armed=false;}
    void Stop() {
        if(thread) {
            Disarm();SetEvent(stop);WaitForSingleObject(thread,INFINITE);
            CloseHandle(thread);CloseHandle(stop);thread=stop=nullptr;
        }
    }
private:
    HANDLE stop{},thread{};
    HWND window{};
    bool enabled{true};
    std::atomic<bool> armed{},emergencyDown{};
    static DWORD WINAPI Run(void* value) {
        auto& self=*static_cast<InputSafetyWatchdog*>(value);
        while(WaitForSingleObject(self.stop,25)==WAIT_TIMEOUT) {
            if(!self.armed.load()){self.emergencyDown=false;continue;}
            bool chord=GetAsyncKeyState(VK_CONTROL)<0&&GetAsyncKeyState(VK_MENU)<0&&
                GetAsyncKeyState(VK_SHIFT)<0&&GetAsyncKeyState(VK_F12)<0;
            bool previous=self.emergencyDown.exchange(chord);
            if(chord&&!previous) {
                // The owner STA releases its own COM input lease when it can process
                // this request. A busy owner is never killed from this worker thread.
                PostMessageW(self.window,EmergencyMessage,0,0);
            }
        }
        return 0;
    }
};
