#include <Windows.h>
#include <mfapi.h>
#include <mfvirtualcamera.h>
#include <shellapi.h>
#include <ks.h>
#include <ksmedia.h>
#include <wrl/client.h>

#include <iostream>
#include <string_view>

using Microsoft::WRL::ComPtr;

namespace
{
    constexpr wchar_t kCameraName[] = L"WindowsCam";
    constexpr wchar_t kSourceClsid[] = L"{D9E25520-0B1B-4CE2-9C8E-6F9B4698B1D5}";
    constexpr wchar_t kSourceDllName[] = L"WindowsCam.VirtualCamera.Source.dll";

    void ThrowIfFailed(HRESULT hr, std::wstring_view action)
    {
        if (FAILED(hr))
        {
            std::wcerr << action << L" failed: 0x" << std::hex << static_cast<unsigned long>(hr) << std::endl;
            ExitProcess(static_cast<UINT>(hr));
        }
    }

    bool IsProcessElevated()
    {
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
        {
            return false;
        }

        TOKEN_ELEVATION elevation{};
        DWORD bytesReturned = 0;
        const auto ok = GetTokenInformation(
            token,
            TokenElevation,
            &elevation,
            sizeof(elevation),
            &bytesReturned);
        CloseHandle(token);

        return ok && elevation.TokenIsElevated != 0;
    }

    int RelaunchElevated(std::wstring_view command)
    {
        wchar_t executablePath[MAX_PATH]{};
        if (GetModuleFileNameW(nullptr, executablePath, ARRAYSIZE(executablePath)) == 0)
        {
            std::wcerr << L"GetModuleFileNameW failed: " << GetLastError() << std::endl;
            return static_cast<int>(GetLastError());
        }

        std::wstring parameters(command);
        SHELLEXECUTEINFOW shellExecute{};
        shellExecute.cbSize = sizeof(shellExecute);
        shellExecute.fMask = SEE_MASK_NOCLOSEPROCESS;
        shellExecute.lpVerb = L"runas";
        shellExecute.lpFile = executablePath;
        shellExecute.lpParameters = parameters.c_str();
        shellExecute.nShow = SW_HIDE;

        std::wcout << L"Administrator approval is required to " << command << L" the system virtual camera." << std::endl;
        if (!ShellExecuteExW(&shellExecute))
        {
            const auto error = GetLastError();
            if (error == ERROR_CANCELLED)
            {
                std::wcerr << L"Administrator approval was cancelled." << std::endl;
            }
            else
            {
                std::wcerr << L"ShellExecuteExW(runas) failed: " << error << std::endl;
            }

            return static_cast<int>(error);
        }

        WaitForSingleObject(shellExecute.hProcess, INFINITE);

        DWORD exitCode = 1;
        if (!GetExitCodeProcess(shellExecute.hProcess, &exitCode))
        {
            exitCode = GetLastError();
        }

        CloseHandle(shellExecute.hProcess);
        return static_cast<int>(exitCode);
    }

    std::wstring GetSourceDllPath()
    {
        wchar_t executablePath[MAX_PATH]{};
        ThrowIfFailed(GetModuleFileNameW(nullptr, executablePath, ARRAYSIZE(executablePath)) == 0
            ? HRESULT_FROM_WIN32(GetLastError())
            : S_OK,
            L"GetModuleFileNameW");

        std::wstring path(executablePath);
        const auto slash = path.find_last_of(L"\\/");
        if (slash == std::wstring::npos)
        {
            return kSourceDllName;
        }

        path.resize(slash + 1);
        path += kSourceDllName;
        return path;
    }

