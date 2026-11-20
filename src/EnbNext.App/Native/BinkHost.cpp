// Isolated x86 bridge to the user's installed codec. No codec is redistributed.
// Retail A162F0/A16870: Open(0), Wait, DoFrame, RGB565 copy, NextFrame.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mmsystem.h>
#include <dsound.h>
#include <mmdeviceapi.h>
#include <audiopolicy.h>
#include <cstdint>
#include <cstdlib>
#include <cstdio>
#include <vector>

// Bink's automatic DirectSound setup searches for a window owned by this
// process (30008374..3000841D). A hidden console helper has no such window.
// Supply an explicitly initialized, cooperative device instead. The borrowed
// device path skips Bink's primary-buffer format/priority setup and Bink leaves
// its release to us (30008258, 3000842E, 30008EE1).
struct MovieAudio {
    HWND window=nullptr;
    IDirectSound* device=nullptr;
    HRESULT Open() {
        WNDCLASSW wc{}; wc.lpfnWndProc=DefWindowProcW;
        wc.hInstance=GetModuleHandleW(nullptr); wc.lpszClassName=L"EnbNextIntroAudio";
        if(!RegisterClassW(&wc))return HRESULT_FROM_WIN32(GetLastError());
        window=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,wc.lpszClassName,
            L"EnbNext intro",WS_OVERLAPPED,0,0,0,0,nullptr,nullptr,wc.hInstance,nullptr);
        if(!window)return HRESULT_FROM_WIN32(GetLastError());
        HRESULT result=DirectSoundCreate(nullptr,&device,nullptr);
        if(FAILED(result))return result;
        return device->SetCooperativeLevel(window,DSSCL_NORMAL);
    }
    ~MovieAudio() { if(device)device->Release();if(window)DestroyWindow(window); }
};

// The intro follows the music volume through this process's own audio session (the
// helper is its own process, so nothing else is affected): the level holds whatever
// Bink's buffers on the borrowed device make of BinkSetVolume, which stays normal.
struct SessionVolume {
    ISimpleAudioVolume* volume=nullptr;
    bool com=false;
    void Open() {
        com=SUCCEEDED(CoInitializeEx(nullptr,COINIT_MULTITHREADED));
        IMMDeviceEnumerator* devices=nullptr; IMMDevice* device=nullptr; IAudioSessionManager* sessions=nullptr;
        if(SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&devices)))
            && SUCCEEDED(devices->GetDefaultAudioEndpoint(eRender,eConsole,&device))
            && SUCCEEDED(device->Activate(__uuidof(IAudioSessionManager),CLSCTX_ALL,nullptr,reinterpret_cast<void**>(&sessions))))
            sessions->GetSimpleAudioVolume(nullptr,FALSE,&volume);
        if(sessions)sessions->Release(); if(device)device->Release(); if(devices)devices->Release();
    }
    void Set(float level,bool muted) { if(volume){volume->SetMasterVolume(level,nullptr);volume->SetMute(muted,nullptr);} }
    ~SessionVolume() { if(volume)volume->Release(); if(com)CoUninitialize(); }
};

