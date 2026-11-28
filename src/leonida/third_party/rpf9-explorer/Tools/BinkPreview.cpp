#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <io.h>
#include <fcntl.h>
#include <algorithm>
#include <string>
#include <vector>

using U32=uint32_t; using S32=int32_t;
struct Plane { void* buffer; S32 allocate; U32 pitch; };
struct PlaneSet { Plane y,cr,cb,alpha,hdr; };
struct FrameBuffers { S32 totalFrames; U32 yaWidth,yaHeight,chromaWidth,chromaHeight,frameNum; PlaneSet frames[3]; };
struct BinkPrefix {
    U32 width,height,frames,frameNum,lastFrameNum,frameRate,frameRateDiv,readError;
    U32 openFlags,binkType,largestFrameSize,frameSize,sndSize,frameChangePercent;
    S32 numTracks,numRects; U32 soundOn,videoOn,needIO; S32 closing;
    S32 rect[4]; float colorSpace[16]; uint64_t fileOffset,highest1SecRate;
    FrameBuffers* buffers;
};
struct OpenOptions { uint64_t fileOffset; int64_t ioBufferSize; S32 forceRate,forceRateDiv,simulate; U32 totalTracks; S32* trackNums; void* userOpen; };
static_assert(offsetof(BinkPrefix,buffers)==176,"Bink x64 ABI mismatch");
static_assert(sizeof(OpenOptions)==48,"Bink options x64 ABI mismatch");
extern "C" {
    BinkPrefix* __stdcall BinkOpenWithOptions(const char*,const OpenOptions*,U32);
    S32 __stdcall BinkDoFrame(BinkPrefix*);
    void __stdcall BinkGoto(BinkPrefix*,U32,S32);
    void __stdcall BinkClose(BinkPrefix*);
    char* __stdcall BinkGetError();


    int setup_rhi(void*,void*,S32,void**) { return 0; }
    void shutdown_rhi() {}
    void* createtextures_rhi(void*) { return nullptr; }
    void begindraw_rhi() {}
    void enddraw_rhi() {}
    void selectscreenrendertarget_rhi(void*,S32,S32,U32,S32) {}
    void selectrendertarget_rhi(void*,S32,S32,S32,U32,S32) {}
    void clearrendertarget_rhi() {}
}
static int error(const char* text) { fprintf(stderr,"%s\n",text); return 2; }
static bool uintarg(const wchar_t* s,uint64_t& out) {
    if(!s || !*s || *s==L'-')return false;
    wchar_t* end=nullptr; out=_wcstoui64(s,&end,0); return end && !*end;
}
static unsigned char channel(float f) {
    return static_cast<unsigned char>(std::clamp(f*255.0f+0.5f,0.0f,255.0f));
}
int wmain(int argc,wchar_t** argv) {
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    if(argc<4 || argc>6) return error("Usage: bink-preview <archive-or-bk2> <offset> <output.bmp> [frame=1] [memberLength]");
    uint64_t offset=0,desired=1,memberLength=0;
    if(!uintarg(argv[2],offset) || (argc>4&&!uintarg(argv[4],desired)) || (argc>5&&!uintarg(argv[5],memberLength)))return error("Invalid numeric argument");
    wchar_t source[32768],output[32768];
    auto sourceLen=GetFullPathNameW(argv[1],32768,source,nullptr),outputLen=GetFullPathNameW(argv[3],32768,output,nullptr);
    if(!sourceLen||sourceLen>=32768||!outputLen||outputLen>=32768)return error("Invalid or overlong path");
    if(!_wcsicmp(source,output))return error("Source and output must differ");
    const size_t outLen=wcslen(output);
    if(outLen<4 || _wcsicmp(output+outLen-4,L".bmp"))return error("Output must have .bmp extension");
    HANDLE input=CreateFileW(source,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(input==INVALID_HANDLE_VALUE)return error("Unable to open source");
    LARGE_INTEGER length{},position{};
    if(!GetFileSizeEx(input,&length) || length.QuadPart<44 || offset>uint64_t(length.QuadPart)-44) {CloseHandle(input);return error("Offset outside source");}
    position.QuadPart=offset;
    unsigned char head[44];DWORD got=0;
    if(!SetFilePointerEx(input,position,nullptr,FILE_BEGIN)||!ReadFile(input,head,44,&got,nullptr)||got!=44) {CloseHandle(input);return error("Unable to read header");}
    auto u32=[&](size_t p){U32 v;memcpy(&v,head+p,4);return v;};
    uint64_t embeddedLength=uint64_t(u32(4))+8;
    if(memcmp(head,"KB2",3)!=0 || embeddedLength<44 || embeddedLength>uint64_t(length.QuadPart)-offset || (memberLength&&embeddedLength>memberLength)) {CloseHandle(input);return error("Invalid or out-of-bounds Bink 2 header");}
    U32 width=u32(20),height=u32(24),frameCount=u32(8);
    if(!width||!height||width>8192||height>8192||uint64_t(width)*height>33554432||!frameCount||frameCount>1000000||!desired||desired>frameCount||u32(12)>67108864) {CloseHandle(input);return error("Invalid dimensions or frame number");}
    if(u32(36)&4) {CloseHandle(input);return error("HDR thumbnail conversion is not supported");}
    position.QuadPart=0; SetFilePointerEx(input,position,nullptr,FILE_BEGIN);
    OpenOptions options{};options.fileOffset=offset;options.ioBufferSize=1024*1024;options.forceRate=-1;options.forceRateDiv=0;options.simulate=-1;

    constexpr U32 flags=0x00800000u|0x00000020u|0x01000000u|0x08000000u|0x00004000u;
    BinkPrefix* b=BinkOpenWithOptions(reinterpret_cast<const char*>(input),&options,flags);
    if(!b){fprintf(stderr,"BinkOpen failed: %s\n",BinkGetError());CloseHandle(input);return 2;}
    if(b->width!=width||b->height!=height||b->frames!=frameCount) {BinkClose(b);CloseHandle(input);return error("Decoder/header mismatch");}
    if(desired>1) BinkGoto(b,static_cast<U32>(desired),0);
    BinkDoFrame(b);
    if(b->readError||!b->buffers||b->buffers->frameNum>2) {fprintf(stderr,"Decode failed: %s\n",BinkGetError());BinkClose(b);CloseHandle(input);return 2;}
    const auto& f=b->buffers->frames[b->buffers->frameNum];
    if(!f.y.buffer||!f.cb.buffer||!f.cr.buffer||f.y.pitch<width||f.cb.pitch<(width+1)/2||f.cr.pitch<(width+1)/2) {BinkClose(b);CloseHandle(input);return error("Missing decoded frame planes");}
    HANDLE destination=CreateFileW(output,GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(destination==INVALID_HANDLE_VALUE){BinkClose(b);CloseHandle(input);return error("Unable to create a new thumbnail (existing files are not overwritten)");}
    int destinationFd=_open_osfhandle(reinterpret_cast<intptr_t>(destination),_O_BINARY|_O_WRONLY);
    FILE* target=destinationFd>=0?_fdopen(destinationFd,"wb"):nullptr;
    if(!target){if(destinationFd>=0)_close(destinationFd);else CloseHandle(destination);BinkClose(b);CloseHandle(input);return error("Unable to open thumbnail stream");}
    BITMAPFILEHEADER file{};file.bfType=0x4d42;file.bfOffBits=sizeof(file)+sizeof(BITMAPINFOHEADER);file.bfSize=file.bfOffBits+width*height*4;
    BITMAPINFOHEADER info{};info.biSize=sizeof(info);info.biWidth=width;info.biHeight=-static_cast<LONG>(height);info.biPlanes=1;info.biBitCount=32;info.biCompression=BI_RGB;info.biSizeImage=width*height*4;
    bool ok=fwrite(&file,sizeof(file),1,target)==1 && fwrite(&info,sizeof(info),1,target)==1;
    std::vector<unsigned char> row(size_t(width)*4);
    const auto Y=static_cast<const unsigned char*>(f.y.buffer),Cb=static_cast<const unsigned char*>(f.cb.buffer),Cr=static_cast<const unsigned char*>(f.cr.buffer);
    const float* c=b->colorSpace;
    for(U32 y=0;y<height && ok;++y){
        for(U32 x=0;x<width;++x){
            float py=Y[uint64_t(y)*f.y.pitch+x]/255.0f;
            float pcb=Cb[uint64_t(y/2)*f.cb.pitch+x/2]/255.0f;
            float pcr=Cr[uint64_t(y/2)*f.cr.pitch+x/2]/255.0f;
            row[x*4+2]=channel(py*c[12]+pcr*c[0]+pcb*c[4]+c[8]);
            row[x*4+1]=channel(py*c[13]+pcr*c[1]+pcb*c[5]+c[9]);
            row[x*4+0]=channel(py*c[14]+pcr*c[2]+pcb*c[6]+c[10]);
            row[x*4+3]=255;
        }
        ok=fwrite(row.data(),1,row.size(),target)==row.size();
    }
    ok=fclose(target)==0 && ok;
    printf("{\"width\":%u,\"height\":%u,\"frames\":%u,\"frame\":%u,\"fpsNumerator\":%u,\"fpsDenominator\":%u,\"offset\":%llu,\"embeddedLength\":%llu,\"success\":%s}\n",width,height,b->frames,b->frameNum,b->frameRate,b->frameRateDiv,static_cast<unsigned long long>(offset),static_cast<unsigned long long>(embeddedLength),ok?"true":"false");
    BinkClose(b);CloseHandle(input);
    return ok?0:2;
}
