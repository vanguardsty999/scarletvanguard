#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cerrno>
#include <cwchar>
#include <cstring>
#include <algorithm>
#include <string>
#include <sstream>
#include <vector>
#include <cstddef>
using U32=uint32_t; using S32=int32_t;
struct Plane { void* buffer; S32 allocate; U32 pitch; };
struct PlaneSet { Plane y,cr,cb,alpha,hdr; };
struct FrameBuffers { S32 totalFrames; U32 yaWidth,yaHeight,chromaWidth,chromaHeight,frameNum; PlaneSet frames[3]; };
struct BinkPrefix { U32 width,height,frames,frameNum,lastFrameNum,frameRate,frameRateDiv,readError;
 U32 openFlags,binkType,largestFrameSize,frameSize,sndSize,frameChangePercent;
 S32 numTracks,numRects; U32 soundOn,videoOn,needIO; S32 closing;
 S32 rect[4]; float colorSpace[16]; uint64_t fileOffset,highest1SecRate; FrameBuffers* buffers; };
struct OpenOptions { uint64_t fileOffset; int64_t ioBufferSize; S32 forceRate,forceRateDiv,simulate; U32 totalTracks; S32* trackNums; void* userOpen; };
static_assert(offsetof(BinkPrefix,buffers)==176 && sizeof(OpenOptions)==48,"Bink ABI mismatch");
using SoundSystemOpen=void* (__stdcall*)(uintptr_t,uintptr_t);
extern "C" {
 BinkPrefix* __stdcall BinkOpenWithOptions(const char*,const OpenOptions*,U32);
 S32 __stdcall BinkDoFrame(BinkPrefix*);
 S32 __stdcall BinkWait(BinkPrefix*);
 S32 __stdcall BinkPause(BinkPrefix*,S32);
 void __stdcall BinkNextFrame(BinkPrefix*);
 void __stdcall BinkGoto(BinkPrefix*,U32,S32);
 void __stdcall BinkClose(BinkPrefix*);
 char* __stdcall BinkGetError();
 S32 __stdcall BinkSetSoundSystem2(SoundSystemOpen,uintptr_t,uintptr_t);
 void* __stdcall BinkOpenXAudio2(uintptr_t,uintptr_t);
 void __stdcall BinkSetVolume(BinkPrefix*,U32,S32);
 U32 __stdcall BinkGetTrackID(BinkPrefix*,U32);
 int setup_rhi(void*,void*,S32,void**) { return 0; }
 void shutdown_rhi() {} void* createtextures_rhi(void*) { return nullptr; }
 void begindraw_rhi() {} void enddraw_rhi() {}
 void selectscreenrendertarget_rhi(void*,S32,S32,U32,S32) {}
 void selectrendertarget_rhi(void*,S32,S32,S32,U32,S32) {}
 void clearrendertarget_rhi() {}
}
static bool number(const wchar_t* p,uint64_t& n) { if(!p||!*p||*p==L'-')return false; errno=0; wchar_t* end=nullptr; n=_wcstoui64(p,&end,0); return errno!=ERANGE && end && !*end; }
static bool writeAll(HANDLE handle,const void* data,size_t count) { auto p=static_cast<const unsigned char*>(data); while(count){DWORD n=0;if(!WriteFile(handle,p,static_cast<DWORD>(std::min<size_t>(count,1<<20)),&n,nullptr)||!n)return false;p+=n;count-=n;}return true; }
static unsigned char channel(float x) { return static_cast<unsigned char>(std::clamp(x*255.f+0.5f,0.f,255.f)); }
static int fail(const char* message) { fprintf(stderr,"%s\n",message);return 2; }