struct BinkHeader { uint32_t width, height, frames, frame, last, rate, divisor; };
struct Reply { uint32_t magic=0x314b4245, status=3, width=0, height=0, frames=0, frame=0, rate=0, divisor=0, bytes=0; };
static bool Write(const void* data, DWORD count) {
    const auto* p=static_cast<const BYTE*>(data);
    while(count) { DWORD n=0; if(!WriteFile(GetStdHandle(STD_OUTPUT_HANDLE),p,count,&n,nullptr)||!n)return false; p+=n;count-=n; }
    return true;
}
template<class T> T Export(HMODULE library,const char* name) { return reinterpret_cast<T>(GetProcAddress(library,name)); }
int wmain(int argc,wchar_t** argv) {
    if(argc!=5&&argc!=6)return 2;
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    HMODULE library=LoadLibraryExW(argv[1],nullptr,LOAD_WITH_ALTERED_SEARCH_PATH);
    Reply reply;
    if(!library){Write(&reply,sizeof(reply));return 3;}
    auto open=Export<BinkHeader*(__stdcall*)(const char*,uint32_t)>(library,"_BinkOpen@8");
    auto close=Export<void(__stdcall*)(BinkHeader*)>(library,"_BinkClose@4");
    auto wait=Export<int(__stdcall*)(BinkHeader*)>(library,"_BinkWait@4");
    auto decode=Export<int(__stdcall*)(BinkHeader*)>(library,"_BinkDoFrame@4");
    auto next=Export<void(__stdcall*)(BinkHeader*)>(library,"_BinkNextFrame@4");
    auto copy=Export<int(__stdcall*)(BinkHeader*,void*,int,uint32_t,uint32_t,uint32_t,uint32_t)>(library,"_BinkCopyToBuffer@28");
    auto volume=Export<void(__stdcall*)(BinkHeader*,int)>(library,"_BinkSetVolume@8");
    auto sound=Export<int(__stdcall*)(void*,uint32_t)>(library,"_BinkSetSoundSystem@8");
    if(!open||!close||!wait||!decode||!next||!copy||!volume||!sound){Write(&reply,sizeof(reply));FreeLibrary(library);return 4;}
    MovieAudio audio;
    // Fresh process: 30044F34 is zero until SetSoundSystem publishes a callback.
    // BinkOpen 30004BE9 skips device creation when it is zero. Silent runs never
    // call a sound-system initializer or create a device/window.
    if(wcstoul(argv[3],nullptr,10)!=0) {
        auto direct=GetProcAddress(library,"_BinkOpenDirectSound@4");
        HRESULT result=direct?audio.Open():E_NOINTERFACE;
        if(FAILED(result) || !sound(reinterpret_cast<void*>(direct),reinterpret_cast<uint32_t>(audio.device)))
            fprintf(stderr,"Intro audio unavailable (0x%08lX); continuing video.\n",static_cast<unsigned long>(FAILED(result)?result:E_FAIL));
    }
    // The requested level is Bink's scale (0x8000 normal): the session takes it as a fraction.
    int requestedVolume=static_cast<int>(wcstol(argv[4],nullptr,10));
    float level=requestedVolume<=0?0.0f:requestedVolume>=0x8000?1.0f:requestedVolume/32768.0f;
    bool muted=argc==6&&wcstol(argv[5],nullptr,10)!=0;
    SessionVolume session; session.Open(); session.Set(level,muted); // before the movie (and its sound) opens
    int size=WideCharToMultiByte(CP_ACP,WC_NO_BEST_FIT_CHARS,argv[2],-1,nullptr,0,nullptr,nullptr);
    std::vector<char> path(size); BOOL replaced=FALSE;
    WideCharToMultiByte(CP_ACP,WC_NO_BEST_FIT_CHARS,argv[2],-1,path.data(),size,nullptr,&replaced);
    BinkHeader* movie=replaced?nullptr:open(path.data(),0);
    if(!movie){Write(&reply,sizeof(reply));FreeLibrary(library);return 0;}
    uint64_t bytes=uint64_t(movie->width)*movie->height*2;
    if(!movie->width||!movie->height||!movie->rate||!movie->divisor||bytes>UINT32_MAX){close(movie);Write(&reply,sizeof(reply));FreeLibrary(library);return 6;}
    std::vector<BYTE> pixels;
    try { pixels.resize(static_cast<size_t>(bytes)); } catch(...) {close(movie);Write(&reply,sizeof(reply));FreeLibrary(library);return 7;}
    volume(movie,muted?0:0x8000);
    auto metadata=[&] {reply.width=movie->width;reply.height=movie->height;reply.frames=movie->frames;reply.frame=movie->frame;reply.rate=movie->rate;reply.divisor=movie->divisor;};
    reply.status=0;metadata();bool connected=Write(&reply,sizeof(reply)), first=true;
    while(connected) {
        BYTE command=0;DWORD read=0;
        if(!ReadFile(GetStdHandle(STD_INPUT_HANDLE),&command,1,&read,nullptr)||read!=1||(command!='P'&&command!='M'))break;
        bool nextMuted=command=='M';
        if(nextMuted!=muted){muted=nextMuted;session.Set(level,muted);volume(movie,muted?0:0x8000);}
        reply.bytes=0;reply.status=0;
        if(movie->frame>=movie->frames)reply.status=2;
        else if(wait(movie)==0||first) {
            first=false;decode(movie);
            copy(movie,pixels.data(),movie->width*2,movie->height,0,0,0x7000000a);
            if(movie->frame<movie->frames)next(movie);
            reply.status=1;reply.bytes=static_cast<uint32_t>(bytes);
        }
        metadata();connected=Write(&reply,sizeof(reply));
        if(connected&&reply.bytes)connected=Write(pixels.data(),reply.bytes);
    }
    close(movie);FreeLibrary(library);return 0;
}