    void SetRegistryString(HKEY key, const wchar_t* name, const wchar_t* value, DWORD type = REG_SZ)
    {
        const auto bytes = static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t));
        ThrowIfFailed(HRESULT_FROM_WIN32(RegSetValueExW(
            key,
            name,
            0,
            type,
            reinterpret_cast<const BYTE*>(value),
            bytes)),
            L"RegSetValueExW");
    }

    void RegisterSourceComServer()
    {
        const auto sourceDllPath = GetSourceDllPath();
        if (GetFileAttributesW(sourceDllPath.c_str()) == INVALID_FILE_ATTRIBUTES)
        {
            std::wcerr << L"Required source DLL not found: " << sourceDllPath << std::endl;
            ExitProcess(HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND));
        }

        const std::wstring clsidKey = std::wstring(L"Software\\Classes\\CLSID\\") + kSourceClsid;
        HKEY key = nullptr;
        ThrowIfFailed(HRESULT_FROM_WIN32(RegCreateKeyExW(
            HKEY_LOCAL_MACHINE,
            clsidKey.c_str(),
            0,
            nullptr,
            REG_OPTION_NON_VOLATILE,
            KEY_WRITE,
            nullptr,
            &key,
            nullptr)),
            L"RegCreateKeyExW(CLSID)");
        SetRegistryString(key, nullptr, L"WindowsCam Virtual Camera Source");
        RegCloseKey(key);

        const std::wstring inprocKey = clsidKey + L"\\InprocServer32";
        ThrowIfFailed(HRESULT_FROM_WIN32(RegCreateKeyExW(
            HKEY_LOCAL_MACHINE,
            inprocKey.c_str(),
            0,
            nullptr,
            REG_OPTION_NON_VOLATILE,
            KEY_WRITE,
            nullptr,
            &key,
            nullptr)),
            L"RegCreateKeyExW(InprocServer32)");
        SetRegistryString(key, nullptr, sourceDllPath.c_str(), REG_EXPAND_SZ);
        SetRegistryString(key, L"ThreadingModel", L"Both");
        RegCloseKey(key);
    }

    void UnregisterSourceComServer()
    {
        const std::wstring clsidKey = std::wstring(L"Software\\Classes\\CLSID\\") + kSourceClsid;
        RegDeleteTreeW(HKEY_LOCAL_MACHINE, clsidKey.c_str());
    }

    ComPtr<IMFVirtualCamera> OpenVirtualCamera()
    {
        GUID categories[] =
        {
            KSCATEGORY_VIDEO_CAMERA,
            KSCATEGORY_VIDEO,
            KSCATEGORY_CAPTURE
        };

        ComPtr<IMFVirtualCamera> camera;
        ThrowIfFailed(MFCreateVirtualCamera(
            MFVirtualCameraType_SoftwareCameraSource,
            MFVirtualCameraLifetime_System,
            MFVirtualCameraAccess_AllUsers,
            kCameraName,
            kSourceClsid,
            categories,
            ARRAYSIZE(categories),
            &camera),
            L"MFCreateVirtualCamera");

        return camera;
    }
}

int wmain(int argc, wchar_t* argv[])
{
    if (argc != 2)
    {
        std::wcerr << L"Usage: WindowsCam.VirtualCamera.Tool.exe register|stop|remove" << std::endl;
        return 2;
    }

    const std::wstring_view command = argv[1];
    if ((command == L"register" || command == L"stop" || command == L"remove") && !IsProcessElevated())
    {
        return RelaunchElevated(command);
    }

    ThrowIfFailed(CoInitializeEx(nullptr, COINIT_MULTITHREADED), L"CoInitializeEx");
    ThrowIfFailed(MFStartup(MF_VERSION, MFSTARTUP_FULL), L"MFStartup");

    auto camera = OpenVirtualCamera();

    if (command == L"register")
    {
        RegisterSourceComServer();
        ThrowIfFailed(camera->Start(nullptr), L"IMFVirtualCamera::Start");
        std::wcout << L"Registered WindowsCam as a Windows 11 virtual camera." << std::endl;
    }
    else if (command == L"stop")
    {
        ThrowIfFailed(camera->Stop(), L"IMFVirtualCamera::Stop");
        std::wcout << L"Stopped WindowsCam enumeration." << std::endl;
    }
    else if (command == L"remove")
    {
        ThrowIfFailed(camera->Remove(), L"IMFVirtualCamera::Remove");
        UnregisterSourceComServer();
        std::wcout << L"Removed WindowsCam virtual camera registration." << std::endl;
    }
    else
    {
        std::wcerr << L"Unknown command: " << command << std::endl;
        return 2;
    }

    camera.Reset();
    MFShutdown();
    CoUninitialize();
    return 0;
}
