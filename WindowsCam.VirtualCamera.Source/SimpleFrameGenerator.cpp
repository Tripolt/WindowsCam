//
// Copyright (C) Microsoft Corporation. All rights reserved.
//
#include "pch.h"

namespace
{
    constexpr ULONGLONG kFrameMagic = 0x5743414D4652414DULL; // WCAMFRAM
    constexpr DWORD kFrameHeaderBytes = 64;

    struct FrameHeader
    {
        ULONGLONG Magic;
        INT32 Version;
        INT32 Width;
        INT32 Height;
        INT32 Fps;
        INT32 LumaStride;
        INT32 PayloadBytes;
        INT64 Sequence;
        INT64 TimestampUnixMs;
        BYTE Reserved[16];
    };
}

HRESULT SimpleFrameGenerator::Initialize(_In_ IMFMediaType* pMediaType)
{
    RETURN_HR_IF_NULL(E_INVALIDARG, pMediaType);

    RETURN_IF_FAILED(pMediaType->GetGUID(MF_MT_SUBTYPE, &m_subType));
    if (m_subType != MFVideoFormat_RGB32 && m_subType != MFVideoFormat_NV12)
    {
        RETURN_HR_MSG(MF_E_UNSUPPORTED_FORMAT, "Unsupported format: %s", winrt::to_hstring(m_subType).data());
    }
    MFGetAttributeSize(pMediaType, MF_MT_FRAME_SIZE, &m_width, &m_height);

    return S_OK;
}

/*:
   Writes to a buffer representing a 2D image.
   Writes a different constant to each line based on row number and current time.
   Assumes top down image, no negative stride and pBuf points to the begnning of the buffer of length len.
   Param:
   pBuf - pointer to beginning of buffer
   pitch - line length in bytes
   len - length of buffer in bytes
*/
HRESULT SimpleFrameGenerator::CreateFrame(
    _Inout_updates_bytes_(len) BYTE* pBuf,
    _In_ DWORD len,
    _In_ LONG pitch,
    _In_ ULONG rgbMask)
{
    if (m_subType == MFVideoFormat_RGB32)
    {
        DEBUG_MSG(L"RGB32 frames %s\n", winrt::to_hstring(MFVideoFormat_RGB32).data());

        RETURN_IF_FAILED(_CreateRGB32Frame(pBuf, len, pitch, m_width, m_height, rgbMask));
    }
    else if(m_subType == MFVideoFormat_NV12)
    {
        DEBUG_MSG(L"NV12 frames %s \n", winrt::to_hstring(MFVideoFormat_NV12).data());

        auto hr = _CreateNV12FrameFromBroker(pBuf, len, pitch);
        if (FAILED(hr))
        {
            if (m_lastFrame.empty())
            {
                RETURN_IF_FAILED(_CreateNeutralNV12Frame(pBuf, len, pitch));
            }
            else
            {
                const BYTE* srcY = m_lastFrame.data();
                BYTE* dstY = pBuf;
                for (UINT32 row = 0; row < m_height; row++)
                {
                    CopyMemory(dstY + row * pitch, srcY + row * m_width, m_width);
                }

                const BYTE* srcUv = m_lastFrame.data() + m_width * m_height;
                BYTE* dstUv = pBuf + pitch * m_height;
                for (UINT32 row = 0; row < m_height / 2; row++)
                {
                    CopyMemory(dstUv + row * pitch, srcUv + row * m_width, m_width);
                }
            }
        }
    }
    else
    {
        return MF_E_UNSUPPORTED_FORMAT;
    }

    return S_OK;
}

//////////////////////////////////////////////////
// private

