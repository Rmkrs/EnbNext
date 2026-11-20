#include <cstring>
#include <stdexcept>
#include <vector>
#include "Browser.h"
#ifdef _WIN32
#include <d3d11_1.h>
#include <wrl/client.h>
using Microsoft::WRL::ComPtr;
#define GL_CALL __stdcall
#else
#define GL_CALL
#endif
namespace {
struct GpuTransfer final:Transfer {
 Resolve resolve;
 void* display;
 void* (GL_CALL *createImage)(void*,void*,unsigned,void*,const int*);
 unsigned (GL_CALL *destroyImage)(void*,void*);
 void (GL_CALL *imageTarget)(unsigned,void*);
 void (GL_CALL *get)(unsigned,int*);
 void (GL_CALL *genTextures)(int,unsigned*);
 void (GL_CALL *deleteTextures)(int,const unsigned*);
 void (GL_CALL *bindTexture)(unsigned,unsigned);
 void (GL_CALL *texImage)(unsigned,int,int,int,int,int,unsigned,unsigned,const void*);
 void (GL_CALL *texParameter)(unsigned,unsigned,int);
 void (GL_CALL *genBuffers)(int,unsigned*);
 void (GL_CALL *deleteBuffers)(int,const unsigned*);
 void (GL_CALL *bindBuffer)(unsigned,unsigned);
 void (GL_CALL *attach)(unsigned,unsigned,unsigned,unsigned,int);
 unsigned (GL_CALL *check)(unsigned);
 void (GL_CALL *blit)(int,int,int,int,int,int,int,int,unsigned,unsigned);
 void (GL_CALL *finish)();
 void (GL_CALL *disable)(unsigned);
 void (GL_CALL *enable)(unsigned);
 unsigned char (GL_CALL *isEnabled)(unsigned);
#ifdef _WIN32
 ComPtr<ID3D11Device1> device;
#endif
 template<class T> T Proc(const char* name){auto p=resolve(name);if(!p)throw std::runtime_error(std::string("Missing public graphics procedure: ")+name);return reinterpret_cast<T>(p);}
 explicit GpuTransfer(Resolve r):resolve(r){
  auto current=Proc<void*(GL_CALL*)()>("eglGetCurrentDisplay");display=current();if(!display)throw std::runtime_error("CEF copy requires current EGL context");
#define P(member,name) member=Proc<decltype(member)>(name)
  P(createImage,"eglCreateImageKHR");P(destroyImage,"eglDestroyImageKHR");P(imageTarget,"glEGLImageTargetTexture2DOES");P(get,"glGetIntegerv");
  P(genTextures,"glGenTextures");P(deleteTextures,"glDeleteTextures");P(bindTexture,"glBindTexture");P(texImage,"glTexImage2D");P(texParameter,"glTexParameteri");
  P(genBuffers,"glGenFramebuffers");P(deleteBuffers,"glDeleteFramebuffers");P(bindBuffer,"glBindFramebuffer");P(attach,"glFramebufferTexture2D");P(check,"glCheckFramebufferStatus");P(blit,"glBlitFramebuffer");P(finish,"glFinish");P(disable,"glDisable");P(enable,"glEnable");P(isEnabled,"glIsEnabled");
#undef P
#ifdef _WIN32
  auto queryDisplay=Proc<unsigned(GL_CALL*)(void*,int,intptr_t*)>("eglQueryDisplayAttribEXT");auto queryDevice=Proc<unsigned(GL_CALL*)(void*,int,intptr_t*)>("eglQueryDeviceAttribEXT");intptr_t eglDevice=0,d3d=0;
  if(!queryDisplay(display,0x322c,&eglDevice)||!queryDevice(reinterpret_cast<void*>(eglDevice),0x33a1,&d3d)||!d3d)throw std::runtime_error("ANGLE D3D11 device unavailable");
  if(FAILED(reinterpret_cast<ID3D11Device*>(d3d)->QueryInterface(IID_PPV_ARGS(&device))))throw std::runtime_error("D3D11.1 device unavailable");
#endif
 }
 void Release(Frame& f) override{if(f.texture)deleteTextures(1,&f.texture);f={};}
 void Copy(Frame& f,const cef_accelerated_paint_info_t& info) override{
  int codedWidth=info.extra.coded_size.width,codedHeight=info.extra.coded_size.height;auto visible=info.extra.visible_rect;int w=visible.width,h=visible.height;if(w<=0||h<=0||visible.x<0||visible.y<0||visible.x+w>codedWidth||visible.y+h>codedHeight)throw std::runtime_error("Invalid accelerated browser extent");
  void* image=nullptr;
#ifdef _WIN32
  ComPtr<ID3D11Texture2D> source;
  if(FAILED(device->OpenSharedResource1(info.shared_texture_handle,IID_PPV_ARGS(&source))))throw std::runtime_error("CEF texture cannot be opened on ANGLE adapter");
  int attrs[]={0x3038};image=createImage(display,nullptr,0x3484,source.Get(),attrs);
#else
  if(info.plane_count<1||info.plane_count>4)throw std::runtime_error("Invalid browser native buffer planes");
  // CEF's public format is BGRA or RGBA. DRM formats describe byte order.
  unsigned format=info.format==CEF_COLOR_TYPE_BGRA_8888?0x34325241u:info.format==CEF_COLOR_TYPE_RGBA_8888?0x34324241u:0;
  if(!format)throw std::runtime_error("Unsupported browser native buffer format");
  std::vector<int> attrs={0x3057,codedWidth,0x3056,codedHeight,0x3271,static_cast<int>(format)};
  const int fd[]={0x3272,0x3275,0x3278,0x3440},offset[]={0x3273,0x3276,0x3279,0x3441},pitch[]={0x3274,0x3277,0x327a,0x3442};
  for(int i=0;i<info.plane_count;i++){auto& p=info.planes[i];if(p.offset>INT32_MAX||p.stride>INT32_MAX)throw std::runtime_error("Browser buffer exceeds EGL integer range");attrs.insert(attrs.end(),{fd[i],p.fd,offset[i],static_cast<int>(p.offset),pitch[i],static_cast<int>(p.stride),0x3443+2*i,static_cast<int>(info.modifier),0x3444+2*i,static_cast<int>(info.modifier>>32)});}
  attrs.push_back(0x3038);image=createImage(display,nullptr,0x3270,nullptr,attrs.data());
#endif
  if(!image)throw std::runtime_error("EGL browser texture import failed");
  int oldTexture=0,oldRead=0,oldDraw=0;get(0x8069,&oldTexture);get(0x8caa,&oldRead);get(0x8ca6,&oldDraw);bool scissor=isEnabled(0x0c11)!=0;disable(0x0c11);
  unsigned sourceTexture=0,fb[2]{};genTextures(1,&sourceTexture);bindTexture(0x0de1,sourceTexture);imageTarget(0x0de1,image);genBuffers(2,fb);
  if(f.texture==0||f.width!=w||f.height!=h){if(oldTexture==static_cast<int>(f.texture))oldTexture=0;Release(f);genTextures(1,&f.texture);bindTexture(0x0de1,f.texture);texImage(0x0de1,0,0x8058,w,h,0,0x1908,0x1401,nullptr);texParameter(0x0de1,0x2801,0x2601);texParameter(0x0de1,0x2800,0x2601);texParameter(0x0de1,0x2802,0x812f);texParameter(0x0de1,0x2803,0x812f);f.width=w;f.height=h;}
  bindBuffer(0x8ca8,fb[0]);attach(0x8ca8,0x8ce0,0x0de1,sourceTexture,0);bindBuffer(0x8ca9,fb[1]);attach(0x8ca9,0x8ce0,0x0de1,f.texture,0);
  bool ok=check(0x8ca8)==0x8cd5&&check(0x8ca9)==0x8cd5;
  if(ok){blit(visible.x,visible.y,visible.x+w,visible.y+h,0,0,w,h,0x4000,0x2600);finish();++f.serial;}
  bindBuffer(0x8ca8,oldRead);bindBuffer(0x8ca9,oldDraw);bindTexture(0x0de1,oldTexture);if(scissor)enable(0x0c11);
  deleteBuffers(2,fb);deleteTextures(1,&sourceTexture);destroyImage(display,image);
  if(!ok)throw std::runtime_error("Browser GPU copy framebuffer incomplete");
 }
};
}
std::unique_ptr<Transfer> MakeTransfer(Resolve resolve){return std::make_unique<GpuTransfer>(resolve);}
