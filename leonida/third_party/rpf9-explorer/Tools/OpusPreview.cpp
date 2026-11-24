#define NOMINMAX
#include <windows.h>
#include <opus.h>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <vector>
#include <string>
#include <stdexcept>
#include <algorithm>
#include <memory>
#include <cmath>
#include <limits>

static constexpr int OpusDecodeRate = 48000;

struct Handle {
    HANDLE value = INVALID_HANDLE_VALUE;
    explicit Handle(HANDLE h = INVALID_HANDLE_VALUE) : value(h) {}
    ~Handle() { if (value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    void close() { if (value != INVALID_HANDLE_VALUE) { CloseHandle(value); value = INVALID_HANDLE_VALUE; } }
};
static uint16_t u16(const uint8_t* p) { return uint16_t(p[0]) | (uint16_t(p[1]) << 8); }
static uint32_t u32(const uint8_t* p) { return uint32_t(u16(p)) | (uint32_t(u16(p+2)) << 16); }
static uint64_t u64(const uint8_t* p) { return uint64_t(u32(p)) | (uint64_t(u32(p+4)) << 32); }
static void put16(uint8_t* p, uint16_t x) { p[0] = uint8_t(x); p[1] = uint8_t(x >> 8); }
static void put32(uint8_t* p, uint32_t x) { put16(p,uint16_t(x)); put16(p+2,uint16_t(x>>16)); }
static uint64_t number(const wchar_t* s) {
    if (!s || !*s || *s == L'-' || *s == L'+') throw std::runtime_error("Invalid unsigned argument.");
    wchar_t* end=nullptr; errno=0; auto v=_wcstoui64(s,&end,0);
    if (errno || !end || *end) throw std::runtime_error("Invalid numeric argument.");
    return v;
}
struct Source {
    Handle file;
    uint64_t length, base, span;
    Source(const wchar_t* path, uint64_t at, uint64_t size) :
        file(CreateFileW(path,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_RANDOM_ACCESS,nullptr)),base(at),span(size) {
        if (file.value == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot open source read-only.");
        LARGE_INTEGER n;
        if (!GetFileSizeEx(file.value,&n) || n.QuadPart<0) throw std::runtime_error("Cannot measure source.");
        length=uint64_t(n.QuadPart);
        if (base>length || span>length-base || span<16) throw std::runtime_error("Source span is out of bounds.");
    }
    void read(uint64_t rel, uint8_t* out, size_t n) {
        if (rel>span || n>span-rel || n>4*1024*1024) throw std::runtime_error("Bounded read rejected.");
        LARGE_INTEGER pos;pos.QuadPart=static_cast<LONGLONG>(base+rel);
        DWORD actual=0;
        if (!SetFilePointerEx(file.value,pos,nullptr,FILE_BEGIN) || !ReadFile(file.value,out,DWORD(n),&actual,nullptr) || actual!=n)
            throw std::runtime_error("Source was truncated or could not be read.");
    }
};
struct AudioSpan { uint64_t offset=0,size=0,skipSamples=0; };
struct Sound { uint32_t id=0, samples=0; int rate=0,codec=-1,channels=0; uint64_t data=0,size=0; bool music=false; std::vector<AudioSpan> spans; };
static Sound inspect(Source& source, uint64_t selected) {
    uint8_t prefix[16];source.read(0,prefix,16);
    if (memcmp(prefix,"ADAT",4)) throw std::runtime_error("Not a little-endian AWC container.");
    auto flags=u32(prefix+4),n=u32(prefix+8),headerSize=u32(prefix+12);
    if ((flags&0xFF00FFFF)!=0xFF000001 || (flags&0x00980000) || !n || n>8192 ||
        headerSize<20 || headerSize>4*1024*1024 || headerSize>source.span)
        throw std::runtime_error("Unsupported flags or invalid AWC header.");
    std::vector<uint8_t> h(headerSize);source.read(0,h.data(),h.size());
    uint64_t p=16+((flags&0x400000)?4:0),indexStart=0;
    if (flags&0x10000) { indexStart=p;p+=uint64_t(n)*2; }
    if (flags&0x200000) p+=(uint64_t(n)+1)*4;
    uint64_t infoStart=p;p+=uint64_t(n)*4;
    if (p>h.size()) throw std::runtime_error("Stream table out of bounds.");
    uint32_t total=0,previous=0;
    for (uint32_t i=0;i<n;i++) {
        auto v=u32(h.data()+infoStart+i*4);auto id=v&0x1FFFFFFF;
        if (i && id<previous) throw std::runtime_error("Stream IDs are not ordered.");previous=id;
        if (indexStart && u16(h.data()+indexStart+i*2)!=total) throw std::runtime_error("Chunk index/count mismatch.");
        total+=v>>29;
    }
    if (p+uint64_t(total)*8!=h.size()) throw std::runtime_error("Tag table/header length mismatch.");
    Sound chosen;uint64_t extent=h.size();bool hasData=false;uint64_t chosenFormat=0,chosenFormatSize=0;
    struct Music { uint64_t format,size,data,dataSize; };std::vector<Music> music;std::vector<uint32_t> ids(n);
    for(uint32_t i=0;i<n;i++)ids[i]=u32(h.data()+infoStart+i*4)&0x1FFFFFFF;
    for (uint32_t i=0;i<n;i++) {
        auto v=u32(h.data()+infoStart+i*4);uint64_t data=0,dataSize=0,format=0,formatSize=0,musicAt=0,musicSize=0;
        for (uint32_t j=0;j<(v>>29);j++,p+=8) {
            auto t=u64(h.data()+p);auto type=t>>56;auto size=(t>>28)&0x0FFFFFFF,at=t&0x0FFFFFFF;
            if (size && (at<h.size() || at>source.span || size>source.span-at)) throw std::runtime_error("Tag span out of bounds.");
            extent=std::max(extent,at+size);
            if (type==0x55) { if (data) throw std::runtime_error("Duplicate data tag.");data=at;dataSize=size;hasData|=size>0; }
            if(type==0x48) { if(musicAt)throw std::runtime_error("Duplicate music format tag.");musicAt=at;musicSize=size; }
            if (type==0x76) { if (format) throw std::runtime_error("Duplicate format tag.");format=at;formatSize=size; }
        }
        if(musicAt && dataSize)music.push_back({musicAt,musicSize,data,dataSize});
        if (i==selected) { chosen.id=v&0x1FFFFFFF;chosen.data=data;chosen.size=dataSize;chosenFormat=format;chosenFormatSize=formatSize; }
    }
    if (!hasData || extent!=source.span) throw std::runtime_error("Provided length must equal the validated complete referenced AWC extent.");
    if(chosenFormatSize>=32 && chosen.data && chosen.size) {
        uint8_t fmt[32];source.read(chosenFormat,fmt,32);
        chosen.samples=u32(fmt);chosen.rate=u16(fmt+8);chosen.codec=fmt[28];chosen.channels=fmt[29];
        if(chosen.codec==0) {
            if(chosen.rate<1000 || chosen.rate>192000 || chosen.channels<1 || chosen.channels>2 || chosen.size!=uint64_t(chosen.samples)*chosen.channels*2)
                throw std::runtime_error("PCM declared sample count does not match bounded data length.");
        } else if(chosen.codec!=0x0B || chosen.rate<1000 || chosen.rate>192000 || chosen.channels<1 || chosen.channels>2 || chosen.size%2048)
            throw std::runtime_error("Selected member is not supported Opus or PCM16.");
        chosen.spans.push_back({chosen.data,chosen.size,0});
    } else {
        bool found=false;
        for(const auto& descriptor:music) {
            if(descriptor.size<12 || descriptor.size>524)throw std::runtime_error("Invalid music descriptor size.");
            std::vector<uint8_t> m(size_t(descriptor.size));source.read(descriptor.format,m.data(),m.size());
            uint32_t blocks=u32(m.data()),chunk=u32(m.data()+4),channels=u32(m.data()+8);
            if(!channels || channels>32 || m.size()!=12+channels*16 || !blocks || blocks>131072 || chunk<2048 || chunk>16*1024*1024 ||
                uint64_t(blocks-1)*chunk>=descriptor.dataSize || uint64_t(blocks)*chunk<descriptor.dataSize)
                throw std::runtime_error("Music block dimensions are invalid.");
            int which=-1;
            for(uint32_t c=0;c<channels;c++) {
                auto id=u32(m.data()+12+c*16)&0x1FFFFFFF;
                if((selected<n && ids[size_t(selected)]==id) || selected==uint64_t(n)+c)which=int(c);
            }
            if(which<0)continue;
            const uint8_t* fmt=m.data()+12+which*16;
            chosen.id=u32(fmt)&0x1FFFFFFF;chosen.samples=u32(fmt+4);chosen.rate=u16(fmt+10);chosen.codec=fmt[12];chosen.channels=1;chosen.music=true;
            chosen.data=descriptor.data;chosen.size=descriptor.dataSize;
            if(chosen.codec!=0x0B || chosen.rate<1000 || chosen.rate>192000)throw std::runtime_error("Only observed codec0B streamed Opus is supported.");
            for(uint32_t b=0;b<blocks;b++) {
                uint64_t at=descriptor.data+uint64_t(b)*chunk;
                uint64_t remaining=descriptor.dataSize-uint64_t(b)*chunk,blockSize=std::min<uint64_t>(chunk,remaining);
                if(blockSize<channels*24)throw std::runtime_error("Truncated music block channel table.");
                uint8_t records[32*24];source.read(at,records,channels*24);
                uint64_t subblocksTotal=0,before=0;uint32_t entries=0,skip=0;
                for(uint32_t c=0;c<channels;c++) {
                    auto count=u32(records+c*24+4);
                    if(count>blockSize/2048)throw std::runtime_error("Invalid music subblock count.");
                    if(c<uint32_t(which))before+=count;
                    if(c==uint32_t(which)){entries=count;skip=u32(records+c*24+8);}
                    subblocksTotal+=count;
                }
                uint64_t header=(uint64_t(channels)*24+subblocksTotal*4+2047)&~uint64_t(2047);
                if(header>blockSize || subblocksTotal*2048>blockSize-header || skip>OpusDecodeRate*10)throw std::runtime_error("Music channel data or skip is out of bounds.");
                if(entries)chosen.spans.push_back({at+header+before*2048,uint64_t(entries)*2048,skip});
            }
            found=true;break;
        }
        if(!found || chosen.spans.empty())throw std::runtime_error("Selected member has no independent audio channel.");
    }
    return chosen;
}
static void write(Handle& f,const void* p,size_t n) {
    DWORD done=0;
    if (n>UINT32_MAX || !WriteFile(f.value,p,DWORD(n),&done,nullptr) || done!=n) throw std::runtime_error("Output write failed.");
}
int wmain(int argc,wchar_t** argv) {
    if (argc<6 || argc>7) { fprintf(stderr,"Usage: opus-preview.exe source offset length streamIndex output.wav [maxSeconds=30]\n");return 2; }
    bool created=false;Handle output;
    try {
        uint64_t maxSeconds=argc==7?number(argv[6]):30;
        if (!maxSeconds || maxSeconds>120) throw std::runtime_error("Preview limit must be 1 to 120 seconds.");
        Source source(argv[1],number(argv[2]),number(argv[3]));
        Sound sound=inspect(source,number(argv[4]));
        const int outputRate = sound.codec == 0x0B && sound.rate != 32000 ? OpusDecodeRate : sound.rate;
        const uint64_t maxFrames = maxSeconds * outputRate;
        int error=0;
        std::unique_ptr<OpusDecoder,decltype(&opus_decoder_destroy)> decoder(sound.codec==0x0B?opus_decoder_create(OpusDecodeRate,sound.channels,&error):nullptr,opus_decoder_destroy);
        if (sound.codec==0x0B && (!decoder || error!=OPUS_OK)) throw std::runtime_error("libopus decoder initialization failed.");
        output.value=CreateFileW(argv[5],GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
        if (output.value==INVALID_HANDLE_VALUE) throw std::runtime_error("Output already exists or could not be created.");
        created=true;
        uint8_t wav[44]={};memcpy(wav,"RIFF",4);memcpy(wav+8,"WAVEfmt ",8);put32(wav+16,16);put16(wav+20,1);
        put16(wav+22,uint16_t(sound.channels));put32(wav+24,uint32_t(outputRate));put32(wav+28,uint32_t(outputRate*sound.channels*2));put16(wav+32,uint16_t(sound.channels*2));put16(wav+34,16);memcpy(wav+36,"data",4);
        write(output,wav,44);
        uint8_t block[2048],previousBlock[2048]={};size_t previousUsed=0;uint64_t previousSamples=0;
        opus_int16 pcm[5760*2];uint64_t written=0,packets=0;int peak=0;long double sumSquares=0;bool limited=false;
        auto emit=[&](const opus_int16* samples,int count) {
            int take=int(std::min<uint64_t>(uint64_t(count),maxFrames-written));
            for(int k=0;k<take*sound.channels;k++){int x=samples[k];peak=std::max(peak,std::abs(x));sumSquares+=static_cast<long double>(x)*x;}
            write(output,samples,size_t(take)*sound.channels*2);written+=take;
            if(written>=maxFrames)limited=true;
        };
        for(const auto& span:sound.spans) {
            if(limited)break;
            if(sound.codec==0) {
                uint64_t done=0;
                while(done<span.size && !limited){size_t n=size_t(std::min<uint64_t>(span.size-done,2048));source.read(span.offset+done,block,n);emit(reinterpret_cast<const opus_int16*>(block),int(n/(2*sound.channels)));done+=n;}
                continue;
            }
            uint64_t skip=span.skipSamples;
            for(uint64_t b=0;b<span.size && !limited;b+=2048) {
                source.read(span.offset+b,block,2048);size_t p=0;uint64_t blockSamples=0;
                if(b==0 && skip && (skip!=previousSamples || !previousUsed || memcmp(block,previousBlock,previousUsed)))
                    throw std::runtime_error("Streamed Opus repeated prefix failed byte validation.");
                while(p+2<=2048) {
                    uint16_t bytes=u16(block+p);
                    if(!bytes){for(size_t k=p;k<2048;k++)if(block[k])throw std::runtime_error("Nonzero bytes after packet padding sentinel.");break;}
                    if(bytes>2048-p-2)throw std::runtime_error("Opus packet crosses a bounded 2048-byte block.");
                    const uint8_t* packet=block+p+2;
                    if(bool(packet[0]&4)!=(sound.channels==2))throw std::runtime_error("Opus TOC channel mismatch.");
                    int expected=opus_packet_get_nb_samples(packet,bytes,OpusDecodeRate);
                    if(expected<=0 || expected>5760)throw std::runtime_error("Invalid Opus packet duration.");
                    blockSamples+=expected;
                    if(skip){if(uint64_t(expected)>skip)throw std::runtime_error("Music repeat skip is not whole packets.");skip-=expected;}
                    else {
                        int decoded=opus_decode(decoder.get(),packet,bytes,pcm,5760,0);
                        if(decoded!=expected)throw std::runtime_error("libopus rejected an audio packet.");
                        emit(pcm,decoded);packets++;
                    }
                    p+=2+bytes;if(limited)break;
                }
                if(!limited && p==2047 && block[p])throw std::runtime_error("Nonzero final block pad.");
                if(!limited){memcpy(previousBlock,block,2048);previousUsed=p;previousSamples=blockSamples;}
            }
            if(skip && !limited)throw std::runtime_error("Incomplete music repeated prefix.");
        }
        if (!written) throw std::runtime_error("No audio packets were decoded.");
        uint32_t dataBytes=uint32_t(written*sound.channels*2);put32(wav+4,36+dataBytes);put32(wav+40,dataBytes);
        LARGE_INTEGER zero={};if(!SetFilePointerEx(output.value,zero,nullptr,FILE_BEGIN)) throw std::runtime_error("Cannot finish WAV header.");write(output,wav,44);output.close();
        printf("{\"codec\":\"%s\",\"streamIndex\":%llu,\"id\":\"%08X\",\"sampleRate\":%d,\"channels\":%d,\"declaredSamples\":%u,\"decodedSamples\":%llu,\"packets\":%llu,\"peak\":%d,\"rms\":%.3f,\"previewLimited\":%s,\"trim\":\"Raw complete packet decode; source-specific pre-skip/end trim unresolved\"}\n",sound.codec==0?"PCM16":"Opus",number(argv[4]),sound.id,outputRate,sound.channels,sound.samples,written,packets,peak,double(sqrt(sumSquares/(written*sound.channels))),limited?"true":"false");
        return 0;
    } catch(const std::exception& e) {
        output.close();if(created) DeleteFileW(argv[5]);fprintf(stderr,"%s\n",e.what());return 1;
    }
}
