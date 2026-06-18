using System.Reflection;
using System.Runtime.InteropServices;
using static Loudswitch.Tray.Win32;

namespace Loudswitch.Tray;

/// <summary>
/// 트레이 아이콘(HICON)을 임베드된 .ico에서 만든다. 활성=파랑, 비활성=회색의 둥근 "L" 배지.
/// System.Drawing 의존 없이 Win32 <c>CreateIconFromResourceEx</c>로 로드해 NativeAOT에서도 안전하다.
/// 반환된 HICON은 사용 측이 <see cref="Win32.DestroyIcon"/>로 해제한다.
/// </summary>
internal static class TrayIcons
{
    public static nint Create(bool active) => LoadIcon(active ? "active.ico" : "inactive.ico");

    private static nint LoadIcon(string resourceName)
    {
        byte[] ico = ReadEmbedded(resourceName);

        // 단일 이미지 .ico: 첫 ICONDIRENTRY(오프셋 6)에서 이미지 블록의 크기/오프셋을 읽는다.
        // ICONDIRENTRY: ... +8=bytesInRes(DWORD), +12=imageOffset(DWORD)
        int bytesInRes = BitConverter.ToInt32(ico, 6 + 8);
        int imageOffset = BitConverter.ToInt32(ico, 6 + 12);

        GCHandle pin = GCHandle.Alloc(ico, GCHandleType.Pinned);
        try
        {
            nint imagePtr = pin.AddrOfPinnedObject() + imageOffset;
            // dwVer=0x00030000 (필수). cx/cy=0 → 실제 크기 사용.
            return CreateIconFromResourceEx(imagePtr, (uint)bytesInRes, fIcon: true, 0x00030000, 0, 0, 0);
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