HRESULT SimpleFrameGenerator::_OpenBrokerMapping()
{
    if (m_brokerView)
    {
        return S_OK;
    }

    wchar_t programData[MAX_PATH]{};
    RETURN_HR_IF(E_FAIL, GetEnvironmentVariableW(L"ProgramData", programData, ARRAYSIZE(programData)) == 0);

    wchar_t framePath[MAX_PATH]{};
    RETURN_HR_IF(E_FAIL, FAILED(StringCchPrintfW(framePath, ARRAYSIZE(framePath), L"%s\\WindowsCam\\latest-frame.mmf", programData)));

    const DWORD payloadBytes = m_width * m_height * 3 / 2;
    m_mappedBytes = kFrameHeaderBytes + payloadBytes;

    m_brokerFile.reset(CreateFileW(
        framePath,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr));
    RETURN_LAST_ERROR_IF(!m_brokerFile);

    m_brokerMapping.reset(CreateFileMappingW(
        m_brokerFile.get(),
        nullptr,
        PAGE_READONLY,
        0,
        m_mappedBytes,
        nullptr));
    RETURN_LAST_ERROR_IF(!m_brokerMapping);

    m_brokerView.reset(static_cast<BYTE*>(MapViewOfFile(
        m_brokerMapping.get(),
        FILE_MAP_READ,
        0,
        0,
        m_mappedBytes)));
    RETURN_LAST_ERROR_IF(!m_brokerView);

    return S_OK;
}

HRESULT SimpleFrameGenerator::_CreateNV12FrameFromBroker(
    _Inout_updates_bytes_(len) BYTE* pBuf,
    _In_ DWORD len,
    _In_ LONG pitch)
{
    RETURN_HR_IF_NULL(E_INVALIDARG, pBuf);
    RETURN_HR_IF(E_INVALIDARG, pitch <= 0);

    RETURN_IF_FAILED(_OpenBrokerMapping());

    FrameHeader header{};
    CopyMemory(&header, m_brokerView.get(), sizeof(header));
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA), header.Magic != kFrameMagic);
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA), header.Version != 1);
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA), header.Width != static_cast<INT32>(m_width));
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA), header.Height != static_cast<INT32>(m_height));
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA), header.LumaStride != static_cast<INT32>(m_width));
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_RETRY), header.Sequence <= 0);

    const DWORD payloadBytes = m_width * m_height * 3 / 2;
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA), header.PayloadBytes != static_cast<INT32>(payloadBytes));
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA), m_mappedBytes < kFrameHeaderBytes + payloadBytes);
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER), len < static_cast<DWORD>(pitch) * m_height + static_cast<DWORD>(pitch) * m_height / 2);

    const BYTE* frameStart = m_brokerView.get() + kFrameHeaderBytes;
    if (m_lastFrame.size() != payloadBytes)
    {
        m_lastFrame.resize(payloadBytes);
    }
    CopyMemory(m_lastFrame.data(), frameStart, payloadBytes);

    const BYTE* srcY = m_lastFrame.data();
    BYTE* dstY = pBuf;
    for (UINT32 row = 0; row < m_height; row++)
    {
        CopyMemory(dstY + row * pitch, srcY + row * m_width, m_width);
    }

    const BYTE* srcUv = srcY + m_width * m_height;
    BYTE* dstUv = pBuf + pitch * m_height;
    for (UINT32 row = 0; row < m_height / 2; row++)
    {
        CopyMemory(dstUv + row * pitch, srcUv + row * m_width, m_width);
    }

    return S_OK;
}

HRESULT SimpleFrameGenerator::_CreateNeutralNV12Frame(
    _Inout_updates_bytes_(len) BYTE* pBuf,
    _In_ DWORD len,
    _In_ LONG pitch)
{
    RETURN_HR_IF_NULL(E_INVALIDARG, pBuf);
    RETURN_HR_IF(E_INVALIDARG, pitch <= 0);
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER), len < static_cast<DWORD>(pitch) * m_height + static_cast<DWORD>(pitch) * m_height / 2);

    for (UINT32 row = 0; row < m_height; row++)
    {
        FillMemory(pBuf + row * pitch, m_width, 16);
    }

    BYTE* uv = pBuf + pitch * m_height;
    for (UINT32 row = 0; row < m_height / 2; row++)
    {
        FillMemory(uv + row * pitch, m_width, 128);
    }

    return S_OK;
}

