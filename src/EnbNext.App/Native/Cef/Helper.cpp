#ifdef _WIN32
#include <windows.h>
#endif
#include "include/capi/cef_app_capi.h"
#include "include/cef_api_hash.h"
#include <cstring>
#ifdef _WIN32
int WINAPI wWinMain(HINSTANCE instance,HINSTANCE,wchar_t*,int){cef_main_args_t args{};args.instance=instance;
#else
int main(int argc,char** argv){cef_main_args_t args{};args.argc=argc;args.argv=argv;
#endif
 if(strcmp(cef_api_hash(15000,0),CEF_API_HASH_PLATFORM)!=0)return 2;
 return cef_execute_process(&args,nullptr,nullptr);
}
