using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Loudswitch.Interop;

/// <summary>
/// NativeAOT 호환 COM 부트스트랩. 고전 <c>[ComImport]</c> + <c>new</c> 코클래스 활성화는 AOT에서
/// 지원되지 않으므로, <c>CoCreateInstance</c>로 코클래스를 만들고 <see cref="StrategyBasedComWrappers"/>로
/// <c>[GeneratedComInterface]</c> 인터페이스에 래핑한다.
/// (소스 생성 COM은 JIT에서도 동일하게 동작하므로 점진 이식이 안전하다.)
/// </summary>
internal static partial class Com
{
    private const int CLSCTX_ALL = 0x17;
    private const int COINIT_APARTMENTTHREADED = 0x2;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, int coInit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, int dwClsContext, in Guid riid, out nint ppv);

    /// <summary>
    /// 호출 스레드에 COM을 초기화한다(idempotent). 반환값은 무시한다: 이미 STA/MTA로 초기화돼
    /// S_FALSE나 RPC_E_CHANGED_MODE가 와도 COM은 사용 가능하다. (기존 <c>new</c> 코클래스가 CLR
    /// 자동 초기화에 의존했던 동작을 백그라운드 스레드에서도 재현하기 위함.)
    /// </summary>
    public static void EnsureInitialized() => CoInitializeEx(0, COINIT_APARTMENTTHREADED);

    /// <summary>CLSID로 코클래스를 만들고 <typeparamref name="T"/> 인터페이스로 래핑한다.</summary>
    public static T Create<T>(in Guid clsid, in Guid iid) where T : class
    {
        EnsureInitialized();
        int hr = CoCreateInstance(in clsid, 0, CLSCTX_ALL, in iid, out nint p);
        if (hr != 0 || p == 0)
            throw new InvalidOperationException($"COM 개체 생성 실패 (CLSID {clsid:B}, HRESULT 0x{hr:X8}).");

        // GetOrCreateObjectForComInstance가 p의 참조 소유권을 가져가므로 별도 Release 금지.
        return (T)Wrappers.GetOrCreateObjectForComInstance(p, CreateObjectFlags.None);
    }
}
