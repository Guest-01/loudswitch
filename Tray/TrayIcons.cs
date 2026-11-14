using System.Reflection;
using System.Runtime.InteropServices;
using static Loudswitch.Tray.Win32;

namespace Loudswitch.Tray;

/// <summary>
/// 트레이 아이콘(HICON)을 임베드된 여러 크기 .ico(16~256px, <c>dev\Generate-TrayIcons.ps1</c>로 생성)에서 만든다.
/// 활성=파랑, 비활성=회색의 둥근 "L" 배지.
///
/// 크기는 창이 놓인 모니터 DPI의 작은 아이콘 크기(<c>GetSystemMetricsForDpi(SM_CXSMICON)</c>)를 쓰고,
/// .ico 안에서 그 크기 이상인 가장 작은 이미지를 골라 축소한다(작은 이미지를 늘리면 흐려지므로).
/// System.Drawing 없이 Win32 <c>CreateIconFromResourceEx</c>로 만들어 NativeAOT에서도 안전하다.
/// 반환된 HICON은 사용 측이 <see cref="Win32.DestroyIcon"/>로 해제한다.
/// </summary>
internal static class TrayIcons
{
    private const int SM_CXSMICON = 49;
    private const int SM_CXICON = 11;

    /// <summary>트레이(알림 영역)용 작은 아이콘.</summary>
    public static nint CreateSmall(bool active, uint dpi) => Load(active, GetSystemMetricsForDpi(SM_CXSMICON, dpi));

    /// <summary>알림(토스트) 본문용 큰 아이콘.</summary>
    public static nint CreateLarge(bool active, uint dpi) => Load(active, GetSystemMetricsForDpi(SM_CXICON, dpi));

    private static nint Load(bool active, int size)
    {
        byte[] ico = ReadEmbedded(active ? "active.ico" : "inactive.ico");

        // ICONDIR: +4 = 이미지 수. ICONDIRENTRY(16바이트, 오프셋 6부터): +0 폭(0=256), +8 크기, +12 오프셋.
        int count = BitConverter.ToUInt16(ico, 4);
        int best = -1, bestWidth = 0;
        for (int i = 0; i < count; i++)
        {
            int width = ico[6 + 16 * i] == 0 ? 256 : ico[6 + 16 * i];
            bool better = best < 0
                || (width >= size && (bestWidth < size || width < bestWidth))  // 목표 이상 중 가장 작은 것
                || (bestWidth < size && width > bestWidth);                    // 아직 못 찾았으면 큰 쪽
            if (better)
            {
                best = i;
                bestWidth = width;
            }
        }

        int bytesInRes = BitConverter.ToInt32(ico, 6 + 16 * best + 8);
        int imageOffset = BitConverter.ToInt32(ico, 6 + 16 * best + 12);

        GCHandle pin = GCHandle.Alloc(ico, GCHandleType.Pinned);
        try
        {
            nint imagePtr = pin.AddrOfPinnedObject() + imageOffset;
            // dwVer=0x00030000 (필수). cx/cy=목표 크기로 맞춰 만든다.
            return CreateIconFromResourceEx(imagePtr, (uint)bytesInRes, fIcon: true, 0x00030000, size, size, 0);
        }
        finally
        {
            pin.Free();
        }
    }

    private static byte[] ReadEmbedded(string logicalName)
    {
        using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName);
        if (s is null)
            throw new InvalidOperationException($"임베드 리소스를 찾지 못했습니다: {logicalName}");

        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
