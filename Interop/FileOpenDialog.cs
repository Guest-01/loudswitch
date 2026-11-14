using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Loudswitch.Interop;

/// <summary>
/// Windows 표준 파일 열기 대화상자(Common Item Dialog, <c>IFileOpenDialog</c>).
/// Microsoft 문서상 Vista부터 옛 <c>GetOpenFileName</c>을 대체하는 권장 API다.
/// 메서드 순서는 Windows SDK 10.0.26100 <c>shobjidl_core.h</c>를 따른다.
/// </summary>
internal static unsafe class FileOpenDialog
{
    private static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid IID_IFileOpenDialog = new("d57c7288-d4ad-4768-be02-9d969532d960");

    private const uint FOS_FORCEFILESYSTEM = 0x40;
    private const uint FOS_PATHMUSTEXIST = 0x800;
    private const uint FOS_FILEMUSTEXIST = 0x1000;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int HRESULT_CANCELLED = unchecked((int)0x800704C7); // ERROR_CANCELLED

    /// <summary>파일 하나를 고르게 하고 전체 경로를 돌려준다. 취소·실패 시 null.</summary>
    public static string? PickFile(nint owner, string title, string filterName, string filterSpec)
    {
        IFileOpenDialog dialog = Com.Create<IFileOpenDialog>(in CLSID_FileOpenDialog, in IID_IFileOpenDialog);

        nint name = Marshal.StringToCoTaskMemUni(filterName);
        nint spec = Marshal.StringToCoTaskMemUni(filterSpec);
        try
        {
            nint* filter = stackalloc nint[2] { name, spec }; // COMDLG_FILTERSPEC { pszName, pszSpec }
            dialog.SetFileTypes(1, (nint)filter);
            dialog.SetTitle(title);
            if (dialog.GetOptions(out uint options) == 0)
                dialog.SetOptions(options | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_FILEMUSTEXIST);

            int hr = dialog.Show(owner);
            if (hr == HRESULT_CANCELLED || hr != 0)
                return null;

            if (dialog.GetResult(out IShellItem? item) != 0 || item is null)
                return null;
            if (item.GetDisplayName(SIGDN_FILESYSPATH, out nint path) != 0 || path == 0)
                return null;
            try
            {
                return Marshal.PtrToStringUni(path);
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(spec);
        }
    }
}

// IModalWindow → IFileDialog → IFileOpenDialog 를 한 vtable로 펼쳐 선언(사용하지 않는 슬롯은 자리만 차지).
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
internal partial interface IFileOpenDialog
{
    [PreserveSig] int Show(nint owner);                         // IModalWindow
    [PreserveSig] int SetFileTypes(uint count, nint filterSpec); // IFileDialog
    [PreserveSig] int SetFileTypeIndex(uint index);
    [PreserveSig] int GetFileTypeIndex(out uint index);
    [PreserveSig] int Advise(nint events, out uint cookie);
    [PreserveSig] int Unadvise(uint cookie);
    [PreserveSig] int SetOptions(uint options);
    [PreserveSig] int GetOptions(out uint options);
    [PreserveSig] int SetDefaultFolder(nint item);
    [PreserveSig] int SetFolder(nint item);
    [PreserveSig] int GetFolder(out nint item);
    [PreserveSig] int GetCurrentSelection(out nint item);
    [PreserveSig] int SetFileName(string name);
    [PreserveSig] int GetFileName(out nint name);
    [PreserveSig] int SetTitle(string title);
    [PreserveSig] int SetOkButtonLabel(string text);
    [PreserveSig] int SetFileNameLabel(string label);
    [PreserveSig] int GetResult(out IShellItem? item);
    // AddPlace 이후 슬롯과 IFileOpenDialog 고유 메서드는 호출하지 않으므로 생략
}

[GeneratedComInterface]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    [PreserveSig] int BindToHandler(nint bindCtx, in Guid bhid, in Guid riid, out nint ppv);
    [PreserveSig] int GetParent(out nint parent);
    [PreserveSig] int GetDisplayName(uint sigdnName, out nint name);
}
