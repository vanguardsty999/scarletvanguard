// ws2_offline: Winsock2 (ws2_32.dll and its wsock32.dll alias) for a mapped title, restricted to loopback only.
//
// Nothing in Leonida goes online. While winhttp_offline.cpp owns winhttp.dll, a title can also import Winsock2
// functions directly (both by name and by standard Windows ws2_32.dll ordinal) or resolve them through
// GetProcAddress / WSAIoctl. Bound straight to the host's ws2_32.dll, those calls could open external sockets or
// trigger DNS queries. wsock32.dll exports forward to ws2_32 natively, so its bind/connect/sendto/gethostby* entries
// go to the same wrappers.
//
// ws2_32.dll stays host-owned in rt::shims::owns_dll (partial override, like kernel32.dll), so socket bookkeeping
// (WSAStartup, socket, closesocket, select, setsockopt, recv, send, byte-order helpers, freeaddrinfo) continues to
// run natively on loopback sockets while every outbound, bind, name-resolution, and extension-lookup entry point is
// vetted here:
//   - connect (#4), WSAConnect, WSAConnectByNameA/W, WSAConnectByList, ConnectEx (from WSAIoctl), sendto (#20),
//     WSASendTo, and WSASendMsg (direct and via WSAIoctl) permit only IPv4 loopback (127.0.0.0/8), IPv6 loopback
//     (::1), and IPv4-mapped IPv6 loopback (::ffff:127.0.0.0/104). Every other destination fails with WSAENETUNREACH.
//     WSAConnectByNameA/W pass "localhost" to the host as the literal 127.0.0.1, so the hosts file is never read.
//   - bind (#2) rewrites wildcard binds (0.0.0.0 or ::) to 127.0.0.1 or ::1 so no external peer can connect in,
//     allows explicit loopback binds, and refuses any other address with WSAEADDRNOTAVAIL.
//   - getaddrinfo, GetAddrInfoW, GetAddrInfoExA/W, gethostbyname (#52), gethostbyaddr (#51), and
//     WSAAsyncGetHostByName/Addr (#103/#102) permit only null/empty nodes, "localhost", and loopback IP literals;
//     everything else fails with WSAHOST_NOT_FOUND without sending DNS packets. A dotted octet with a leading zero
//     (0127.0.0.1) is refused: Windows reads it as octal, so it is not the loopback name it looks like.
//   - WSALookupServiceBeginA/W (name-service queries) always fail with WSASERVICE_NOT_FOUND.
//   - getnameinfo / GetNameInfoW force NI_NUMERICHOST for non-loopback addresses (or return WSAHOST_NOT_FOUND when
//     NI_NAMEREQD is set), avoiding reverse DNS lookups.
//   - WSAIoctl wraps WSAID_CONNECTEX and WSAID_WSASENDMSG and refuses SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER
//     with WSAEINVAL so extension functions cannot bypass the checks. The wrappers ask the host for the native
//     pointer of the socket they are called on, since each Winsock provider has its own.
//
// Every host ws2_32.dll call is resolved dynamically from System32 so gta6rt needs no static ws2_32.lib link
// dependency. Each refused destination is logged once ("[rt] ws2: offline, refused <op> <target>") with no payload.
#ifndef _WINSOCK_DEPRECATED_NO_WARNINGS
#define _WINSOCK_DEPRECATED_NO_WARNINGS
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <winsock2.h>
#include <ws2tcpip.h>
#include <mswsock.h>
#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <set>
#include <string>

#include "shims.h"

#ifndef SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER
#define SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER 0xC8000024u
#endif

