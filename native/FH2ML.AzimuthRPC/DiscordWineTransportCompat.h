#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <tlhelp32.h>

#include <climits>
#include <cstdint>
#include <cstdlib>
#include <cwchar>
#include <mutex>
#include <new>
#include <string>
#include <unordered_map>

namespace azimuthrpc::winecompat
{
constexpr char kBridgeEnvironmentVariable[] = "AZIMUTHRPC_DISCORD_TCP";
constexpr wchar_t kDiscordPipePrefix[] = L"\\\\.\\pipe\\discord-ipc-";

struct SocketToken
{
};

inline std::mutex& SocketRegistryMutex()
{
    static std::mutex mutex;
    return mutex;
}

inline std::unordered_map<HANDLE, SOCKET>& SocketRegistry()
{
    static std::unordered_map<HANDLE, SOCKET> sockets;
    return sockets;
}

inline bool IsDiscordPipePath(const wchar_t* path)
{
    if (path == nullptr)
        return false;

    constexpr std::size_t prefixLength = (sizeof(kDiscordPipePrefix) / sizeof(wchar_t)) - 1;
    return std::wcsncmp(path, kDiscordPipePrefix, prefixLength) == 0;
}

inline bool TryGetBridgePort(unsigned short& port)
{
    char value[128]{};
    const DWORD length = GetEnvironmentVariableA(
        kBridgeEnvironmentVariable,
        value,
        static_cast<DWORD>(sizeof(value)));

    if (length == 0 || length >= sizeof(value))
        return false;

    std::string endpoint(value, length);
    const std::size_t separator = endpoint.find_last_of(':');
    if (separator == std::string::npos || separator == 0 || separator + 1 >= endpoint.size())
        return false;

    const std::string host = endpoint.substr(0, separator);
    if (host != "127.0.0.1" && host != "localhost")
        return false;

    char* end = nullptr;
    const long parsed = std::strtol(endpoint.c_str() + separator + 1, &end, 10);
    if (end == nullptr || *end != '\0' || parsed < 1 || parsed > 65535)
        return false;

    port = static_cast<unsigned short>(parsed);
    return true;
}

inline bool EnsureWinsock()
{
    static const bool initialized = []
    {
        WSADATA data{};
        return WSAStartup(MAKEWORD(2, 2), &data) == 0;
    }();

    return initialized;
}

inline HANDLE RegisterSocket(const SOCKET socket)
{
    auto* token = new (std::nothrow) SocketToken{};
    if (token == nullptr)
    {
        closesocket(socket);
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return INVALID_HANDLE_VALUE;
    }

    const HANDLE handle = reinterpret_cast<HANDLE>(token);
    {
        std::lock_guard lock(SocketRegistryMutex());
        SocketRegistry().emplace(handle, socket);
    }

    return handle;
}

inline bool TryGetSocket(const HANDLE handle, SOCKET& socket)
{
    std::lock_guard lock(SocketRegistryMutex());
    const auto found = SocketRegistry().find(handle);
    if (found == SocketRegistry().end())
        return false;

    socket = found->second;
    return true;
}

inline bool TakeSocket(const HANDLE handle, SOCKET& socket)
{
    {
        std::lock_guard lock(SocketRegistryMutex());
        const auto found = SocketRegistry().find(handle);
        if (found == SocketRegistry().end())
            return false;

        socket = found->second;
        SocketRegistry().erase(found);
    }

    delete reinterpret_cast<SocketToken*>(handle);
    return true;
}

inline HANDLE OpenBridgeSocket(const unsigned short port)
{
    if (!EnsureWinsock())
    {
        SetLastError(ERROR_NETWORK_UNREACHABLE);
        return INVALID_HANDLE_VALUE;
    }

    const SOCKET socket = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (socket == INVALID_SOCKET)
    {
        SetLastError(ERROR_NETWORK_UNREACHABLE);
        return INVALID_HANDLE_VALUE;
    }

    sockaddr_in endpoint{};
    endpoint.sin_family = AF_INET;
    endpoint.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    endpoint.sin_port = htons(port);

    if (::connect(socket, reinterpret_cast<const sockaddr*>(&endpoint), sizeof(endpoint)) == SOCKET_ERROR)
    {
        closesocket(socket);
        SetLastError(ERROR_PIPE_NOT_CONNECTED);
        return INVALID_HANDLE_VALUE;
    }

    const BOOL noDelay = TRUE;
    setsockopt(
        socket,
        IPPROTO_TCP,
        TCP_NODELAY,
        reinterpret_cast<const char*>(&noDelay),
        sizeof(noDelay));

    return RegisterSocket(socket);
}

inline BOOL WINAPI WaitNamedPipeWCompat(const LPCWSTR name, const DWORD timeout)
{
    unsigned short port = 0;
    if (IsDiscordPipePath(name) && TryGetBridgePort(port))
        return TRUE;

    return ::WaitNamedPipeW(name, timeout);
}

inline HANDLE WINAPI CreateFileWCompat(
    const LPCWSTR fileName,
    const DWORD desiredAccess,
    const DWORD shareMode,
    const LPSECURITY_ATTRIBUTES securityAttributes,
    const DWORD creationDisposition,
    const DWORD flagsAndAttributes,
    const HANDLE templateFile)
{
    unsigned short port = 0;
    if (IsDiscordPipePath(fileName) && TryGetBridgePort(port))
        return OpenBridgeSocket(port);

    return ::CreateFileW(
        fileName,
        desiredAccess,
        shareMode,
        securityAttributes,
        creationDisposition,
        flagsAndAttributes,
        templateFile);
}

inline BOOL WINAPI WriteFileCompat(
    const HANDLE file,
    const LPCVOID buffer,
    const DWORD bytesToWrite,
    const LPDWORD bytesWritten,
    const LPOVERLAPPED overlapped)
{
    SOCKET socket = INVALID_SOCKET;
    if (!TryGetSocket(file, socket))
        return ::WriteFile(file, buffer, bytesToWrite, bytesWritten, overlapped);

    if (bytesWritten != nullptr)
        *bytesWritten = 0;

    if (overlapped != nullptr || bytesToWrite > static_cast<DWORD>(INT_MAX))
    {
        SetLastError(ERROR_INVALID_PARAMETER);
        return FALSE;
    }

    const int sent = ::send(
        socket,
        static_cast<const char*>(buffer),
        static_cast<int>(bytesToWrite),
        0);

    if (sent == SOCKET_ERROR || sent == 0)
    {
        SetLastError(ERROR_BROKEN_PIPE);
        return FALSE;
    }

    if (bytesWritten != nullptr)
        *bytesWritten = static_cast<DWORD>(sent);

    return TRUE;
}

inline BOOL WINAPI ReadFileCompat(
    const HANDLE file,
    const LPVOID buffer,
    const DWORD bytesToRead,
    const LPDWORD bytesRead,
    const LPOVERLAPPED overlapped)
{
    SOCKET socket = INVALID_SOCKET;
    if (!TryGetSocket(file, socket))
        return ::ReadFile(file, buffer, bytesToRead, bytesRead, overlapped);

    if (bytesRead != nullptr)
        *bytesRead = 0;

    if (overlapped != nullptr || bytesToRead > static_cast<DWORD>(INT_MAX))
    {
        SetLastError(ERROR_INVALID_PARAMETER);
        return FALSE;
    }

    const int received = ::recv(
        socket,
        static_cast<char*>(buffer),
        static_cast<int>(bytesToRead),
        0);

    if (received == SOCKET_ERROR || received == 0)
    {
        SetLastError(ERROR_BROKEN_PIPE);
        return FALSE;
    }

    if (bytesRead != nullptr)
        *bytesRead = static_cast<DWORD>(received);

    return TRUE;
}

inline BOOL WINAPI PeekNamedPipeCompat(
    const HANDLE pipe,
    const LPVOID buffer,
    const DWORD bufferSize,
    const LPDWORD bytesRead,
    const LPDWORD totalBytesAvailable,
    const LPDWORD bytesLeftThisMessage)
{
    SOCKET socket = INVALID_SOCKET;
    if (!TryGetSocket(pipe, socket))
    {
        return ::PeekNamedPipe(
            pipe,
            buffer,
            bufferSize,
            bytesRead,
            totalBytesAvailable,
            bytesLeftThisMessage);
    }

    if (bytesRead != nullptr)
        *bytesRead = 0;

    u_long available = 0;
    if (ioctlsocket(socket, FIONREAD, &available) == SOCKET_ERROR)
    {
        SetLastError(ERROR_BROKEN_PIPE);
        return FALSE;
    }

    if (available == 0)
    {
        fd_set readable{};
        FD_ZERO(&readable);
        FD_SET(socket, &readable);
        timeval timeout{};

        const int ready = select(0, &readable, nullptr, nullptr, &timeout);
        if (ready == SOCKET_ERROR)
        {
            SetLastError(ERROR_BROKEN_PIPE);
            return FALSE;
        }

        if (ready > 0)
        {
            char probe = 0;
            const int peeked = recv(socket, &probe, 1, MSG_PEEK);
            if (peeked <= 0)
            {
                SetLastError(ERROR_BROKEN_PIPE);
                return FALSE;
            }
            available = static_cast<u_long>(peeked);
        }
    }

    if (buffer != nullptr && bufferSize > 0 && available > 0)
    {
        const DWORD requested = bufferSize < available ? bufferSize : static_cast<DWORD>(available);
        const int peeked = recv(
            socket,
            static_cast<char*>(buffer),
            static_cast<int>(requested),
            MSG_PEEK);

        if (peeked == SOCKET_ERROR)
        {
            SetLastError(ERROR_BROKEN_PIPE);
            return FALSE;
        }

        if (bytesRead != nullptr)
            *bytesRead = static_cast<DWORD>(peeked);
    }

    if (totalBytesAvailable != nullptr)
        *totalBytesAvailable = static_cast<DWORD>(available);
    if (bytesLeftThisMessage != nullptr)
        *bytesLeftThisMessage = static_cast<DWORD>(available);

    return TRUE;
}

inline BOOL WINAPI CloseHandleCompat(const HANDLE handle)
{
    SOCKET socket = INVALID_SOCKET;
    if (!TakeSocket(handle, socket))
        return ::CloseHandle(handle);

    shutdown(socket, SD_BOTH);
    closesocket(socket);
    return TRUE;
}
} // namespace azimuthrpc::winecompat

#define WaitNamedPipeW azimuthrpc::winecompat::WaitNamedPipeWCompat
#define CreateFileW azimuthrpc::winecompat::CreateFileWCompat
#define WriteFile azimuthrpc::winecompat::WriteFileCompat
#define ReadFile azimuthrpc::winecompat::ReadFileCompat
#define PeekNamedPipe azimuthrpc::winecompat::PeekNamedPipeCompat
#define CloseHandle azimuthrpc::winecompat::CloseHandleCompat