int wmain(int argc,wchar_t** argv) {
 SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
 if(argc<4||argc>5)return fail("Usage: bink-player <source> <offset> <memberLength> [previewWidth=960]");
 uint64_t offset=0,member=0,maxWidth=960;
 if(!number(argv[2],offset)||!number(argv[3],member)||(argc==5&&!number(argv[4],maxWidth))||maxWidth<160||maxWidth>1920)return fail("Invalid bounds or preview width");
 HANDLE input=CreateFileW(argv[1],GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
 if(input==INVALID_HANDLE_VALUE)return fail("Cannot open source archive");
 auto closeInput=[&](){CloseHandle(input);};
 LARGE_INTEGER size{},pos{};unsigned char header[48];DWORD got=0;
 if(!GetFileSizeEx(input,&size)||size.QuadPart<48||offset>uint64_t(size.QuadPart)-48||member>uint64_t(size.QuadPart)-offset){closeInput();return fail("Member outside source");}
 pos.QuadPart=offset;
 if(!SetFilePointerEx(input,pos,nullptr,FILE_BEGIN)||!ReadFile(input,header,sizeof(header),&got,nullptr)||got!=sizeof(header)){closeInput();return fail("Cannot read video header");}
 auto u32=[&](size_t at){U32 v;memcpy(&v,header+at,4);return v;};
 if((memcmp(header,"KB2",3)&&memcmp(header,"BIK",3))||uint64_t(u32(4))+8!=member||!u32(8)||u32(8)>1000000||u32(16)!=u32(8)||!u32(20)||!u32(24)||u32(20)>8192||u32(24)>8192||uint64_t(u32(20))*u32(24)>33554432||!u32(28)||!u32(32)||u32(12)>67108864||u32(40)>256){closeInput();return fail("Unsupported or malformed video header");}
 if(u32(36)&4){closeInput();return fail("HDR video playback is not supported by this CPU preview decoder");}
 bool audio=u32(40)>0 && BinkSetSoundSystem2(BinkOpenXAudio2,uintptr_t(-2),0)!=0;
 S32 track=0;OpenOptions options{};options.fileOffset=offset;options.ioBufferSize=1024*1024;options.forceRate=-1;options.simulate=-1;
 options.totalTracks=audio?1:0;options.trackNums=&track;
 pos.QuadPart=0;SetFilePointerEx(input,pos,nullptr,FILE_BEGIN);
 constexpr U32 flags=0x00800000u|0x00000020u|0x01000000u|0x08000000u|0x00004000u;
 BinkPrefix* b=BinkOpenWithOptions(reinterpret_cast<const char*>(input),&options,flags);
 if(!b){fprintf(stderr,"BinkOpen: %s\n",BinkGetError());closeInput();return 2;}
 auto close=[&](){BinkClose(b);closeInput();};
 if(b->width!=u32(20)||b->height!=u32(24)||b->frames!=u32(8)){close();return fail("Decoder/header mismatch");}
 BinkPause(b,1);
 double scale=std::min({1.0,double(maxWidth)/b->width,1080.0/b->height});
 U32 width=std::max<U32>(1,U32(b->width*scale)),height=std::max<U32>(1,U32(b->height*scale));
 HANDLE out=GetStdHandle(STD_OUTPUT_HANDLE),commands=GetStdHandle(STD_INPUT_HANDLE);
 audio=audio && b->soundOn!=0 && b->numTracks>0;
 U32 intro[]={0x50504652,1,width,height,b->width,b->height,b->frames,b->frameRate,b->frameRateDiv,u32(40),audio?1u:0u};
 if(!writeAll(out,intro,sizeof(intro))){close();return 0;}
 std::vector<unsigned char> pixels(size_t(width)*height*4);
 std::string pending;bool quit=false,playing=false,loop=false,decodePending=true,atEnd=false;
 auto state=[&](){U32 p[]={0x54415453,b->frameNum,playing?1u:0u,atEnd?1u:0u};return writeAll(out,p,sizeof(p));};
 auto volume=[&](int value){if(audio)BinkSetVolume(b,BinkGetTrackID(b,0),std::clamp(value,0,100)*32768/100);};
 volume(60);
 while(!quit){
  DWORD available=0;
  if(!PeekNamedPipe(commands,nullptr,0,nullptr,&available,nullptr)){quit=true;break;}
  while(available){
   char buf[4096];DWORD read=0;if(!ReadFile(commands,buf,std::min<DWORD>(available,sizeof(buf)),&read,nullptr)||!read){quit=true;break;}
   pending.append(buf,read);if(pending.size()>8192){quit=true;break;}
   size_t newline;
   while((newline=pending.find('\n'))!=std::string::npos){
    std::string line=pending.substr(0,newline);pending.erase(0,newline+1);std::istringstream parser(line);std::string cmd;int value=0;parser>>cmd;
    if(cmd=="QUIT"){quit=true;break;}
    if(cmd=="PLAY"){if(atEnd){BinkGoto(b,1,0);decodePending=true;atEnd=false;}playing=true;BinkPause(b,0);if(!state())quit=true;}
    else if(cmd=="PAUSE"){playing=false;BinkPause(b,1);if(!state())quit=true;}
    else if(cmd=="SEEK" && parser>>value){U32 frame=std::clamp<U32>(value<1?1u:U32(value),1,b->frames);BinkGoto(b,frame,0);BinkPause(b,playing?0:1);decodePending=true;atEnd=false;}
    else if(cmd=="STEP"){playing=false;BinkPause(b,1);if(b->frameNum<b->frames)BinkGoto(b,b->frameNum+1,0);decodePending=true;atEnd=false;}
    else if(cmd=="VOLUME" && parser>>value)volume(value);
    else if(cmd=="LOOP" && parser>>value)loop=value!=0;
   }
   if(quit||!PeekNamedPipe(commands,nullptr,0,nullptr,&available,nullptr))break;
  }
  if(quit)break;
  if(!decodePending && (!playing || BinkWait(b))){Sleep(2);continue;}
  if(!decodePending){
   if(b->frameNum>=b->frames){
    if(loop){BinkGoto(b,1,0);BinkPause(b,0);atEnd=false;}
    else{playing=false;atEnd=true;BinkPause(b,1);if(!state())break;continue;}
   } else BinkNextFrame(b);
  }
  BinkDoFrame(b);decodePending=false;
  if(b->readError||!b->buffers||b->buffers->frameNum>2){close();return fail("Video decode failed");}
  auto& f=b->buffers->frames[b->buffers->frameNum];
  if(!f.y.buffer||!f.cb.buffer||!f.cr.buffer||f.y.pitch<b->width||f.cb.pitch<(b->width+1)/2||f.cr.pitch<(b->width+1)/2){close();return fail("Invalid video frame planes");}
  auto Y=static_cast<const unsigned char*>(f.y.buffer),Cb=static_cast<const unsigned char*>(f.cb.buffer),Cr=static_cast<const unsigned char*>(f.cr.buffer);const float* c=b->colorSpace;
  for(U32 y=0;y<height;y++)for(U32 x=0;x<width;x++){
   U32 sy=U32(uint64_t(y)*b->height/height),sx=U32(uint64_t(x)*b->width/width);
   float py=Y[uint64_t(sy)*f.y.pitch+sx]/255.f,cb=Cb[uint64_t(sy/2)*f.cb.pitch+sx/2]/255.f,cr=Cr[uint64_t(sy/2)*f.cr.pitch+sx/2]/255.f;
   size_t p=(size_t(y)*width+x)*4;
   pixels[p+2]=channel(py*c[12]+cr*c[0]+cb*c[4]+c[8]);pixels[p+1]=channel(py*c[13]+cr*c[1]+cb*c[5]+c[9]);pixels[p]=channel(py*c[14]+cr*c[2]+cb*c[6]+c[10]);pixels[p+3]=255;
  }
  U32 frame[]={0x4d415246,b->frameNum,U32(pixels.size()),playing?1u:0u};
  if(!writeAll(out,frame,sizeof(frame))||!writeAll(out,pixels.data(),pixels.size()))break;
 }
 close();return 0;
}