HRESULT SimpleFrameGenerator::_CreateRGB32Frame(
    _Inout_updates_bytes_(len) BYTE* pBuf,
    _In_ DWORD len,
    _In_ LONG pitch,
    _In_ DWORD width,
    _In_ DWORD height,
    _In_ ULONG rgbMask )
{
    RETURN_HR_IF_NULL(E_INVALIDARG, pBuf);

    LONGLONG curSysTimeInS = MFGetSystemTime() / (MFTIME)10000000;
    int offset = curSysTimeInS % height;

    for (unsigned int r = 0; r < height; r++)
    {
        uint32_t* p = (uint32_t*)(pBuf + (r * pitch));
        for (unsigned int c = 0; c < width; c++)
        {
            BYTE gray = (BYTE)r + offset;
            *p = ((uint32_t)gray << 16 | (uint32_t)gray << 8 | (uint32_t)gray) & rgbMask;
            p++;
        }
    }

    return S_OK;
}

//////////////////////////////////////////////////
// pixelFormatConverter

void SimpleFrameGenerator::RGB24ToYUY2(int R, int G, int B, BYTE* pY, BYTE* pU, BYTE* pV)
{
    *pY = ((66 * R + 129 * G + 25 * B + 128) >> 8) + 16;
    *pU = ((-38 * R - 74 * G + 112 * B + 128) >> 8) + 128;
    *pV = ((112 * R - 94 * G - 18 * B + 128) >> 8) + 128;
}

void SimpleFrameGenerator::RGB24ToY(int R, int G, int B, BYTE* pY)
{
    *pY = ((66 * R + 129 * G + 25 * B + 128) >> 8) + 16;
}

void SimpleFrameGenerator::RGB32ToNV12(BYTE RGB1[8], BYTE RGB2[8], BYTE* pY1, BYTE* pY2, BYTE* pUV)
{
    RGB24ToYUY2(RGB1[2], RGB1[1], RGB1[0], pY1, pUV, pUV + 1);
    RGB24ToY(RGB1[6], RGB1[5], RGB1[4], pY1 + 1);
    RGB24ToYUY2(RGB2[2], RGB2[1], RGB2[0], pY2, pUV, pUV + 1);
    RGB24ToY(RGB2[6], RGB2[5], RGB2[4], pY2 + 1);
};

//////////////////////////////////////////////////
// FrameFormatConverter

HRESULT SimpleFrameGenerator::RGB32ToNV12Frame(_Inout_updates_bytes_(len) BYTE* pbBuff, ULONG cbBuff, long stride, UINT width, UINT height, BYTE* pbBuffOut, ULONG cbBuffOut, long strideOut)
{
    do
    {
        RETURN_HR_IF(E_UNEXPECTED, width * 4 * height > cbBuff);
        RETURN_HR_IF(E_UNEXPECTED, width * 1.5 * height > cbBuffOut);
        RETURN_HR_IF_NULL(E_INVALIDARG, pbBuff);

        RETURN_HR_IF_NULL(E_INVALIDARG, pbBuffOut);
        for (DWORD h = 0; h < height - 1; h += 2)
        {
            BYTE* pRGB1 = h * stride + pbBuff;
            BYTE* pRGB2 = (h + 1) * stride + pbBuff;
            BYTE* pY1 = h * strideOut + pbBuffOut;
            BYTE* pY2 = (h + 1) * strideOut + pbBuffOut;
            BYTE* pUV = (h / 2 + height) * strideOut + pbBuffOut;

            for (DWORD w = 0; w < width; w += 2)
            {
                RGB32ToNV12(pRGB1, pRGB2, pY1, pY2, pUV);
                pRGB1 += 8;
                pRGB2 += 8;
                pY1 += 2;
                pY2 += 2;
                pUV += 2;
            }
        }
    } while (FALSE);

    return S_OK;
}
