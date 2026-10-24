#pragma once
#include <windows.h>
#include <atomic>
#include <cstdio>

// No COM, SendInput or target-process access is permitted on this thread. If the
// owner STA cannot release its lease, terminating this process lets Windows tear
// down that lease. The existing persistent overlay marker survives termination.
class InputSafetyWatchdog final {
public:
    static constexpr UINT EmergencyMessage = WM_APP + 0x463;
    static constexpr DWORD TimeoutMilliseconds = 5000, EmergencyMilliseconds = 750;
    static constexpr UINT ExitCode = 0x46434957; // FCIW: input watchdog
    InputSafetyWatchdog() = default;
    InputSafetyWatchdog(const InputSafetyWatchdog&) = delete;
    ~InputSafetyWatchdog() { Stop(); }
    bool Start(HWND owner) {
        if(thread) return true;
        window=owner;
        stop=CreateEventW(nullptr,TRUE,FALSE,nullptr);
        if(!stop)return false;
        thread=CreateThread(nullptr,0,Run,this,0,nullptr);
        if(thread)return true;
        CloseHandle(stop);stop=nullptr;return false;
    }
    bool SetJournal(const wchar_t* path) {
        if(armed.load())return false;
        if(journal!=INVALID_HANDLE_VALUE)CloseHandle(journal);
        journal=CreateFileW(path,FILE_APPEND_DATA,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL|FILE_FLAG_OVERLAPPED|FILE_FLAG_WRITE_THROUGH,nullptr);
        if(journal==INVALID_HANDLE_VALUE)return false;
        if(!journalWrite.hEvent)journalWrite.hEvent=CreateEventW(nullptr,TRUE,FALSE,nullptr);
        journalWrite.Offset=journalWrite.OffsetHigh=0xffffffff;
        return journalWrite.hEvent!=nullptr;
    }
    void Arm() {beat=GetTickCount64();emergencyDeadline=0;emergencyCause=0;armed=true;}
    void Heartbeat() {beat=GetTickCount64();}
    void Disarm() {armed=false;emergencyDeadline=0;}
    void ReleaseFailed() {emergencyCause=2;emergencyDeadline=GetTickCount64()+EmergencyMilliseconds;}
    bool Expired(ULONGLONG now) const {
        auto deadline=emergencyDeadline.load();
        auto lastBeat=beat.load();
        return armed.load() && ((now>=lastBeat && now-lastBeat>=TimeoutMilliseconds) || (deadline!=0 && now>=deadline));
    }
    void Stop() {
        if(thread) {
            Disarm();SetEvent(stop);WaitForSingleObject(thread,INFINITE);
            CloseHandle(thread);CloseHandle(stop);thread=stop=nullptr;
        }
        if(journal!=INVALID_HANDLE_VALUE){CloseHandle(journal);journal=INVALID_HANDLE_VALUE;}
        if(journalWrite.hEvent){CloseHandle(journalWrite.hEvent);journalWrite.hEvent=nullptr;}
    }
private:
    HANDLE stop{},thread{};
    HANDLE journal{INVALID_HANDLE_VALUE};
    OVERLAPPED journalWrite{};
    char journalBuffer[192]{};
    HWND window{};
    std::atomic<bool> armed{};
    std::atomic<int> emergencyCause{};
    std::atomic<ULONGLONG> beat{},emergencyDeadline{};
    static DWORD WINAPI Run(void* value) {
        auto& self=*static_cast<InputSafetyWatchdog*>(value);
        while(WaitForSingleObject(self.stop,25)==WAIT_TIMEOUT) {
            if(!self.armed.load())continue;
            ULONGLONG now=GetTickCount64();
            if(self.emergencyDeadline.load()==0 && GetAsyncKeyState(VK_CONTROL)<0 &&
                GetAsyncKeyState(VK_MENU)<0 && GetAsyncKeyState(VK_SHIFT)<0 && GetAsyncKeyState(VK_F12)<0) {
                self.emergencyDeadline=now+EmergencyMilliseconds;
                self.emergencyCause=1;
                PostMessageW(self.window,EmergencyMessage,0,0);
            }
            if(self.Expired(now)) {
                if(self.journal!=INVALID_HANDLE_VALUE) {
                    FILETIME utc{};GetSystemTimeAsFileTime(&utc);
                    ULONGLONG ticks=(static_cast<ULONGLONG>(utc.dwHighDateTime)<<32)|utc.dwLowDateTime;
                    int size=std::snprintf(self.journalBuffer,sizeof(self.journalBuffer),
                        "{\"reason\":\"%s\",\"uptimeMs\":%llu,\"utcFileTime\":%llu,\"exitCode\":%u}\n",
                        self.emergencyCause.load()==1?"emergency-chord-timeout":self.emergencyCause.load()==2?"release-failure":"ui-heartbeat-timeout",now,ticks,ExitCode);
                    WriteFile(self.journal,self.journalBuffer,static_cast<DWORD>(size),nullptr,&self.journalWrite);
                    // An unavailable disk must never delay input release indefinitely.
                    WaitForSingleObject(self.journalWrite.hEvent,50);
                }
                TerminateProcess(GetCurrentProcess(),ExitCode);
            }
        }
        return 0;
    }
};
