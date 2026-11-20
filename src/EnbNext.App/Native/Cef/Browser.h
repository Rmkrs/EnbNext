#pragma once
#include <atomic>
#include <memory>
#include <string>
#include <chrono>
#include "include/capi/cef_app_capi.h"
#include "include/capi/cef_client_capi.h"
#include "include/capi/cef_browser_capi.h"
#include "include/capi/cef_request_context_capi.h"
#include "include/cef_api_hash.h"
#ifdef _WIN32
#define ENB_EXPORT extern "C" __declspec(dllexport)
#else
#define ENB_EXPORT extern "C" __attribute__((visibility("default")))
#endif
struct Text {
 cef_string_t value{};
 explicit Text(const char* s){if(s)cef_string_utf8_to_utf16(s,strlen(s),&value);}
 ~Text(){cef_string_utf16_clear(&value);}
};
inline std::string Utf8(const cef_string_t* s){cef_string_utf8_t t{};if(s)cef_string_utf16_to_utf8(s->str,s->length,&t);std::string r(t.str?t.str:"");cef_string_utf8_clear(&t);return r;}
template<class T> void Drop(T* p){if(p){auto base=reinterpret_cast<cef_base_ref_counted_t*>(p);base->release(base);}}
template<class T> void Retain(T* p){auto base=reinterpret_cast<cef_base_ref_counted_t*>(p);base->add_ref(base);}
template<class T> struct Received {T* value;~Received(){Drop(value);}};
template<class T,class Owner> struct Handler {
 T api{};std::atomic<int> refs{1};std::shared_ptr<Owner> owner;
 explicit Handler(std::shared_ptr<Owner> o):owner(std::move(o)){
  api.base.size=sizeof(T);
  api.base.add_ref=[](cef_base_ref_counted_t* p){++reinterpret_cast<Handler*>(p)->refs;};
  api.base.release=[](cef_base_ref_counted_t* p)->int{auto h=reinterpret_cast<Handler*>(p);if(--h->refs==0){delete h;return 1;}return 0;};
  api.base.has_one_ref=[](cef_base_ref_counted_t* p)->int{return reinterpret_cast<Handler*>(p)->refs==1;};
  api.base.has_at_least_one_ref=[](cef_base_ref_counted_t* p)->int{return reinterpret_cast<Handler*>(p)->refs>0;};
 }
 static Owner& Get(T* p){return *reinterpret_cast<Handler*>(p)->owner;}
};
using Event= int(*)(void*,int,const char*);
struct Frame {unsigned texture=0;int width=0,height=0;unsigned long long serial=0;};
struct Transfer {
 virtual ~Transfer()=default;
 virtual void Copy(Frame&,const cef_accelerated_paint_info_t&)=0;
 virtual void Release(Frame&)=0;
};
using Resolve=void*(*)(const char*);
std::unique_ptr<Transfer> MakeTransfer(Resolve);
