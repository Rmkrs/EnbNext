#include <cstring>
#include <stdexcept>
#include <map>
#include <string>
#include "Browser.h"
#include "include/capi/cef_render_handler_capi.h"
#include "include/capi/cef_life_span_handler_capi.h"
#include "include/capi/cef_load_handler_capi.h"
#include "include/capi/cef_request_handler_capi.h"
#include "include/capi/cef_context_menu_handler_capi.h"
#include "include/capi/cef_browser_process_handler_capi.h"
#include "include/capi/cef_command_line_capi.h"
namespace {
struct Runtime {std::atomic<int64_t> due{0};std::string root;std::map<std::string,cef_request_context_t*> profiles;~Runtime(){for(auto& item:profiles)Drop(item.second);}};
std::shared_ptr<Runtime> runtime;
int64_t Now(){return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();}
struct Page {
 cef_browser_t* browser=nullptr;Event event;void* tag;int width=1,height=1;float scale=1;bool visible=false,closing=false,closeSent=false,closed=false,popup=false,silent=false,loadFailed=false;
 cef_rect_t popupRect{};Frame frames[2];Resolve resolve;std::unique_ptr<Transfer> transfer;
 Page(Event e,void* t,Resolve resolver):event(e),tag(t),resolve(resolver),transfer(MakeTransfer(resolver)){}
 ~Page(){Drop(browser);if(transfer)for(auto& f:frames)transfer->Release(f);}
 int Emit(int kind,const char* text=""){return !closing&&event?event(tag,kind,text):0;}
};
using PageHandle=std::shared_ptr<Page>;
cef_render_handler_t* Render(PageHandle p){
 auto h=new Handler<cef_render_handler_t,Page>(p);auto& a=h->api;
 a.get_view_rect=[](cef_render_handler_t* s,cef_browser_t* b,cef_rect_t* r){Received<cef_browser_t> received{b};auto& p=Handler<cef_render_handler_t,Page>::Get(s);*r={0,0,p.width,p.height};};
 a.get_screen_info=[](cef_render_handler_t* s,cef_browser_t* b,cef_screen_info_t* i)->int{Received<cef_browser_t> received{b};auto& p=Handler<cef_render_handler_t,Page>::Get(s);i->device_scale_factor=p.scale;i->depth=32;i->depth_per_component=8;i->rect=i->available_rect={0,0,p.width,p.height};return 1;};
 a.on_popup_show=[](cef_render_handler_t* s,cef_browser_t* b,int show){Received<cef_browser_t> received{b};Handler<cef_render_handler_t,Page>::Get(s).popup=show!=0;};
 a.on_popup_size=[](cef_render_handler_t* s,cef_browser_t* b,const cef_rect_t* r){Received<cef_browser_t> received{b};Handler<cef_render_handler_t,Page>::Get(s).popupRect=*r;};
 a.on_paint=[](cef_render_handler_t* s,cef_browser_t* b,cef_paint_element_type_t,size_t,const cef_rect_t*,const void*,int,int){Received<cef_browser_t> received{b};Handler<cef_render_handler_t,Page>::Get(s).Emit(2,"CEF did not provide accelerated painting");};
 a.on_accelerated_paint=[](cef_render_handler_t* s,cef_browser_t* b,cef_paint_element_type_t type,size_t,const cef_rect_t*,const cef_accelerated_paint_info_t* info){Received<cef_browser_t> received{b};auto& p=Handler<cef_render_handler_t,Page>::Get(s);if(p.closing)return;try{if(!p.transfer)p.transfer=MakeTransfer(p.resolve);p.transfer->Copy(p.frames[type==PET_POPUP?1:0],*info);}catch(const std::exception& e){p.Emit(2,e.what());}};
 return &h->api;
}
cef_life_span_handler_t* Life(PageHandle p){
 auto h=new Handler<cef_life_span_handler_t,Page>(p);auto& a=h->api;
 a.on_after_created=[](cef_life_span_handler_t* s,cef_browser_t* b){auto& p=Handler<cef_life_span_handler_t,Page>::Get(s);p.browser=b;auto host=b->get_host(b);host->set_audio_muted(host,p.silent);host->was_hidden(host,!p.visible);Drop(host);};
 a.on_before_close=[](cef_life_span_handler_t* s,cef_browser_t* b){Received<cef_browser_t> received{b};auto& p=Handler<cef_life_span_handler_t,Page>::Get(s);p.Emit(3);p.closed=true;Drop(p.browser);p.browser=nullptr;};
 a.do_close=[](cef_life_span_handler_t*,cef_browser_t* b)->int{Received<cef_browser_t> received{b};return 0;};
 a.on_before_popup=[](cef_life_span_handler_t* s,cef_browser_t* b,cef_frame_t* f,int,const cef_string_t* url,const cef_string_t*,cef_window_open_disposition_t,int,const cef_popup_features_t*,cef_window_info_t*,cef_client_t**,cef_browser_settings_t*,cef_dictionary_value_t**,int*)->int{Received<cef_browser_t> received{b};Received<cef_frame_t> frame{f};auto& p=Handler<cef_life_span_handler_t,Page>::Get(s);auto u=Utf8(url);p.Emit(5,u.c_str());return 1;};
 return &h->api;
}
cef_load_handler_t* Load(PageHandle p){auto h=new Handler<cef_load_handler_t,Page>(p);
 // A new main-frame load (a reload after a failure) may succeed again.
 h->api.on_load_start=[](cef_load_handler_t* s,cef_browser_t* b,cef_frame_t* f,cef_transition_type_t){Received<cef_browser_t> received{b};Received<cef_frame_t> frame{f};if(f->is_main(f))Handler<cef_load_handler_t,Page>::Get(s).loadFailed=false;};
 h->api.on_load_end=[](cef_load_handler_t* s,cef_browser_t* b,cef_frame_t* f,int){Received<cef_browser_t> received{b};Received<cef_frame_t> frame{f};auto& p=Handler<cef_load_handler_t,Page>::Get(s);if(f->is_main(f)&&!p.loadFailed)p.Emit(1);};
 h->api.on_load_error=[](cef_load_handler_t* s,cef_browser_t* b,cef_frame_t* f,cef_errorcode_t e,const cef_string_t*,const cef_string_t*){Received<cef_browser_t> received{b};Received<cef_frame_t> frame{f};if(f->is_main(f)&&e!=ERR_ABORTED){auto& p=Handler<cef_load_handler_t,Page>::Get(s);p.loadFailed=true;std::string text="The web page could not be loaded (network error "+std::to_string(static_cast<int>(e))+").";p.Emit(2,text.c_str());}};return &h->api;
}
cef_request_handler_t* Request(PageHandle p){auto h=new Handler<cef_request_handler_t,Page>(p);
 h->api.on_render_process_terminated=[](cef_request_handler_t* s,cef_browser_t* b,cef_termination_status_t,int,const cef_string_t*){Received<cef_browser_t> received{b};Handler<cef_request_handler_t,Page>::Get(s).Emit(2,"The browser renderer stopped.");};
 h->api.on_before_browse=[](cef_request_handler_t* s,cef_browser_t* b,cef_frame_t* f,cef_request_t* r,int,int)->int{Received<cef_browser_t> received{b};Received<cef_frame_t> frame{f};Received<cef_request_t> request{r};auto u=r->get_url(r);auto text=Utf8(u);cef_string_userfree_free(u);auto& p=Handler<cef_request_handler_t,Page>::Get(s);int cancel=p.Emit(4,text.c_str());if(!cancel&&f->is_main(f))p.loadFailed=false;return cancel;};return &h->api;
}
cef_client_t* Client(PageHandle p){auto h=new Handler<cef_client_t,Page>(p);
 h->api.get_render_handler=[](cef_client_t* s){return Render(reinterpret_cast<Handler<cef_client_t,Page>*>(s)->owner);};
 h->api.get_life_span_handler=[](cef_client_t* s){return Life(reinterpret_cast<Handler<cef_client_t,Page>*>(s)->owner);};
 h->api.get_load_handler=[](cef_client_t* s){return Load(reinterpret_cast<Handler<cef_client_t,Page>*>(s)->owner);};
 h->api.get_request_handler=[](cef_client_t* s){return Request(reinterpret_cast<Handler<cef_client_t,Page>*>(s)->owner);};
 h->api.get_context_menu_handler=[](cef_client_t* s){auto m=new Handler<cef_context_menu_handler_t,Page>(reinterpret_cast<Handler<cef_client_t,Page>*>(s)->owner);m->api.on_before_context_menu=[](cef_context_menu_handler_t*,cef_browser_t* b,cef_frame_t* f,cef_context_menu_params_t* p,cef_menu_model_t* model){Received<cef_browser_t> received{b};Received<cef_frame_t> frame{f};Received<cef_context_menu_params_t> params{p};Received<cef_menu_model_t> menu{model};model->clear(model);};return &m->api;};return &h->api;
}
cef_app_t* App(){auto h=new Handler<cef_app_t,Runtime>(runtime);
 h->api.get_browser_process_handler=[](cef_app_t* s){auto b=new Handler<cef_browser_process_handler_t,Runtime>(reinterpret_cast<Handler<cef_app_t,Runtime>*>(s)->owner);b->api.on_schedule_message_pump_work=[](cef_browser_process_handler_t* s,int64_t delay){Handler<cef_browser_process_handler_t,Runtime>::Get(s).due=Now()+std::max<int64_t>(delay,0);};return &b->api;};return &h->api;
}
}
ENB_EXPORT int enb_cef_initialize(const char* helper,const char* resources,const char* cache){
 if(runtime)return 1;
 if(strcmp(cef_api_hash(15000,0),CEF_API_HASH_PLATFORM)!=0)return 0;
 cef_main_args_t args{};
#ifdef _WIN32
 args.instance=GetModuleHandle(nullptr);
#else
 char name[]="EnbNext";char* argv[]={name,nullptr};args.argc=1;args.argv=argv;
#endif
 cef_settings_t settings{};settings.size=sizeof(settings);settings.windowless_rendering_enabled=1;settings.external_message_pump=1;
 // Chromium's own diagnostics (extension registry, USB enumeration, ...) are not
 // game output: keep them off the console and out of a debug.log file.
 settings.log_severity=LOGSEVERITY_DISABLE;
 // No Windows sandbox: the game is an ordinary
 // executable and Chromium's subprocesses run the separate helper on every
 // platform. Linux keeps Chromium's own platform sandbox.
#ifdef _WIN32
 settings.no_sandbox=1;
#endif
 Text path(helper),root(resources),profile(cache);
 settings.browser_subprocess_path=path.value;
 settings.resources_dir_path=root.value;settings.root_cache_path=profile.value;
 runtime=std::make_shared<Runtime>();runtime->root=cache;auto app=App();int ok=cef_initialize(&args,&settings,app,nullptr);if(!ok)runtime.reset();return ok;
}
ENB_EXPORT void enb_cef_pump(){if(!runtime)return;auto scheduled=runtime->due.load();if(scheduled<=Now()&&runtime->due.compare_exchange_strong(scheduled,INT64_MAX)){cef_do_message_loop_work();
 // CEF tests/shared/browser/main_message_loop_external_pump.cc bounds idle
 // wakeups to 1000/30 ms; scheduled callbacks can request earlier work.
 auto due=runtime->due.load();auto latest=Now()+1000/30;while(due>latest&&!runtime->due.compare_exchange_weak(due,latest)){}}}
