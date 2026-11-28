// Checks the offline Winsock2 (ws2_32.dll) shim (runtime/shims/ws2_offline.cpp).
//
// Resolves every shimmed symbol through rt::shims::find under ws2_32.dll (by name and by standard Windows ordinal),
// checks that wsock32.dll resolves its shared entries to the same wrappers, and verifies that external addresses
// (RFC 5737 192.0.2.1 / RFC 3849 2001:db8::1), .invalid hostnames, octal-looking 0127.0.0.1 and name-service queries
// are refused in-process while loopback (127.0.0.1 / ::1 / localhost) still works. Sends on unbound UDP sockets bind
// them to loopback, and multicast joins (setsockopt on both DLLs, WSAJoinLeaf) are refused. Nothing touches the network.
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

#include "shims.h"

#ifndef SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER
#define SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER 0xC8000024u
#endif

namespace {

int g_checks = 0;
int g_failures = 0;

void check(bool condition, const char* what) {
    ++g_checks;
    std::printf("%s %s\n", condition ? "PASS" : "FAIL", what);
    if (!condition) ++g_failures;
}

template <typename Fn>
Fn resolve(const char* sym) {
    return reinterpret_cast<Fn>(rt::shims::find("ws2_32.dll", sym));
}

sockaddr_in make_v4(uint8_t a, uint8_t b, uint8_t c, uint8_t d, uint16_t port) {
    sockaddr_in sa{};
    sa.sin_family = AF_INET;
    sa.sin_port = htons(port);
    auto* bytes = reinterpret_cast<uint8_t*>(&sa.sin_addr);
    bytes[0] = a;
    bytes[1] = b;
    bytes[2] = c;
    bytes[3] = d;
    return sa;
}

sockaddr_in6 make_v6_doc(uint16_t port) {
    // 2001:db8::1 (RFC 3849 documentation prefix, non-loopback)
    sockaddr_in6 sa{};
    sa.sin6_family = AF_INET6;
    sa.sin6_port = htons(port);
    auto* bytes = reinterpret_cast<uint8_t*>(&sa.sin6_addr);
    bytes[0] = 0x20;
    bytes[1] = 0x01;
    bytes[2] = 0x0d;
    bytes[3] = 0xb8;
    bytes[15] = 0x01;
    return sa;
}

}  // namespace