namespace rt::ws2 {
namespace {

HMODULE host_ws2() {
    static HMODULE mod = LoadLibraryExW(L"ws2_32.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    return mod;
}

template <typename Fn>
Fn host_proc(const char* name) {
    HMODULE mod = host_ws2();
    return mod ? reinterpret_cast<Fn>(GetProcAddress(mod, name)) : nullptr;
}

void set_wsa_error(int err) {
    using SetErrFn = void(WSAAPI*)(int);
    static SetErrFn fn = host_proc<SetErrFn>("WSASetLastError");
    if (fn) {
        fn(err);
    } else {
        SetLastError(static_cast<DWORD>(err));
    }
}

uint16_t read_be16(uint16_t net_val) {
    const auto* b = reinterpret_cast<const uint8_t*>(&net_val);
    return static_cast<uint16_t>((static_cast<uint16_t>(b[0]) << 8) | b[1]);
}

bool is_ipv4_loopback_bytes(const uint8_t b[4]) { return b[0] == 127; }

bool is_ipv4_unspecified_bytes(const uint8_t b[4]) { return b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0; }

bool is_ipv6_loopback_bytes(const uint8_t b[16]) {
    // ::1
    bool exact_loopback = true;
    for (int i = 0; i < 15; ++i) {
        if (b[i] != 0) {
            exact_loopback = false;
            break;
        }
    }
    if (exact_loopback && b[15] == 1) return true;

    // ::ffff:127.0.0.0/104 (IPv4-mapped loopback)
    for (int i = 0; i < 10; ++i) {
        if (b[i] != 0) return false;
    }
    return b[10] == 0xff && b[11] == 0xff && b[12] == 127;
}

bool is_ipv6_unspecified_bytes(const uint8_t b[16]) {
    for (int i = 0; i < 16; ++i) {
        if (b[i] != 0) return false;
    }
    return true;
}

bool is_loopback_sockaddr(const sockaddr* sa, int namelen) {
    if (!sa || namelen < static_cast<int>(sizeof(ADDRESS_FAMILY))) return false;
    if (sa->sa_family == AF_INET) {
        if (namelen < static_cast<int>(sizeof(sockaddr_in))) return false;
        sockaddr_in v4{};
        std::memcpy(&v4, sa, sizeof(v4));
        const auto* b = reinterpret_cast<const uint8_t*>(&v4.sin_addr);
        return is_ipv4_loopback_bytes(b);
    }
    if (sa->sa_family == AF_INET6) {
        if (namelen < static_cast<int>(sizeof(sockaddr_in6))) return false;
        sockaddr_in6 v6{};
        std::memcpy(&v6, sa, sizeof(v6));
        const auto* b = reinterpret_cast<const uint8_t*>(&v6.sin6_addr);
        return is_ipv6_loopback_bytes(b);
    }
    return false;
}

std::string format_sockaddr(const sockaddr* sa, int namelen) {
    if (!sa || namelen < static_cast<int>(sizeof(ADDRESS_FAMILY))) return "<invalid>";
    char buf[96] = {};
    if (sa->sa_family == AF_INET && namelen >= static_cast<int>(sizeof(sockaddr_in))) {
        sockaddr_in v4{};
        std::memcpy(&v4, sa, sizeof(v4));
        const auto* b = reinterpret_cast<const uint8_t*>(&v4.sin_addr);
        std::snprintf(buf, sizeof(buf), "%u.%u.%u.%u:%u", b[0], b[1], b[2], b[3],
                      static_cast<unsigned>(read_be16(v4.sin_port)));
        return buf;
    }
    if (sa->sa_family == AF_INET6 && namelen >= static_cast<int>(sizeof(sockaddr_in6))) {
        sockaddr_in6 v6{};
        std::memcpy(&v6, sa, sizeof(v6));
        const auto* b = reinterpret_cast<const uint8_t*>(&v6.sin6_addr);
        std::snprintf(buf, sizeof(buf), "[%02x%02x:%02x%02x:...:%02x%02x]:%u", b[0], b[1], b[2], b[3], b[14], b[15],
                      static_cast<unsigned>(read_be16(v6.sin6_port)));
        return buf;
    }
    std::snprintf(buf, sizeof(buf), "family=%u", static_cast<unsigned>(sa->sa_family));
    return buf;
}

void log_refused(const char* op, const std::string& target) {
    // Intentionally immortal: mapped-DLL teardown can run after normal statics are destroyed.
    static auto* mutex = new std::mutex;
    static auto* seen = new std::set<std::string>;
    const std::string key = std::string(op) + " " + target;
    std::lock_guard lock(*mutex);
    if (seen->insert(key).second) {
        std::fprintf(stderr, "[rt] ws2: offline, refused %s %s\n", op, target.c_str());
    }
}

bool ascii_iequals(const char* a, const char* b) {
    if (!a || !b) return false;
    while (*a && *b) {
        unsigned char ca = static_cast<unsigned char>(*a++);
        unsigned char cb = static_cast<unsigned char>(*b++);
        if (ca >= 'A' && ca <= 'Z') ca = static_cast<unsigned char>(ca + 32);
        if (cb >= 'A' && cb <= 'Z') cb = static_cast<unsigned char>(cb + 32);
        if (ca != cb) return false;
    }
    return *a == '\0' && *b == '\0';
}

std::string narrow_ascii(const wchar_t* w) {
    std::string out;
    if (!w) return out;
    for (; *w; ++w) out += (*w < 0x80) ? static_cast<char>(*w) : '?';
    return out;
}

bool parse_ipv4_literal(const char* s, uint8_t out[4]) {
    if (!s || !*s) return false;
    const char* p = s;
    for (int part = 0; part < 4; ++part) {
        if (*p < '0' || *p > '9') return false;
        const char* start = p;
        unsigned val = 0;
        int digits = 0;
        while (*p >= '0' && *p <= '9') {
            val = val * 10u + static_cast<unsigned>(*p - '0');
            if (val > 255u) return false;
            ++p;
            ++digits;
        }
        if (digits == 0) return false;
        if (digits > 1 && *start == '0') return false;  // a leading zero is octal to Windows: 0127 is 87, not 127
        out[part] = static_cast<uint8_t>(val);
        if (part < 3) {
            if (*p != '.') return false;
            ++p;
        }
    }
    return *p == '\0';
}

bool is_loopback_node_a(const char* node) {
    if (!node || !*node) return true;
    if (ascii_iequals(node, "localhost") || ascii_iequals(node, "localhost.")) return true;

    uint8_t v4[4] = {};
    if (parse_ipv4_literal(node, v4)) return is_ipv4_loopback_bytes(v4);

    // Strip optional brackets and IPv6 zone suffix (%...) for literal check via host inet_pton.
    std::string clean(node);
    if (clean.size() >= 2 && clean.front() == '[' && clean.back() == ']') {
        clean = clean.substr(1, clean.size() - 2);
    }
    if (const size_t pct = clean.find('%'); pct != std::string::npos) {
        clean.resize(pct);
    }

    using InetPtonFn = INT(WSAAPI*)(INT, PCSTR, PVOID);
    static InetPtonFn pton = host_proc<InetPtonFn>("inet_pton");
    if (pton) {
        uint8_t v6[16] = {};
        if (pton(AF_INET6, clean.c_str(), v6) == 1) return is_ipv6_loopback_bytes(v6);
    } else if (clean == "::1") {
        return true;
    }
    return false;
}

bool is_loopback_node_w(const wchar_t* node) {
    if (!node || !*node) return true;
    for (const wchar_t* p = node; *p; ++p) {
        if (*p >= 0x80) return false;
    }
    const std::string ascii = narrow_ascii(node);
    return is_loopback_node_a(ascii.c_str());
}

bool is_localhost_name_a(const char* node) {
    return ascii_iequals(node, "localhost") || ascii_iequals(node, "localhost.");
}

// ---- Socket connect / send / bind shims ----

int WSAAPI connect_offline(SOCKET s, const sockaddr* name, int namelen) {
    if (!is_loopback_sockaddr(name, namelen)) {
        log_refused("connect", format_sockaddr(name, namelen));
        set_wsa_error(WSAENETUNREACH);
        return SOCKET_ERROR;
    }
    using Fn = int(WSAAPI*)(SOCKET, const sockaddr*, int);
    static Fn real_fn = host_proc<Fn>("connect");
    if (!real_fn) {
        set_wsa_error(WSAENETUNREACH);
        return SOCKET_ERROR;
    }
    return real_fn(s, name, namelen);
}

int WSAAPI wsa_connect_offline(SOCKET s, const sockaddr* name, int namelen, LPWSABUF caller_data,
                               LPWSABUF callee_data, LPQOS sqos, LPQOS gqos) {
    if (!is_loopback_sockaddr(name, namelen)) {
        log_refused("WSAConnect", format_sockaddr(name, namelen));
        set_wsa_error(WSAENETUNREACH);
        return SOCKET_ERROR;
    }
    using Fn = int(WSAAPI*)(SOCKET, const sockaddr*, int, LPWSABUF, LPWSABUF, LPQOS, LPQOS);
    static Fn real_fn = host_proc<Fn>("WSAConnect");
    if (!real_fn) {
        set_wsa_error(WSAENETUNREACH);
        return SOCKET_ERROR;
    }
    return real_fn(s, name, namelen, caller_data, callee_data, sqos, gqos);
}

BOOL WSAAPI wsa_connect_by_name_a_offline(SOCKET s, LPCSTR nodename, LPCSTR servicename,
                                          LPDWORD local_addr_len, LPSOCKADDR local_addr,
                                          LPDWORD remote_addr_len, LPSOCKADDR remote_addr,
                                          const timeval* timeout, LPWSAOVERLAPPED reserved) {
    if (!nodename || !*nodename || !is_loopback_node_a(nodename)) {
        log_refused("WSAConnectByNameA", nodename ? nodename : "<null>");
        set_wsa_error(WSAENETUNREACH);
        return FALSE;
    }
    using Fn = BOOL(WSAAPI*)(SOCKET, LPCSTR, LPCSTR, LPDWORD, LPSOCKADDR, LPDWORD, LPSOCKADDR, const timeval*,
                             LPWSAOVERLAPPED);
    static Fn real_fn = host_proc<Fn>("WSAConnectByNameA");
    if (!real_fn) {
        set_wsa_error(WSAENETUNREACH);
        return FALSE;
    }
    // The host would resolve "localhost" through its hosts file; hand it the literal instead.
    LPCSTR node = is_localhost_name_a(nodename) ? "127.0.0.1" : nodename;
    return real_fn(s, node, servicename, local_addr_len, local_addr, remote_addr_len, remote_addr, timeout, reserved);
}

BOOL WSAAPI wsa_connect_by_name_w_offline(SOCKET s, LPWSTR nodename, LPWSTR servicename,
                                          LPDWORD local_addr_len, LPSOCKADDR local_addr,
                                          LPDWORD remote_addr_len, LPSOCKADDR remote_addr,
                                          const timeval* timeout, LPWSAOVERLAPPED reserved) {
    if (!nodename || !*nodename || !is_loopback_node_w(nodename)) {
        log_refused("WSAConnectByNameW", nodename ? narrow_ascii(nodename) : "<null>");
        set_wsa_error(WSAENETUNREACH);
        return FALSE;
    }
    using Fn = BOOL(WSAAPI*)(SOCKET, LPWSTR, LPWSTR, LPDWORD, LPSOCKADDR, LPDWORD, LPSOCKADDR, const timeval*,
                             LPWSAOVERLAPPED);
    static Fn real_fn = host_proc<Fn>("WSAConnectByNameW");
    if (!real_fn) {
        set_wsa_error(WSAENETUNREACH);
        return FALSE;
    }
    // The host would resolve "localhost" through its hosts file; hand it the literal instead.
    wchar_t loopback[] = L"127.0.0.1";
    LPWSTR node = is_localhost_name_a(narrow_ascii(nodename).c_str()) ? loopback : nodename;
    return real_fn(s, node, servicename, local_addr_len, local_addr, remote_addr_len, remote_addr, timeout, reserved);
}

BOOL WSAAPI wsa_connect_by_list_offline(SOCKET s, PSOCKET_ADDRESS_LIST addr_list, LPDWORD local_addr_len,
                                        LPSOCKADDR local_addr, LPDWORD remote_addr_len, LPSOCKADDR remote_addr,
                                        const timeval* timeout, LPWSAOVERLAPPED reserved) {
    if (!addr_list || addr_list->iAddressCount <= 0) {
        set_wsa_error(WSAEINVAL);
        return FALSE;
    }
    for (INT i = 0; i < addr_list->iAddressCount; ++i) {
        const SOCKET_ADDRESS& entry = addr_list->Address[i];
        if (!is_loopback_sockaddr(entry.lpSockaddr, entry.iSockaddrLength)) {
            log_refused("WSAConnectByList", format_sockaddr(entry.lpSockaddr, entry.iSockaddrLength));
            set_wsa_error(WSAENETUNREACH);
            return FALSE;
        }
    }
    using Fn = BOOL(WSAAPI*)(SOCKET, PSOCKET_ADDRESS_LIST, LPDWORD, LPSOCKADDR, LPDWORD, LPSOCKADDR, const timeval*,
                             LPWSAOVERLAPPED);
    static Fn real_fn = host_proc<Fn>("WSAConnectByList");
    if (!real_fn) {
        set_wsa_error(WSAENETUNREACH);
        return FALSE;
    }
    return real_fn(s, addr_list, local_addr_len, local_addr, remote_addr_len, remote_addr, timeout, reserved);
}

// sendto, WSASendTo, WSASendMsg and WSAJoinLeaf bind an unbound socket to the wildcard address (Microsoft's remarks
// for sendto and WSAJoinLeaf: getsockname then reports 0.0.0.0 or ::), so its port would also take datagrams from other
// hosts. Bind such a socket to the loopback address of the (already checked) loopback destination's family first.
// Returns false, with WSAEADDRNOTAVAIL set, only if the socket is still unbound afterwards.
bool bind_loopback_if_unbound(SOCKET s, const sockaddr* dest) {
    using NameFn = int(WSAAPI*)(SOCKET, sockaddr*, int*);
    using BindFn = int(WSAAPI*)(SOCKET, const sockaddr*, int);
    using LastErrFn = int(WSAAPI*)();
    static NameFn real_getsockname = host_proc<NameFn>("getsockname");
    static BindFn real_bind = host_proc<BindFn>("bind");
    static LastErrFn real_last_error = host_proc<LastErrFn>("WSAGetLastError");
    if (!real_getsockname || !real_bind || !real_last_error) {
        set_wsa_error(WSAEADDRNOTAVAIL);
        return false;
    }
    // Unbound is WSAEINVAL; any other getsockname error (not a socket, ...) is left for the real call to report.
    const auto unbound = [&] {
        sockaddr_storage local{};
        int local_len = sizeof(local);
        return real_getsockname(s, reinterpret_cast<sockaddr*>(&local), &local_len) == SOCKET_ERROR &&
               real_last_error() == WSAEINVAL;
    };
    if (!unbound()) return true;

    sockaddr_storage local{};
    int local_len = 0;
    if (dest->sa_family == AF_INET) {
        auto* v4 = reinterpret_cast<sockaddr_in*>(&local);
        v4->sin_family = AF_INET;
        auto* b = reinterpret_cast<uint8_t*>(&v4->sin_addr);
        b[0] = 127;
        b[3] = 1;
        local_len = sizeof(sockaddr_in);
    } else {
        sockaddr_in6 to6{};
        std::memcpy(&to6, dest, sizeof(to6));
        const auto* d = reinterpret_cast<const uint8_t*>(&to6.sin6_addr);
        auto* v6 = reinterpret_cast<sockaddr_in6*>(&local);
        v6->sin6_family = AF_INET6;
        auto* b = reinterpret_cast<uint8_t*>(&v6->sin6_addr);
        if (d[10] == 0xff && d[11] == 0xff) {  // IPv4-mapped loopback destination: ::ffff:127.0.0.1
            b[10] = 0xff;
            b[11] = 0xff;
            b[12] = 127;
            b[15] = 1;
        } else {  // ::1
            b[15] = 1;
        }
        local_len = sizeof(sockaddr_in6);
    }
    // A concurrent send on the same socket may have bound it between the two checks; that bind came through here too.
    if (real_bind(s, reinterpret_cast<const sockaddr*>(&local), local_len) == 0 || !unbound()) return true;
    log_refused("implicit wildcard bind", format_sockaddr(reinterpret_cast<const sockaddr*>(&local), local_len));
    set_wsa_error(WSAEADDRNOTAVAIL);
    return false;
}

int WSAAPI sendto_offline(SOCKET s, const char* buf, int len, int flags, const sockaddr* to, int tolen) {
    if (to && tolen > 0 && !is_loopback_sockaddr(to, tolen)) {
        log_refused("sendto", format_sockaddr(to, tolen));
        set_wsa_error(WSAENETUNREACH);
        return SOCKET_ERROR;
    }
    if (to && tolen > 0 && !bind_loopback_if_unbound(s, to)) return SOCKET_ERROR;
    using Fn = int(WSAAPI*)(SOCKET, const char*, int, int, const sockaddr*, int);
    static Fn real_fn = host_proc<Fn>("sendto");
    if (!real_fn) {
        set_wsa_error(WSAENETUNREACH);
        return SOCKET_ERROR;
    }
    return real_fn(s, buf, len, flags, to, tolen);
}

int WSAAPI wsa_send_to_offline(SOCKET s, LPWSABUF buffers, DWORD buffer_count, LPDWORD bytes_sent, DWORD flags,
                               const sockaddr* to, int tolen, LPWSAOVERLAPPED overlapped,
                               LPWSAOVERLAPPED_COMPLETION_ROUTINE completion) {
    if (to && tolen > 0 && !is_loopback_sockaddr(to, tolen)) {
        log_refused("WSASendTo", format_sockaddr(to, tolen));
        set_wsa_error(WSAENETUNREACH);
        return SOCKET_ERROR;
    }
    if (to && tolen > 0 && !bind_loopback_if_unbound(s, to)) return SOCKET_ERROR;
    using Fn = int(WSAAPI*)(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, const sockaddr*, int, LPWSAOVERLAPPED,
                            LPWSAOVERLAPPED_COMPLETION_ROUTINE);
    static Fn real_fn = host_proc<Fn>("WSASendTo");
    if (!real_fn) {
        set_wsa_error(WSAENETUNREACH);
        return SOCKET_ERROR;
    }
    return real_fn(s, buffers, buffer_count, bytes_sent, flags, to, tolen, overlapped, completion);
}

int WSAAPI wsa_send_msg_offline(SOCKET s, LPWSAMSG msg, DWORD flags, LPDWORD bytes_sent,
                                LPWSAOVERLAPPED overlapped, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion) {
    if (msg && msg->name && msg->namelen > 0 && !is_loopback_sockaddr(msg->name, msg->namelen)) {
        log_refused("WSASendMsg", format_sockaddr(msg->name, msg->namelen));
        set_wsa_error(WSAENETUNREACH);
        return SOCKET_ERROR;
    }
    if (msg && msg->name && msg->namelen > 0 && !bind_loopback_if_unbound(s, msg->name)) return SOCKET_ERROR;
    using Fn = int(WSAAPI*)(SOCKET, LPWSAMSG, DWORD, LPDWORD, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
    static Fn real_fn = host_proc<Fn>("WSASendMsg");
    if (!real_fn) {
        set_wsa_error(WSAENETUNREACH);
        return SOCKET_ERROR;
    }
    return real_fn(s, msg, flags, bytes_sent, overlapped, completion);
}

int WSAAPI bind_offline(SOCKET s, const sockaddr* name, int namelen) {
    if (!name || namelen < static_cast<int>(sizeof(ADDRESS_FAMILY))) {
        set_wsa_error(WSAEINVAL);
        return SOCKET_ERROR;
    }
    using Fn = int(WSAAPI*)(SOCKET, const sockaddr*, int);
    static Fn real_fn = host_proc<Fn>("bind");
    if (!real_fn) {
        set_wsa_error(WSAEADDRNOTAVAIL);
        return SOCKET_ERROR;
    }

    if (name->sa_family == AF_INET && namelen >= static_cast<int>(sizeof(sockaddr_in))) {
        sockaddr_in v4{};
        std::memcpy(&v4, name, sizeof(v4));
        auto* b = reinterpret_cast<uint8_t*>(&v4.sin_addr);
        if (is_ipv4_unspecified_bytes(b)) {
            b[0] = 127;
            b[1] = 0;
            b[2] = 0;
            b[3] = 1;
            return real_fn(s, reinterpret_cast<const sockaddr*>(&v4), sizeof(v4));
        }
        if (is_ipv4_loopback_bytes(b)) {
            return real_fn(s, name, namelen);
        }
    } else if (name->sa_family == AF_INET6 && namelen >= static_cast<int>(sizeof(sockaddr_in6))) {
        sockaddr_in6 v6{};
        std::memcpy(&v6, name, sizeof(v6));
        auto* b = reinterpret_cast<uint8_t*>(&v6.sin6_addr);
        if (is_ipv6_unspecified_bytes(b)) {
            b[15] = 1;
            return real_fn(s, reinterpret_cast<const sockaddr*>(&v6), sizeof(v6));
        }
        if (is_ipv6_loopback_bytes(b)) {
            return real_fn(s, name, namelen);
        }
    }

    log_refused("bind", format_sockaddr(name, namelen));
    set_wsa_error(WSAEADDRNOTAVAIL);
    return SOCKET_ERROR;
}

// ---- Multicast ----

// A multicast group join makes the host announce membership on a real interface and receive the group's traffic
// from other hosts, so every join is refused. Leaving a group and the other options pass through.
bool is_multicast_join(int level, int optname) {
    if (level == IPPROTO_IP) {
        return optname == IP_ADD_MEMBERSHIP || optname == IP_ADD_SOURCE_MEMBERSHIP || optname == MCAST_JOIN_GROUP ||
               optname == MCAST_JOIN_SOURCE_GROUP;
    }
    if (level == IPPROTO_IPV6) {
        return optname == IPV6_ADD_MEMBERSHIP || optname == MCAST_JOIN_GROUP || optname == MCAST_JOIN_SOURCE_GROUP;
    }
    return false;
}

int refuse_multicast_join(const char* op, int level, int optname) {
    log_refused(op, "multicast join (level " + std::to_string(level) + ", option " + std::to_string(optname) + ")");
    set_wsa_error(WSAEADDRNOTAVAIL);
    return SOCKET_ERROR;
}

int WSAAPI setsockopt_offline(SOCKET s, int level, int optname, const char* optval, int optlen) {
    if (is_multicast_join(level, optname)) return refuse_multicast_join("setsockopt", level, optname);
    using Fn = int(WSAAPI*)(SOCKET, int, int, const char*, int);
    static Fn real_fn = host_proc<Fn>("setsockopt");
    if (!real_fn) {
        set_wsa_error(WSAENETDOWN);
        return SOCKET_ERROR;
    }
    return real_fn(s, level, optname, optval, optlen);
}

// wsock32.dll's setsockopt is its own export, not a forwarder: it takes the Winsock 1.1 IPPROTO_IP numbers, maps
// 5 (winsock.h IP_ADD_MEMBERSHIP) to the Winsock 2 value and passes numbers it does not know through unchanged (Wine's
// dlls/wsock32/socket.c remaps the same table). Refuse both spellings of a join, then let the host wsock32 remap.
int WSAAPI wsock32_setsockopt_offline(SOCKET s, int level, int optname, const char* optval, int optlen) {
    constexpr int kWinsock1AddMembership = 5;
    if (is_multicast_join(level, optname) || (level == IPPROTO_IP && optname == kWinsock1AddMembership)) {
        return refuse_multicast_join("wsock32 setsockopt", level, optname);
    }
    using Fn = int(WSAAPI*)(SOCKET, int, int, const char*, int);
    static HMODULE host_wsock32 = LoadLibraryExW(L"wsock32.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    static Fn real_fn = host_wsock32 ? reinterpret_cast<Fn>(GetProcAddress(host_wsock32, "setsockopt")) : nullptr;
    if (!real_fn) {
        set_wsa_error(WSAENETDOWN);
        return SOCKET_ERROR;
    }
    return real_fn(s, level, optname, optval, optlen);
}

// WSAJoinLeaf joins a multipoint session; a group address is never loopback, so in practice every join is refused.
SOCKET WSAAPI wsa_join_leaf_offline(SOCKET s, const sockaddr* name, int namelen, LPWSABUF caller_data,
                                    LPWSABUF callee_data, LPQOS sqos, LPQOS gqos, DWORD flags) {
    if (!is_loopback_sockaddr(name, namelen)) {
        log_refused("WSAJoinLeaf", format_sockaddr(name, namelen));
        set_wsa_error(WSAENETUNREACH);
        return INVALID_SOCKET;
    }
    if (!bind_loopback_if_unbound(s, name)) return INVALID_SOCKET;
    using Fn = SOCKET(WSAAPI*)(SOCKET, const sockaddr*, int, LPWSABUF, LPWSABUF, LPQOS, LPQOS, DWORD);
    static Fn real_fn = host_proc<Fn>("WSAJoinLeaf");
    if (!real_fn) {
        set_wsa_error(WSAENETUNREACH);
        return INVALID_SOCKET;
    }
    return real_fn(s, name, namelen, caller_data, callee_data, sqos, gqos, flags);
}

// ---- Name resolution shims ----

INT WSAAPI getaddrinfo_offline(PCSTR node, PCSTR service, const ADDRINFOA* hints, PADDRINFOA* result) {
    if (result) *result = nullptr;
    if (!is_loopback_node_a(node)) {
        log_refused("getaddrinfo", node ? node : "<null>");
        set_wsa_error(WSAHOST_NOT_FOUND);
        return WSAHOST_NOT_FOUND;
    }
    using Fn = INT(WSAAPI*)(PCSTR, PCSTR, const ADDRINFOA*, PADDRINFOA*);
    static Fn real_fn = host_proc<Fn>("getaddrinfo");
    if (!real_fn) {
        set_wsa_error(WSAHOST_NOT_FOUND);
        return WSAHOST_NOT_FOUND;
    }
    return real_fn(node, service, hints, result);
}

INT WSAAPI get_addr_info_w_offline(PCWSTR node, PCWSTR service, const ADDRINFOW* hints, PADDRINFOW* result) {
    if (result) *result = nullptr;
    if (!is_loopback_node_w(node)) {
        log_refused("GetAddrInfoW", node ? narrow_ascii(node) : "<null>");
        set_wsa_error(WSAHOST_NOT_FOUND);
        return WSAHOST_NOT_FOUND;
    }
    using Fn = INT(WSAAPI*)(PCWSTR, PCWSTR, const ADDRINFOW*, PADDRINFOW*);
    static Fn real_fn = host_proc<Fn>("GetAddrInfoW");
    if (!real_fn) {
        set_wsa_error(WSAHOST_NOT_FOUND);
        return WSAHOST_NOT_FOUND;
    }
    return real_fn(node, service, hints, result);
}

INT WSAAPI get_addr_info_ex_a_offline(PCSTR name, PCSTR service, DWORD ns, LPGUID nsp_id, const ADDRINFOEXA* hints,
                                      PADDRINFOEXA* result, timeval* timeout, LPOVERLAPPED overlapped,
                                      LPLOOKUPSERVICE_COMPLETION_ROUTINE completion, LPHANDLE cancel_handle) {
    if (result) *result = nullptr;
    if (cancel_handle) *cancel_handle = nullptr;
    if (!is_loopback_node_a(name)) {
        log_refused("GetAddrInfoExA", name ? name : "<null>");
        set_wsa_error(WSAHOST_NOT_FOUND);
        return WSAHOST_NOT_FOUND;
    }
    using Fn = INT(WSAAPI*)(PCSTR, PCSTR, DWORD, LPGUID, const ADDRINFOEXA*, PADDRINFOEXA*, timeval*, LPOVERLAPPED,
                            LPLOOKUPSERVICE_COMPLETION_ROUTINE, LPHANDLE);
    static Fn real_fn = host_proc<Fn>("GetAddrInfoExA");
    if (!real_fn) {
        set_wsa_error(WSAHOST_NOT_FOUND);
        return WSAHOST_NOT_FOUND;
    }
    return real_fn(name, service, ns, nsp_id, hints, result, timeout, overlapped, completion, cancel_handle);
}

INT WSAAPI get_addr_info_ex_w_offline(PCWSTR name, PCWSTR service, DWORD ns, LPGUID nsp_id, const ADDRINFOEXW* hints,
                                      PADDRINFOEXW* result, timeval* timeout, LPOVERLAPPED overlapped,
                                      LPLOOKUPSERVICE_COMPLETION_ROUTINE completion, LPHANDLE cancel_handle) {
    if (result) *result = nullptr;
    if (cancel_handle) *cancel_handle = nullptr;
    if (!is_loopback_node_w(name)) {
        log_refused("GetAddrInfoExW", name ? narrow_ascii(name) : "<null>");
        set_wsa_error(WSAHOST_NOT_FOUND);
        return WSAHOST_NOT_FOUND;
    }
    using Fn = INT(WSAAPI*)(PCWSTR, PCWSTR, DWORD, LPGUID, const ADDRINFOEXW*, PADDRINFOEXW*, timeval*, LPOVERLAPPED,
                            LPLOOKUPSERVICE_COMPLETION_ROUTINE, LPHANDLE);
    static Fn real_fn = host_proc<Fn>("GetAddrInfoExW");
    if (!real_fn) {
        set_wsa_error(WSAHOST_NOT_FOUND);
        return WSAHOST_NOT_FOUND;
    }
    return real_fn(name, service, ns, nsp_id, hints, result, timeout, overlapped, completion, cancel_handle);
}

hostent* WSAAPI gethostbyname_offline(const char* name) {
    if (!is_loopback_node_a(name)) {
        log_refused("gethostbyname", name ? name : "<null>");
        set_wsa_error(WSAHOST_NOT_FOUND);
        return nullptr;
    }
    using Fn = hostent*(WSAAPI*)(const char*);
    static Fn real_fn = host_proc<Fn>("gethostbyname");
    if (!real_fn) {
        set_wsa_error(WSAHOST_NOT_FOUND);
        return nullptr;
    }
    return real_fn(name);
}

bool is_loopback_raw_addr(const char* addr, int len, int type) {
    if (!addr) return false;
    if (type == AF_INET && len == 4) {
        return is_ipv4_loopback_bytes(reinterpret_cast<const uint8_t*>(addr));
    }
    if (type == AF_INET6 && len == 16) {
        return is_ipv6_loopback_bytes(reinterpret_cast<const uint8_t*>(addr));
    }
    return false;
}

hostent* WSAAPI gethostbyaddr_offline(const char* addr, int len, int type) {
    if (!is_loopback_raw_addr(addr, len, type)) {
        log_refused("gethostbyaddr", "non-loopback");
        set_wsa_error(WSAHOST_NOT_FOUND);
        return nullptr;
    }
    using Fn = hostent*(WSAAPI*)(const char*, int, int);
    static Fn real_fn = host_proc<Fn>("gethostbyaddr");
    if (!real_fn) {
        set_wsa_error(WSAHOST_NOT_FOUND);
        return nullptr;
    }
    return real_fn(addr, len, type);
}

HANDLE WSAAPI wsa_async_get_host_by_name_offline(HWND hWnd, u_int wMsg, const char* name, char* buf, int buflen) {
    if (!is_loopback_node_a(name)) {
        log_refused("WSAAsyncGetHostByName", name ? name : "<null>");
        set_wsa_error(WSAHOST_NOT_FOUND);
        return nullptr;
    }
    using Fn = HANDLE(WSAAPI*)(HWND, u_int, const char*, char*, int);
    static Fn real_fn = host_proc<Fn>("WSAAsyncGetHostByName");
    if (!real_fn) {
        set_wsa_error(WSAHOST_NOT_FOUND);
        return nullptr;
    }
    return real_fn(hWnd, wMsg, name, buf, buflen);
}

HANDLE WSAAPI wsa_async_get_host_by_addr_offline(HWND hWnd, u_int wMsg, const char* addr, int len, int type,
                                                 char* buf, int buflen) {
    if (!is_loopback_raw_addr(addr, len, type)) {
        log_refused("WSAAsyncGetHostByAddr", "non-loopback");
        set_wsa_error(WSAHOST_NOT_FOUND);
        return nullptr;
    }
    using Fn = HANDLE(WSAAPI*)(HWND, u_int, const char*, int, int, char*, int);
    static Fn real_fn = host_proc<Fn>("WSAAsyncGetHostByAddr");
    if (!real_fn) {
        set_wsa_error(WSAHOST_NOT_FOUND);
        return nullptr;
    }
    return real_fn(hWnd, wMsg, addr, len, type, buf, buflen);
}

INT WSAAPI getnameinfo_offline(const sockaddr* sa, socklen_t salen, PCHAR host, DWORD host_size, PCHAR serv,
                               DWORD serv_size, INT flags) {
    if (!is_loopback_sockaddr(sa, static_cast<int>(salen))) {
        if ((flags & NI_NAMEREQD) != 0) {
            log_refused("getnameinfo", format_sockaddr(sa, static_cast<int>(salen)));
            set_wsa_error(WSAHOST_NOT_FOUND);
            return WSAHOST_NOT_FOUND;
        }
        flags |= NI_NUMERICHOST;
    }
    using Fn = INT(WSAAPI*)(const sockaddr*, socklen_t, PCHAR, DWORD, PCHAR, DWORD, INT);
    static Fn real_fn = host_proc<Fn>("getnameinfo");
    if (!real_fn) {
        set_wsa_error(WSAHOST_NOT_FOUND);
        return WSAHOST_NOT_FOUND;
    }
    return real_fn(sa, salen, host, host_size, serv, serv_size, flags);
}

INT WSAAPI get_name_info_w_offline(const sockaddr* sa, socklen_t salen, PWCHAR host, DWORD host_size, PWCHAR serv,
                                   DWORD serv_size, INT flags) {
    if (!is_loopback_sockaddr(sa, static_cast<int>(salen))) {
        if ((flags & NI_NAMEREQD) != 0) {
            log_refused("GetNameInfoW", format_sockaddr(sa, static_cast<int>(salen)));
            set_wsa_error(WSAHOST_NOT_FOUND);
            return WSAHOST_NOT_FOUND;
        }
        flags |= NI_NUMERICHOST;
    }
    using Fn = INT(WSAAPI*)(const sockaddr*, socklen_t, PWCHAR, DWORD, PWCHAR, DWORD, INT);
    static Fn real_fn = host_proc<Fn>("GetNameInfoW");
    if (!real_fn) {
        set_wsa_error(WSAHOST_NOT_FOUND);
        return WSAHOST_NOT_FOUND;
    }
    return real_fn(sa, salen, host, host_size, serv, serv_size, flags);
}

// ---- Name-service queries (WSALookupServiceBegin) ----

// Name-service (SAP/RNR) queries would reach the host's providers, so every begin call is refused.
int WSAAPI wsa_lookup_service_begin_a_offline(LPWSAQUERYSETA restrictions, DWORD, LPHANDLE lookup) {
    if (lookup) *lookup = nullptr;
    const bool named = restrictions && restrictions->lpszServiceInstanceName;
    log_refused("WSALookupServiceBeginA", named ? restrictions->lpszServiceInstanceName : "<null>");
    set_wsa_error(WSASERVICE_NOT_FOUND);
    return SOCKET_ERROR;
}

int WSAAPI wsa_lookup_service_begin_w_offline(LPWSAQUERYSETW restrictions, DWORD, LPHANDLE lookup) {
    if (lookup) *lookup = nullptr;
    const bool named = restrictions && restrictions->lpszServiceInstanceName;
    log_refused("WSALookupServiceBeginW", named ? narrow_ascii(restrictions->lpszServiceInstanceName) : "<null>");
    set_wsa_error(WSASERVICE_NOT_FOUND);
    return SOCKET_ERROR;
}

// ---- WSAIoctl extension-function interception (ConnectEx / WSASendMsg) ----

// The provider's own extension function for `s`. Asked per socket: each Winsock provider has its own pointers.
template <typename Fn>
Fn host_extension(SOCKET s, const GUID& guid) {
    using IoctlFn = int(WSAAPI*)(SOCKET, DWORD, LPVOID, DWORD, LPVOID, DWORD, LPDWORD, LPWSAOVERLAPPED,
                                 LPWSAOVERLAPPED_COMPLETION_ROUTINE);
    static IoctlFn ioctl = host_proc<IoctlFn>("WSAIoctl");
    if (!ioctl) return nullptr;
    GUID requested = guid;
    Fn fn = nullptr;
    DWORD returned = 0;
    if (ioctl(s, SIO_GET_EXTENSION_FUNCTION_POINTER, &requested, sizeof(requested), &fn, sizeof(fn), &returned, nullptr,
              nullptr) != 0)
        return nullptr;
    return fn;
}

BOOL PASCAL connect_ex_offline(SOCKET s, const sockaddr* name, int namelen, PVOID send_buf, DWORD send_len,
                               LPDWORD bytes_sent, LPOVERLAPPED overlapped) {
    if (!is_loopback_sockaddr(name, namelen)) {
        log_refused("ConnectEx", format_sockaddr(name, namelen));
        set_wsa_error(WSAENETUNREACH);
        return FALSE;
    }
    static constexpr GUID kConnectExGuid = WSAID_CONNECTEX;
    LPFN_CONNECTEX real_fn = host_extension<LPFN_CONNECTEX>(s, kConnectExGuid);
    if (!real_fn) {
        set_wsa_error(WSAENETUNREACH);
        return FALSE;
    }
    return real_fn(s, name, namelen, send_buf, send_len, bytes_sent, overlapped);
}

int PASCAL wsa_send_msg_ext_offline(SOCKET s, LPWSAMSG msg, DWORD flags, LPDWORD bytes_sent,
                                    LPWSAOVERLAPPED overlapped, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion) {
    if (msg && msg->name && msg->namelen > 0 && !is_loopback_sockaddr(msg->name, msg->namelen)) {
        log_refused("WSASendMsg(ext)", format_sockaddr(msg->name, msg->namelen));
        set_wsa_error(WSAENETUNREACH);
        return SOCKET_ERROR;
    }
    if (msg && msg->name && msg->namelen > 0 && !bind_loopback_if_unbound(s, msg->name)) return SOCKET_ERROR;
    static constexpr GUID kSendMsgGuid = WSAID_WSASENDMSG;
    LPFN_WSASENDMSG real_fn = host_extension<LPFN_WSASENDMSG>(s, kSendMsgGuid);
    if (!real_fn) {
        set_wsa_error(WSAENETUNREACH);
        return SOCKET_ERROR;
    }
    return real_fn(s, msg, flags, bytes_sent, overlapped, completion);
}

int WSAAPI wsa_ioctl_offline(SOCKET s, DWORD io_control_code, LPVOID in_buf, DWORD in_len, LPVOID out_buf,
                             DWORD out_len, LPDWORD bytes_returned, LPWSAOVERLAPPED overlapped,
                             LPWSAOVERLAPPED_COMPLETION_ROUTINE completion) {
    using Fn = int(WSAAPI*)(SOCKET, DWORD, LPVOID, DWORD, LPVOID, DWORD, LPDWORD, LPWSAOVERLAPPED,
                            LPWSAOVERLAPPED_COMPLETION_ROUTINE);
    static Fn real_fn = host_proc<Fn>("WSAIoctl");
    if (!real_fn) {
        set_wsa_error(WSAEINVAL);
        return SOCKET_ERROR;
    }

    if (io_control_code == SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER) {
        set_wsa_error(WSAEINVAL);
        return SOCKET_ERROR;
    }

    if (io_control_code == SIO_GET_EXTENSION_FUNCTION_POINTER && in_buf && in_len >= sizeof(GUID)) {
        static constexpr GUID kConnectExGuid = WSAID_CONNECTEX;
        static constexpr GUID kSendMsgGuid = WSAID_WSASENDMSG;
        GUID requested{};
        std::memcpy(&requested, in_buf, sizeof(requested));

        if (std::memcmp(&requested, &kConnectExGuid, sizeof(GUID)) == 0) {
            if (!out_buf || out_len < sizeof(void*) || !bytes_returned) {
                set_wsa_error(WSAEFAULT);
                return SOCKET_ERROR;
            }
            LPFN_CONNECTEX real_ext = nullptr;
            DWORD returned = 0;
            const int rc = real_fn(s, io_control_code, in_buf, in_len, &real_ext, sizeof(real_ext), &returned,
                                   overlapped, completion);
            if (rc != 0) return rc;
            LPFN_CONNECTEX wrapped = &connect_ex_offline;
            std::memcpy(out_buf, &wrapped, sizeof(wrapped));
            *bytes_returned = sizeof(wrapped);
            return 0;
        }
        if (std::memcmp(&requested, &kSendMsgGuid, sizeof(GUID)) == 0) {
            if (!out_buf || out_len < sizeof(void*) || !bytes_returned) {
                set_wsa_error(WSAEFAULT);
                return SOCKET_ERROR;
            }
            LPFN_WSASENDMSG real_ext = nullptr;
            DWORD returned = 0;
            const int rc = real_fn(s, io_control_code, in_buf, in_len, &real_ext, sizeof(real_ext), &returned,
                                   overlapped, completion);
            if (rc != 0) return rc;
            LPFN_WSASENDMSG wrapped = &wsa_send_msg_ext_offline;
            std::memcpy(out_buf, &wrapped, sizeof(wrapped));
            *bytes_returned = sizeof(wrapped);
            return 0;
        }
    }

    return real_fn(s, io_control_code, in_buf, in_len, out_buf, out_len, bytes_returned, overlapped, completion);
}

}  // namespace
}  // namespace rt::ws2

namespace {
using namespace rt::ws2;
}  // namespace

// Both named exports and the fixed Windows ordinals (#2 bind, #4 connect, #20 sendto, #21 setsockopt,
// #51 gethostbyaddr, #52 gethostbyname, #102 WSAAsyncGetHostByAddr, #103 WSAAsyncGetHostByName) are registered so
// static imports by ordinal and dynamic GetProcAddress lookups resolve to the same offline wrappers. wsock32.dll gets
// the same wrappers for the entries it shares with ws2_32, since those exports forward to ws2_32 natively; its
// setsockopt is its own export (Winsock 1.1 option numbers), so it gets its own wrapper.
SHIM_TABLE(ws2_offline,
    {"ws2_32.dll", "bind", reinterpret_cast<void*>(&bind_offline)},
    {"ws2_32.dll", "#2", reinterpret_cast<void*>(&bind_offline)},
    {"ws2_32.dll", "connect", reinterpret_cast<void*>(&connect_offline)},
    {"ws2_32.dll", "#4", reinterpret_cast<void*>(&connect_offline)},
    {"ws2_32.dll", "sendto", reinterpret_cast<void*>(&sendto_offline)},
    {"ws2_32.dll", "#20", reinterpret_cast<void*>(&sendto_offline)},
    {"ws2_32.dll", "setsockopt", reinterpret_cast<void*>(&setsockopt_offline)},
    {"ws2_32.dll", "#21", reinterpret_cast<void*>(&setsockopt_offline)},
    {"ws2_32.dll", "WSAJoinLeaf", reinterpret_cast<void*>(&wsa_join_leaf_offline)},
    {"ws2_32.dll", "gethostbyaddr", reinterpret_cast<void*>(&gethostbyaddr_offline)},
    {"ws2_32.dll", "#51", reinterpret_cast<void*>(&gethostbyaddr_offline)},
    {"ws2_32.dll", "gethostbyname", reinterpret_cast<void*>(&gethostbyname_offline)},
    {"ws2_32.dll", "#52", reinterpret_cast<void*>(&gethostbyname_offline)},
    {"ws2_32.dll", "WSAAsyncGetHostByAddr", reinterpret_cast<void*>(&wsa_async_get_host_by_addr_offline)},
    {"ws2_32.dll", "#102", reinterpret_cast<void*>(&wsa_async_get_host_by_addr_offline)},
    {"ws2_32.dll", "WSAAsyncGetHostByName", reinterpret_cast<void*>(&wsa_async_get_host_by_name_offline)},
    {"ws2_32.dll", "#103", reinterpret_cast<void*>(&wsa_async_get_host_by_name_offline)},
    {"ws2_32.dll", "getaddrinfo", reinterpret_cast<void*>(&getaddrinfo_offline)},
    {"ws2_32.dll", "GetAddrInfoW", reinterpret_cast<void*>(&get_addr_info_w_offline)},
    {"ws2_32.dll", "GetAddrInfoExA", reinterpret_cast<void*>(&get_addr_info_ex_a_offline)},
    {"ws2_32.dll", "GetAddrInfoExW", reinterpret_cast<void*>(&get_addr_info_ex_w_offline)},
    {"ws2_32.dll", "getnameinfo", reinterpret_cast<void*>(&getnameinfo_offline)},
    {"ws2_32.dll", "GetNameInfoW", reinterpret_cast<void*>(&get_name_info_w_offline)},
    {"ws2_32.dll", "WSAConnect", reinterpret_cast<void*>(&wsa_connect_offline)},
    {"ws2_32.dll", "WSAConnectByNameA", reinterpret_cast<void*>(&wsa_connect_by_name_a_offline)},
    {"ws2_32.dll", "WSAConnectByNameW", reinterpret_cast<void*>(&wsa_connect_by_name_w_offline)},
    {"ws2_32.dll", "WSAConnectByList", reinterpret_cast<void*>(&wsa_connect_by_list_offline)},
    {"ws2_32.dll", "WSASendTo", reinterpret_cast<void*>(&wsa_send_to_offline)},
    {"ws2_32.dll", "WSASendMsg", reinterpret_cast<void*>(&wsa_send_msg_offline)},
    {"ws2_32.dll", "WSAIoctl", reinterpret_cast<void*>(&wsa_ioctl_offline)},
    {"ws2_32.dll", "WSALookupServiceBeginA", reinterpret_cast<void*>(&wsa_lookup_service_begin_a_offline)},
    {"ws2_32.dll", "WSALookupServiceBeginW", reinterpret_cast<void*>(&wsa_lookup_service_begin_w_offline)},
    {"wsock32.dll", "bind", reinterpret_cast<void*>(&bind_offline)},
    {"wsock32.dll", "#2", reinterpret_cast<void*>(&bind_offline)},
    {"wsock32.dll", "connect", reinterpret_cast<void*>(&connect_offline)},
    {"wsock32.dll", "#4", reinterpret_cast<void*>(&connect_offline)},
    {"wsock32.dll", "sendto", reinterpret_cast<void*>(&sendto_offline)},
    {"wsock32.dll", "#20", reinterpret_cast<void*>(&sendto_offline)},
    {"wsock32.dll", "setsockopt", reinterpret_cast<void*>(&wsock32_setsockopt_offline)},
    {"wsock32.dll", "#21", reinterpret_cast<void*>(&wsock32_setsockopt_offline)},
    {"wsock32.dll", "gethostbyaddr", reinterpret_cast<void*>(&gethostbyaddr_offline)},
    {"wsock32.dll", "#51", reinterpret_cast<void*>(&gethostbyaddr_offline)},
    {"wsock32.dll", "gethostbyname", reinterpret_cast<void*>(&gethostbyname_offline)},
    {"wsock32.dll", "#52", reinterpret_cast<void*>(&gethostbyname_offline)},
    {"wsock32.dll", "WSAAsyncGetHostByAddr", reinterpret_cast<void*>(&wsa_async_get_host_by_addr_offline)},
    {"wsock32.dll", "#102", reinterpret_cast<void*>(&wsa_async_get_host_by_addr_offline)},
    {"wsock32.dll", "WSAAsyncGetHostByName", reinterpret_cast<void*>(&wsa_async_get_host_by_name_offline)},
    {"wsock32.dll", "#103", reinterpret_cast<void*>(&wsa_async_get_host_by_name_offline)},
);