ENB_EXPORT void enb_cef_shutdown(){if(runtime){for(auto& item:runtime->profiles)Drop(item.second);runtime->profiles.clear();cef_shutdown();runtime.reset();}}
ENB_EXPORT void* enb_cef_create(const char* url,const char* profile,Event event,void* tag,Resolve resolver,void* parent,int silent){
 try{auto p=std::make_shared<Page>(event,tag,resolver);p->silent=silent!=0;auto client=Client(p);cef_window_info_t window{};window.size=sizeof(window);window.windowless_rendering_enabled=1;window.shared_texture_enabled=1;
#ifdef _WIN32
 window.parent_window=static_cast<HWND>(parent);
#else
 window.parent_window=reinterpret_cast<uintptr_t>(parent);
#endif
 cef_browser_settings_t settings{};settings.size=sizeof(settings);settings.windowless_frame_rate=60;settings.background_color=0xff000000;Text address(url);
 auto& context=runtime->profiles[profile];if(!context){cef_request_context_settings_t contextSettings{};contextSettings.size=sizeof(contextSettings);Text cachePath((runtime->root+"/"+profile).c_str());contextSettings.cache_path=cachePath.value;context=cef_request_context_create_context(&contextSettings,nullptr);if(!context)throw std::runtime_error("CEF profile creation failed");}
 Retain(context);int accepted=cef_browser_host_create_browser(&window,client,&address.value,&settings,nullptr,context);
 if(!accepted)return nullptr;return new PageHandle(p);
 }catch(const std::exception& e){if(event)event(tag,2,e.what());return nullptr;}
}
// Runs a script in the page's main frame (the Next wiki window's route buttons).
ENB_EXPORT void enb_cef_script(void* handle,const char* code){auto& p=**static_cast<PageHandle*>(handle);if(!p.browser||p.closing)return;auto frame=p.browser->get_main_frame(p.browser);Text text(code),where("about:blank");frame->execute_java_script(frame,&text.value,&where.value,0);Drop(frame);}
ENB_EXPORT void enb_cef_navigate(void* handle,const char* url){auto& p=**static_cast<PageHandle*>(handle);if(!p.browser||p.closing)return;auto frame=p.browser->get_main_frame(p.browser);Text text(url);frame->load_url(frame,&text.value);Drop(frame);}
ENB_EXPORT void enb_cef_place(void* handle,int width,int height,float scale,int visible){auto& p=**static_cast<PageHandle*>(handle);if(p.closing)return;bool resized=p.width!=width||p.height!=height||p.scale!=scale;p.width=width;p.height=height;p.scale=scale;if(!p.browser){p.visible=visible!=0;return;}auto host=p.browser->get_host(p.browser);if(resized){host->notify_screen_info_changed(host);host->was_resized(host);}if(p.visible!=(visible!=0)){p.visible=visible!=0;host->was_hidden(host,!visible);}Drop(host);}
ENB_EXPORT void enb_cef_focus(void* handle,int focus){auto& p=**static_cast<PageHandle*>(handle);if(!p.browser)return;auto h=p.browser->get_host(p.browser);if(!focus)h->send_capture_lost_event(h);h->set_focus(h,focus);Drop(h);}
ENB_EXPORT void enb_cef_mouse(void* handle,int kind,int x,int y,int button,int value,int modifiers,int clicks){auto& p=**static_cast<PageHandle*>(handle);if(!p.browser||p.closing)return;auto h=p.browser->get_host(p.browser);cef_mouse_event_t e{};e.x=x;e.y=y;e.modifiers=modifiers;if(kind==0)h->send_mouse_move_event(h,&e,value);else if(kind==1)h->send_mouse_click_event(h,&e,static_cast<cef_mouse_button_type_t>(button),value,clicks);else h->send_mouse_wheel_event(h,&e,button,value);Drop(h);}
ENB_EXPORT void enb_cef_key(void* handle,int kind,int key,int native,int modifiers,int character){auto& p=**static_cast<PageHandle*>(handle);if(!p.browser||p.closing)return;auto h=p.browser->get_host(p.browser);cef_key_event_t e{};e.size=sizeof(e);e.is_system_key=(modifiers&8)!=0;e.type=static_cast<cef_key_event_type_t>(kind);e.windows_key_code=key;e.native_key_code=native;e.modifiers=modifiers;e.character=static_cast<char16_t>(character);e.unmodified_character=e.character;h->send_key_event(h,&e);Drop(h);}
ENB_EXPORT int enb_cef_frame(void* handle,int popup,Frame* frame,cef_rect_t* bounds){auto& p=**static_cast<PageHandle*>(handle);*frame=p.frames[popup?1:0];*bounds=p.popupRect;return !p.closing&&frame->texture&&(!popup||p.popup);}
ENB_EXPORT void enb_cef_close(void* handle){auto& p=**static_cast<PageHandle*>(handle);p.closing=true;if(p.browser&&!p.closeSent){p.closeSent=true;auto h=p.browser->get_host(p.browser);h->close_browser(h,1);Drop(h);}}
ENB_EXPORT int enb_cef_closed(void* handle){auto& p=**static_cast<PageHandle*>(handle);if(p.closing&&p.browser&&!p.closeSent)enb_cef_close(handle);return p.closed;}
ENB_EXPORT void enb_cef_release(void* handle){delete static_cast<PageHandle*>(handle);}

ENB_EXPORT void enb_cef_ime(void* handle,const char* utf8,int start,int length,int commit){auto& p=**static_cast<PageHandle*>(handle);if(!p.browser||p.closing)return;auto h=p.browser->get_host(p.browser);Text text(utf8);cef_range_t unchanged{UINT32_MAX,UINT32_MAX};if(commit)h->ime_commit_text(h,&text.value,&unchanged,0);else if(text.value.length==0)h->ime_cancel_composition(h);else{cef_range_t selected{static_cast<uint32_t>(start),static_cast<uint32_t>(start+length)};h->ime_set_composition(h,&text.value,0,nullptr,&unchanged,&selected);}Drop(h);}

ENB_EXPORT void enb_cef_release_gpu(void* handle,int lost){auto& p=**static_cast<PageHandle*>(handle);for(auto& f:p.frames){if(!lost&&p.transfer)p.transfer->Release(f);else f={};}p.transfer.reset();if(p.browser&&!p.closing){auto h=p.browser->get_host(p.browser);h->invalidate(h,PET_VIEW);if(p.popup)h->invalidate(h,PET_POPUP);Drop(h);}}