int main() {
    WSADATA wsa{};
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        std::fprintf(stderr, "WSAStartup failed (%d)\n", WSAGetLastError());
        return 1;
    }

    using BindFn = int(WSAAPI*)(SOCKET, const sockaddr*, int);
    using ConnectFn = int(WSAAPI*)(SOCKET, const sockaddr*, int);
    using SendToFn = int(WSAAPI*)(SOCKET, const char*, int, int, const sockaddr*, int);
    using WsaConnectFn = int(WSAAPI*)(SOCKET, const sockaddr*, int, LPWSABUF, LPWSABUF, LPQOS, LPQOS);
    using WsaConnectByNameAFn = BOOL(WSAAPI*)(SOCKET, LPCSTR, LPCSTR, LPDWORD, LPSOCKADDR, LPDWORD, LPSOCKADDR,
                                              const timeval*, LPWSAOVERLAPPED);
    using WsaConnectByNameWFn = BOOL(WSAAPI*)(SOCKET, LPWSTR, LPWSTR, LPDWORD, LPSOCKADDR, LPDWORD, LPSOCKADDR,
                                              const timeval*, LPWSAOVERLAPPED);
    using WsaConnectByListFn = BOOL(WSAAPI*)(SOCKET, PSOCKET_ADDRESS_LIST, LPDWORD, LPSOCKADDR, LPDWORD, LPSOCKADDR,
                                             const timeval*, LPWSAOVERLAPPED);
    using WsaSendToFn = int(WSAAPI*)(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, const sockaddr*, int, LPWSAOVERLAPPED,
                                     LPWSAOVERLAPPED_COMPLETION_ROUTINE);
    using WsaSendMsgFn = int(WSAAPI*)(SOCKET, LPWSAMSG, DWORD, LPDWORD, LPWSAOVERLAPPED,
                                      LPWSAOVERLAPPED_COMPLETION_ROUTINE);
    using WsaIoctlFn = int(WSAAPI*)(SOCKET, DWORD, LPVOID, DWORD, LPVOID, DWORD, LPDWORD, LPWSAOVERLAPPED,
                                    LPWSAOVERLAPPED_COMPLETION_ROUTINE);
    using GetAddrInfoAFn = INT(WSAAPI*)(PCSTR, PCSTR, const ADDRINFOA*, PADDRINFOA*);
    using GetAddrInfoWFn = INT(WSAAPI*)(PCWSTR, PCWSTR, const ADDRINFOW*, PADDRINFOW*);
    using GetNameInfoAFn = INT(WSAAPI*)(const sockaddr*, socklen_t, PCHAR, DWORD, PCHAR, DWORD, INT);
    using GetHostByNameFn = hostent*(WSAAPI*)(const char*);
    using GetHostByAddrFn = hostent*(WSAAPI*)(const char*, int, int);
    using LookupBeginWFn = int(WSAAPI*)(LPWSAQUERYSETW, DWORD, LPHANDLE);

    const auto sh_bind = resolve<BindFn>("bind");
    const auto sh_bind_ord = resolve<BindFn>("#2");
    const auto sh_connect = resolve<ConnectFn>("connect");
    const auto sh_connect_ord = resolve<ConnectFn>("#4");
    const auto sh_sendto = resolve<SendToFn>("sendto");
    const auto sh_sendto_ord = resolve<SendToFn>("#20");
    const auto sh_wsa_connect = resolve<WsaConnectFn>("WSAConnect");
    const auto sh_connect_name_a = resolve<WsaConnectByNameAFn>("WSAConnectByNameA");
    const auto sh_connect_name_w = resolve<WsaConnectByNameWFn>("WSAConnectByNameW");
    const auto sh_connect_list = resolve<WsaConnectByListFn>("WSAConnectByList");
    const auto sh_wsa_sendto = resolve<WsaSendToFn>("WSASendTo");
    const auto sh_wsa_sendmsg = resolve<WsaSendMsgFn>("WSASendMsg");
    const auto sh_wsa_ioctl = resolve<WsaIoctlFn>("WSAIoctl");
    const auto sh_getaddrinfo = resolve<GetAddrInfoAFn>("getaddrinfo");
    const auto sh_getaddrinfo_w = resolve<GetAddrInfoWFn>("GetAddrInfoW");
    const auto sh_getnameinfo = resolve<GetNameInfoAFn>("getnameinfo");
    const auto sh_gethostbyname = resolve<GetHostByNameFn>("gethostbyname");
    const auto sh_gethostbyname_ord = resolve<GetHostByNameFn>("#52");
    const auto sh_gethostbyaddr = resolve<GetHostByAddrFn>("gethostbyaddr");
    const auto sh_gethostbyaddr_ord = resolve<GetHostByAddrFn>("#51");

    check(sh_bind && sh_bind == sh_bind_ord, "bind and #2 resolve to the same shim");
    check(sh_connect && sh_connect == sh_connect_ord, "connect and #4 resolve to the same shim");
    check(sh_sendto && sh_sendto == sh_sendto_ord, "sendto and #20 resolve to the same shim");
    check(sh_gethostbyname && sh_gethostbyname == sh_gethostbyname_ord, "gethostbyname and #52 resolve to the same shim");
    check(sh_gethostbyaddr && sh_gethostbyaddr == sh_gethostbyaddr_ord, "gethostbyaddr and #51 resolve to the same shim");
    check(sh_wsa_connect && sh_connect_name_a && sh_connect_name_w && sh_connect_list && sh_wsa_sendto &&
              sh_wsa_sendmsg && sh_wsa_ioctl && sh_getaddrinfo && sh_getaddrinfo_w && sh_getnameinfo &&
              rt::shims::find("ws2_32.dll", "#102") && rt::shims::find("ws2_32.dll", "#103"),
          "all offline Winsock2 shims resolve through the registry");
    check(!rt::shims::owns_dll("ws2_32.dll"),
          "ws2_32.dll stays partially overridden (host socket bookkeeping remains available)");
    check(!rt::shims::owns_dll("wsock32.dll"),
          "wsock32.dll stays partially overridden (host socket bookkeeping remains available)");

    // wsock32.dll resolves its shared entries to the same wrappers as ws2_32.dll, by name and by ordinal.
    check(rt::shims::find("wsock32.dll", "connect") == sh_connect && rt::shims::find("wsock32.dll", "#4") == sh_connect,
          "wsock32.dll connect and #4 resolve to the ws2_32.dll connect shim");
    check(rt::shims::find("wsock32.dll", "bind") == sh_bind && rt::shims::find("wsock32.dll", "#2") == sh_bind,
          "wsock32.dll bind and #2 resolve to the ws2_32.dll bind shim");
    check(rt::shims::find("wsock32.dll", "gethostbyname") == sh_gethostbyname &&
              rt::shims::find("wsock32.dll", "#52") == sh_gethostbyname,
          "wsock32.dll gethostbyname and #52 resolve to the ws2_32.dll shim");

    // 1. External connect / WSAConnect / WSAConnectByName / WSAConnectByList refused with WSAENETUNREACH.
    {
        const SOCKET tcp = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        check(tcp != INVALID_SOCKET, "create IPv4 TCP socket");
        const sockaddr_in ext4 = make_v4(192, 0, 2, 1, 443);
        const sockaddr_in6 ext6 = make_v6_doc(443);

        WSASetLastError(0);
        check(sh_connect(tcp, reinterpret_cast<const sockaddr*>(&ext4), sizeof(ext4)) == SOCKET_ERROR &&
                  WSAGetLastError() == WSAENETUNREACH,
              "connect refuses external IPv4 with WSAENETUNREACH");

        WSASetLastError(0);
        check(sh_connect(tcp, reinterpret_cast<const sockaddr*>(&ext6), sizeof(ext6)) == SOCKET_ERROR &&
                  WSAGetLastError() == WSAENETUNREACH,
              "connect refuses external IPv6 with WSAENETUNREACH");

        WSASetLastError(0);
        check(sh_wsa_connect(tcp, reinterpret_cast<const sockaddr*>(&ext4), sizeof(ext4), nullptr, nullptr, nullptr,
                             nullptr) == SOCKET_ERROR &&
                  WSAGetLastError() == WSAENETUNREACH,
              "WSAConnect refuses external IPv4 with WSAENETUNREACH");

        WSASetLastError(0);
        check(sh_connect_name_a(tcp, "example.invalid", "443", nullptr, nullptr, nullptr, nullptr, nullptr, nullptr) ==
                      FALSE &&
                  WSAGetLastError() == WSAENETUNREACH,
              "WSAConnectByNameA refuses external hostname with WSAENETUNREACH");

        wchar_t bad_host[] = L"example.invalid";
        wchar_t port_w[] = L"443";
        WSASetLastError(0);
        check(sh_connect_name_w(tcp, bad_host, port_w, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr) == FALSE &&
                  WSAGetLastError() == WSAENETUNREACH,
              "WSAConnectByNameW refuses external hostname with WSAENETUNREACH");

        SOCKET_ADDRESS_LIST list{};
        sockaddr_in ext4_mut = ext4;
        list.iAddressCount = 1;
        list.Address[0].lpSockaddr = reinterpret_cast<LPSOCKADDR>(&ext4_mut);
        list.Address[0].iSockaddrLength = sizeof(ext4_mut);
        WSASetLastError(0);
        check(sh_connect_list(tcp, &list, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr) == FALSE &&
                  WSAGetLastError() == WSAENETUNREACH,
              "WSAConnectByList refuses non-loopback address list with WSAENETUNREACH");

        // ConnectEx via WSAIoctl is wrapped and refuses external targets.
        GUID connect_ex_guid = WSAID_CONNECTEX;
        LPFN_CONNECTEX connect_ex = nullptr;
        DWORD returned = 0;
        const int ioctl_rc = sh_wsa_ioctl(tcp, SIO_GET_EXTENSION_FUNCTION_POINTER, &connect_ex_guid,
                                          sizeof(connect_ex_guid), &connect_ex, sizeof(connect_ex), &returned, nullptr,
                                          nullptr);
        check(ioctl_rc == 0 && connect_ex != nullptr && returned == sizeof(connect_ex),
              "WSAIoctl returns wrapped ConnectEx");
        if (connect_ex) {
            OVERLAPPED ov{};
            WSASetLastError(0);
            check(connect_ex(tcp, reinterpret_cast<const sockaddr*>(&ext4), sizeof(ext4), nullptr, 0, nullptr, &ov) ==
                          FALSE &&
                      WSAGetLastError() == WSAENETUNREACH,
                  "wrapped ConnectEx refuses external IPv4 with WSAENETUNREACH");
        }

        // SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER is refused.
        DWORD dummy_ret = 0;
        WSASetLastError(0);
        check(sh_wsa_ioctl(tcp, SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER, nullptr, 0, nullptr, 0, &dummy_ret,
                           nullptr, nullptr) == SOCKET_ERROR &&
                  WSAGetLastError() == WSAEINVAL,
              "WSAIoctl refuses SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER");

        closesocket(tcp);
    }

    // 2. External datagram sends (sendto, WSASendTo, WSASendMsg direct & via WSAIoctl) refused with WSAENETUNREACH.
    {
        const SOCKET udp = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        check(udp != INVALID_SOCKET, "create IPv4 UDP socket");
        const sockaddr_in ext4 = make_v4(192, 0, 2, 2, 53);
        char payload[] = "ping";

        WSASetLastError(0);
        check(sh_sendto(udp, payload, 4, 0, reinterpret_cast<const sockaddr*>(&ext4), sizeof(ext4)) == SOCKET_ERROR &&
                  WSAGetLastError() == WSAENETUNREACH,
              "sendto refuses external IPv4 with WSAENETUNREACH");

        WSABUF wbuf{4, payload};
        DWORD sent = 0;
        WSASetLastError(0);
        check(sh_wsa_sendto(udp, &wbuf, 1, &sent, 0, reinterpret_cast<const sockaddr*>(&ext4), sizeof(ext4), nullptr,
                            nullptr) == SOCKET_ERROR &&
                  WSAGetLastError() == WSAENETUNREACH,
              "WSASendTo refuses external IPv4 with WSAENETUNREACH");

        sockaddr_in ext4_mut = ext4;
        WSAMSG msg{};
        msg.name = reinterpret_cast<LPSOCKADDR>(&ext4_mut);
        msg.namelen = sizeof(ext4_mut);
        msg.lpBuffers = &wbuf;
        msg.dwBufferCount = 1;
        WSASetLastError(0);
        check(sh_wsa_sendmsg(udp, &msg, 0, &sent, nullptr, nullptr) == SOCKET_ERROR &&
                  WSAGetLastError() == WSAENETUNREACH,
              "WSASendMsg refuses external IPv4 with WSAENETUNREACH");

        GUID sendmsg_guid = WSAID_WSASENDMSG;
        LPFN_WSASENDMSG sendmsg_ext = nullptr;
        DWORD returned = 0;
        const int ioctl_rc = sh_wsa_ioctl(udp, SIO_GET_EXTENSION_FUNCTION_POINTER, &sendmsg_guid, sizeof(sendmsg_guid),
                                          &sendmsg_ext, sizeof(sendmsg_ext), &returned, nullptr, nullptr);
        check(ioctl_rc == 0 && sendmsg_ext != nullptr, "WSAIoctl returns wrapped WSASendMsg");
        if (sendmsg_ext) {
            WSASetLastError(0);
            check(sendmsg_ext(udp, &msg, 0, &sent, nullptr, nullptr) == SOCKET_ERROR &&
                      WSAGetLastError() == WSAENETUNREACH,
                  "wrapped WSASendMsg extension refuses external IPv4 with WSAENETUNREACH");
        }

        closesocket(udp);
    }

    // 3. bind narrows wildcard 0.0.0.0 to 127.0.0.1 and refuses non-loopback addresses.
    {
        const SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        const sockaddr_in ext4 = make_v4(192, 0, 2, 1, 0);
        WSASetLastError(0);
        check(sh_bind(s, reinterpret_cast<const sockaddr*>(&ext4), sizeof(ext4)) == SOCKET_ERROR &&
                  WSAGetLastError() == WSAEADDRNOTAVAIL,
              "bind refuses non-loopback IPv4 with WSAEADDRNOTAVAIL");

        const sockaddr_in any4 = make_v4(0, 0, 0, 0, 0);
        check(sh_bind(s, reinterpret_cast<const sockaddr*>(&any4), sizeof(any4)) == 0,
              "bind accepts wildcard 0.0.0.0 and rewrites to loopback");
        sockaddr_in bound{};
        int bound_len = sizeof(bound);
        check(getsockname(s, reinterpret_cast<sockaddr*>(&bound), &bound_len) == 0 &&
                  bound.sin_addr.s_addr == htonl(INADDR_LOOPBACK),
              "wildcard bind narrowed socket address to 127.0.0.1");
        closesocket(s);
    }

    // 4. DNS lookups refuse external hostnames and permit localhost / 127.0.0.1.
    {
        ADDRINFOA* res = nullptr;
        check(sh_getaddrinfo("example.invalid", "443", nullptr, &res) == WSAHOST_NOT_FOUND && res == nullptr,
              "getaddrinfo refuses external hostname with WSAHOST_NOT_FOUND");
        // Windows reads a leading-zero octet as octal, so 0127.0.0.1 is 87.0.0.1 there; it is refused, not loopback.
        res = nullptr;
        check(sh_getaddrinfo("0127.0.0.1", "80", nullptr, &res) == WSAHOST_NOT_FOUND && res == nullptr,
              "getaddrinfo refuses octal-looking 0127.0.0.1 with WSAHOST_NOT_FOUND");
        check(sh_getaddrinfo("127.0.0.1", "80", nullptr, &res) == 0 && res != nullptr,
              "getaddrinfo permits 127.0.0.1");
        if (res) freeaddrinfo(res);
        res = nullptr;
        check(sh_getaddrinfo("localhost", "80", nullptr, &res) == 0 && res != nullptr,
              "getaddrinfo permits localhost");
        if (res) freeaddrinfo(res);

        ADDRINFOW* res_w = nullptr;
        check(sh_getaddrinfo_w(L"example.invalid", L"443", nullptr, &res_w) == WSAHOST_NOT_FOUND && res_w == nullptr,
              "GetAddrInfoW refuses external hostname with WSAHOST_NOT_FOUND");
        check(sh_getaddrinfo_w(L"localhost", L"80", nullptr, &res_w) == 0 && res_w != nullptr,
              "GetAddrInfoW permits localhost");
        if (res_w) FreeAddrInfoW(res_w);

        WSASetLastError(0);
        check(sh_gethostbyname("example.invalid") == nullptr && WSAGetLastError() == WSAHOST_NOT_FOUND,
              "gethostbyname refuses external hostname with WSAHOST_NOT_FOUND");

        const uint8_t ext_bytes[4] = {192, 0, 2, 1};
        WSASetLastError(0);
        check(sh_gethostbyaddr(reinterpret_cast<const char*>(ext_bytes), 4, AF_INET) == nullptr &&
                  WSAGetLastError() == WSAHOST_NOT_FOUND,
              "gethostbyaddr refuses external IPv4 with WSAHOST_NOT_FOUND");

        const sockaddr_in ext4 = make_v4(192, 0, 2, 1, 80);
        char host_buf[64] = {};
        check(sh_getnameinfo(reinterpret_cast<const sockaddr*>(&ext4), sizeof(ext4), host_buf, sizeof(host_buf),
                             nullptr, 0, NI_NAMEREQD) == WSAHOST_NOT_FOUND,
              "getnameinfo with NI_NAMEREQD refuses external IPv4");
        check(sh_getnameinfo(reinterpret_cast<const sockaddr*>(&ext4), sizeof(ext4), host_buf, sizeof(host_buf),
                             nullptr, 0, 0) == 0 &&
                  std::strcmp(host_buf, "192.0.2.1") == 0,
              "getnameinfo without NI_NAMEREQD returns numeric IP without reverse DNS");
    }

    // 5. Real loopback UDP round-trip through the shimmed bind and sendto succeeds.
    {
        const SOCKET recv_sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        const SOCKET send_sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        const sockaddr_in loop = make_v4(127, 0, 0, 1, 0);
        check(sh_bind(recv_sock, reinterpret_cast<const sockaddr*>(&loop), sizeof(loop)) == 0,
              "loopback UDP receiver bind succeeds");
        sockaddr_in dst{};
        int dst_len = sizeof(dst);
        check(getsockname(recv_sock, reinterpret_cast<sockaddr*>(&dst), &dst_len) == 0,
              "getsockname returns loopback UDP port");
        const char msg[] = "ok";
        check(sh_sendto(send_sock, msg, 2, 0, reinterpret_cast<const sockaddr*>(&dst), sizeof(dst)) == 2,
              "loopback sendto succeeds");
        char got[8] = {};
        const int n = recv(recv_sock, got, sizeof(got), 0);
        check(n == 2 && got[0] == 'o' && got[1] == 'k', "loopback UDP payload received");
        closesocket(send_sock);
        closesocket(recv_sock);
    }

    // 6. Name-service queries are refused with WSASERVICE_NOT_FOUND.
    {
        const auto sh_lookup_w = resolve<LookupBeginWFn>("WSALookupServiceBeginW");
        wchar_t service[] = L"example.invalid";
        WSAQUERYSETW restrictions{};
        restrictions.dwSize = sizeof(restrictions);
        restrictions.lpszServiceInstanceName = service;
        HANDLE lookup = nullptr;
        WSASetLastError(0);
        check(sh_lookup_w && sh_lookup_w(&restrictions, 0, &lookup) == SOCKET_ERROR &&
                  WSAGetLastError() == WSASERVICE_NOT_FOUND && lookup == nullptr,
              "WSALookupServiceBeginW refuses with WSASERVICE_NOT_FOUND");
    }

    // 7. ConnectEx from WSAIoctl connects to a loopback listener through the host's per-socket pointer.
    {
        const SOCKET listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        sockaddr_in any = make_v4(127, 0, 0, 1, 0);
        sockaddr_in listen_addr{};
        int listen_len = sizeof(listen_addr);
        check(sh_bind(listener, reinterpret_cast<const sockaddr*>(&any), sizeof(any)) == 0 &&
                  listen(listener, 1) == 0 && getsockname(listener, reinterpret_cast<sockaddr*>(&listen_addr),
                                                          &listen_len) == 0,
              "loopback TCP listener is up");

        const SOCKET client = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        check(sh_bind(client, reinterpret_cast<const sockaddr*>(&any), sizeof(any)) == 0,
              "ConnectEx client socket is bound to loopback (ConnectEx requires a bound socket)");
        GUID connect_ex_guid = WSAID_CONNECTEX;
        LPFN_CONNECTEX connect_ex = nullptr;
        DWORD returned = 0;
        check(sh_wsa_ioctl(client, SIO_GET_EXTENSION_FUNCTION_POINTER, &connect_ex_guid, sizeof(connect_ex_guid),
                           &connect_ex, sizeof(connect_ex), &returned, nullptr, nullptr) == 0 &&
                  connect_ex != nullptr,
              "WSAIoctl returns a ConnectEx pointer for a bound TCP socket");

        if (connect_ex) {
            OVERLAPPED ov{};
            ov.hEvent = WSACreateEvent();
            check(connect_ex(client, reinterpret_cast<const sockaddr*>(&listen_addr), sizeof(listen_addr), nullptr, 0,
                             nullptr, &ov) == TRUE ||
                      (WSAGetLastError() == WSA_IO_PENDING &&
                       WSAWaitForMultipleEvents(1, &ov.hEvent, TRUE, 5000, FALSE) == WSA_WAIT_EVENT_0),
                  "wrapped ConnectEx connects to the loopback listener");
            DWORD transferred = 0, flags = 0;
            check(WSAGetOverlappedResult(client, &ov, &transferred, FALSE, &flags) == TRUE,
                  "ConnectEx completes with no error");
            WSACloseEvent(ov.hEvent);
        }

        const SOCKET ext = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        sockaddr_in ext_any = make_v4(127, 0, 0, 1, 0);
        check(sh_bind(ext, reinterpret_cast<const sockaddr*>(&ext_any), sizeof(ext_any)) == 0,
              "second ConnectEx socket is bound to loopback");
        if (connect_ex) {
            const sockaddr_in ext4 = make_v4(192, 0, 2, 1, 443);
            OVERLAPPED ov{};
            WSASetLastError(0);
            check(connect_ex(ext, reinterpret_cast<const sockaddr*>(&ext4), sizeof(ext4), nullptr, 0, nullptr, &ov) ==
                          FALSE &&
                      WSAGetLastError() == WSAENETUNREACH,
                  "wrapped ConnectEx refuses non-loopback 192.0.2.1 with WSAENETUNREACH");
        }
        closesocket(ext);
        closesocket(client);
        closesocket(listener);
    }

    // Implicit binds, multicast joins and WSAJoinLeaf. A send on an unbound datagram socket binds it to the wildcard
    // address (Microsoft's sendto remarks); the shim binds it to loopback first. Joins are refused before the host.
    {
        using SetSockOptFn = int(WSAAPI*)(SOCKET, int, int, const char*, int);
        using JoinLeafFn = SOCKET(WSAAPI*)(SOCKET, const sockaddr*, int, LPWSABUF, LPWSABUF, LPQOS, LPQOS, DWORD);
        const auto sh_setsockopt = resolve<SetSockOptFn>("setsockopt");
        const auto sh_join_leaf = resolve<JoinLeafFn>("WSAJoinLeaf");
        const auto ws1_setsockopt = reinterpret_cast<SetSockOptFn>(rt::shims::find("wsock32.dll", "setsockopt"));
        check(sh_setsockopt && sh_setsockopt == resolve<SetSockOptFn>("#21"),
              "setsockopt and #21 resolve to the same shim");
        check(sh_join_leaf != nullptr, "WSAJoinLeaf resolves to a shim");
        check(ws1_setsockopt && ws1_setsockopt == reinterpret_cast<SetSockOptFn>(rt::shims::find("wsock32.dll", "#21")) &&
                  ws1_setsockopt != sh_setsockopt,
              "wsock32.dll setsockopt and #21 resolve to their own Winsock 1.1 shim");

        const SOCKET receiver = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        sockaddr_in to = make_v4(127, 0, 0, 1, 0);
        int to_len = sizeof(to);
        check(bind(receiver, reinterpret_cast<const sockaddr*>(&to), sizeof(to)) == 0 &&
                  getsockname(receiver, reinterpret_cast<sockaddr*>(&to), &to_len) == 0,
              "bind a loopback UDP receiver");
        const auto local_v4_first_byte = [](SOCKET s) {
            sockaddr_in local{};
            int len = sizeof(local);
            if (getsockname(s, reinterpret_cast<sockaddr*>(&local), &len) != 0 || local.sin_port == 0) return -1;
            return static_cast<int>(reinterpret_cast<const uint8_t*>(&local.sin_addr)[0]);
        };
        char payload[] = "ping";

        const SOCKET control = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        check(sendto(control, payload, 4, 0, reinterpret_cast<const sockaddr*>(&to), sizeof(to)) == 4 &&
                  local_v4_first_byte(control) == 0,
              "control: the host sendto binds an unbound UDP socket to 0.0.0.0");
        const SOCKET unbound = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        check(sh_sendto(unbound, payload, 4, 0, reinterpret_cast<const sockaddr*>(&to), sizeof(to)) == 4 &&
                  local_v4_first_byte(unbound) == 127,
              "sendto binds an unbound UDP socket to 127.0.0.1 before sending");
        const SOCKET unbound_wsa = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        WSABUF wbuf{4, payload};
        DWORD sent = 0;
        check(sh_wsa_sendto(unbound_wsa, &wbuf, 1, &sent, 0, reinterpret_cast<const sockaddr*>(&to), sizeof(to), nullptr,
                            nullptr) == 0 &&
                  sent == 4 && local_v4_first_byte(unbound_wsa) == 127,
              "WSASendTo binds an unbound UDP socket to 127.0.0.1 before sending");
        check(sh_sendto(unbound, payload, 4, 0, reinterpret_cast<const sockaddr*>(&to), sizeof(to)) == 4 &&
                  local_v4_first_byte(unbound) == 127,
              "a second sendto on the now-bound socket keeps its loopback binding");

        const SOCKET receiver6 = socket(AF_INET6, SOCK_DGRAM, IPPROTO_UDP);
        if (receiver6 != INVALID_SOCKET) {
            sockaddr_in6 to6{};
            to6.sin6_family = AF_INET6;
            reinterpret_cast<uint8_t*>(&to6.sin6_addr)[15] = 1;  // ::1
            int to6_len = sizeof(to6);
            const bool bound6 = bind(receiver6, reinterpret_cast<const sockaddr*>(&to6), sizeof(to6)) == 0 &&
                                getsockname(receiver6, reinterpret_cast<sockaddr*>(&to6), &to6_len) == 0;
            const SOCKET unbound6 = socket(AF_INET6, SOCK_DGRAM, IPPROTO_UDP);
            sockaddr_in6 local6{};
            int local6_len = sizeof(local6);
            check(bound6 && sh_sendto(unbound6, payload, 4, 0, reinterpret_cast<const sockaddr*>(&to6), sizeof(to6)) == 4 &&
                      getsockname(unbound6, reinterpret_cast<sockaddr*>(&local6), &local6_len) == 0 &&
                      reinterpret_cast<const uint8_t*>(&local6.sin6_addr)[15] == 1 &&
                      reinterpret_cast<const uint8_t*>(&local6.sin6_addr)[0] == 0,
                  "sendto binds an unbound IPv6 UDP socket to ::1 before sending");
            closesocket(unbound6);
            closesocket(receiver6);
        }

        // 239.255.0.1 (administratively scoped group); every join is refused before it reaches the host.
        ip_mreq mreq{};
        auto* group = reinterpret_cast<uint8_t*>(&mreq.imr_multiaddr);
        group[0] = 239;
        group[1] = 255;
        group[3] = 1;
        const auto refused = [](int rc) { return rc == SOCKET_ERROR && WSAGetLastError() == WSAEADDRNOTAVAIL; };
        const char* mreq_bytes = reinterpret_cast<const char*>(&mreq);
        WSASetLastError(0);
        check(refused(sh_setsockopt(unbound, IPPROTO_IP, IP_ADD_MEMBERSHIP, mreq_bytes, sizeof(mreq))),
              "setsockopt refuses IP_ADD_MEMBERSHIP with WSAEADDRNOTAVAIL");
        WSASetLastError(0);
        check(refused(sh_setsockopt(unbound, IPPROTO_IP, IP_ADD_SOURCE_MEMBERSHIP, mreq_bytes, sizeof(mreq))),
              "setsockopt refuses IP_ADD_SOURCE_MEMBERSHIP");
        WSASetLastError(0);
        check(refused(sh_setsockopt(unbound, IPPROTO_IP, MCAST_JOIN_GROUP, mreq_bytes, sizeof(mreq))),
              "setsockopt refuses MCAST_JOIN_GROUP");
        WSASetLastError(0);
        check(refused(sh_setsockopt(unbound, IPPROTO_IPV6, IPV6_ADD_MEMBERSHIP, mreq_bytes, sizeof(mreq))),
              "setsockopt refuses IPV6_ADD_MEMBERSHIP");
        WSASetLastError(0);
        check(refused(ws1_setsockopt(unbound, IPPROTO_IP, 5, mreq_bytes, sizeof(mreq))),
              "wsock32 setsockopt refuses the Winsock 1.1 IP_ADD_MEMBERSHIP (5)");
        WSASetLastError(0);
        check(refused(ws1_setsockopt(unbound, IPPROTO_IP, IP_ADD_MEMBERSHIP, mreq_bytes, sizeof(mreq))),
              "wsock32 setsockopt refuses the Winsock 2 IP_ADD_MEMBERSHIP (12) it would pass through");
        int value = 3;
        const char* value_bytes = reinterpret_cast<const char*>(&value);
        check(sh_setsockopt(unbound, IPPROTO_IP, IP_MULTICAST_TTL, value_bytes, sizeof(value)) == 0,
              "setsockopt passes other IPPROTO_IP options (IP_MULTICAST_TTL)");
        check(ws1_setsockopt(unbound, IPPROTO_IP, 7, value_bytes, sizeof(value)) == 0,
              "wsock32 setsockopt passes the Winsock 1.1 IP_TTL (7) to the host's remap");
        value = 65536;
        check(sh_setsockopt(unbound, SOL_SOCKET, SO_RCVBUF, value_bytes, sizeof(value)) == 0,
              "setsockopt passes SOL_SOCKET options");

        const sockaddr_in group_addr = make_v4(239, 255, 0, 1, 5000);
        WSASetLastError(0);
        check(sh_join_leaf(unbound, reinterpret_cast<const sockaddr*>(&group_addr), sizeof(group_addr), nullptr, nullptr,
                           nullptr, nullptr, JL_BOTH) == INVALID_SOCKET &&
                  WSAGetLastError() == WSAENETUNREACH,
              "WSAJoinLeaf refuses a multicast group with WSAENETUNREACH");

        closesocket(unbound_wsa);
        closesocket(unbound);
        closesocket(control);
        closesocket(receiver);
    }

    WSACleanup();
    std::printf("%d check(s), %d failure(s)\n", g_checks, g_failures);
    return g_failures ? 1 : 0;
}
