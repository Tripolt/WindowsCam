#include <Windows.h>
#include <mfapi.h>
#include <mfvirtualcamera.h>
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

    void ThrowIfFailed(HRESULT hr, std::wstring_view action)
    {
        if (FAILED(hr))
        {
            std::wcerr << action << L" failed: 0x" << std::hex << static_cast<unsigned long>(hr) << std::endl;
            ExitProcess(static_cast<UINT>(hr));
        }
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

    ThrowIfFailed(CoInitializeEx(nullptr, COINIT_MULTITHREADED), L"CoInitializeEx");
    ThrowIfFailed(MFStartup(MF_VERSION, MFSTARTUP_FULL), L"MFStartup");

    auto camera = OpenVirtualCamera();
    const std::wstring_view command = argv[1];

    if (command == L"register")
    {
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
