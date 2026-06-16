<#
.SYNOPSIS
  [개발 환경 전용] Loudness EQ를 노출하지 않는 출력 장치에 Microsoft 기본 APO + Loudness EQ
  키를 강제 주입(force-create)해 슬라이스 1/2의 테스트 대상을 만든다. 복구(-Revert) 포함.

.DESCRIPTION
  ⚠ 이 스크립트는 우리 애플리케이션의 일부가 아니다.
     앱은 초기 계획대로 "이미 노출된 Loudness EQ만 토글"한다. 아무것도 강제 생성하지 않는다.
     이 파일은 오직 이 PC에서 개발/검증할 테스트베드를 구성하기 위한 일회성 셋업이다.

  주입하는 값은 Falcosc/enable-loudness-equalisation에서 검증된 것을 그대로 사용하되,
    - 우리 C# 도구가 읽는 "표준" 위치(...\Render\{GUID}\FxProperties)에 쓰고
    - 장치를 GUID로 정확히 타깃하며(이름 부분일치 모호성 제거)
    - Falcosc에 없는 '복구'(-Revert)를 제공한다.

  [권한 메모] MMDevices 오디오 키는 SYSTEM 소유이고 Administrators에겐 'SetValue, ReadKey'만
  부여돼 있다(CreateSubKey 없음). 그래서 PowerShell의 New-ItemProperty(키를 KEY_WRITE=
  SetValue+CreateSubKey로 엶)는 거부당한다. 대신 .NET RegistryKey를 정확히 'SetValue, ReadKey'
  로만 열어 값을 쓴다 → 소유권/ACL 변경 불필요. (FxProperties가 아예 없으면 CreateSubKey가
  없어 만들 수 없으므로, 그 경우엔 다른 접근이 필요하다고 안내하고 중단한다.)

  동작 원리: Loudness EQ는 드라이버가 아니라 Windows가 내장한 MS 제공 APO다.
  FxProperties에 MS sysfx APO 체인(stream/mode/endpoint effect CLSID)을 가리키는 키를
  주입하면 Windows가 그 APO를 로드하고, Loudness 켜기 비트가 의미를 갖게 된다.
  (HDMI/SPDIF passthrough, 일부 Bluetooth/독점 모드 엔드포인트에서는 무시되거나 오디오가
   깨질 수 있다 — 그럴 땐 -Revert.)

.PARAMETER DeviceGuid
  대상 엔드포인트 GUID. 예: {55c803e1-1971-4764-b6dd-b3354567bc7c}
  `dotnet run` 출력의 "엔드포인트:" 줄 끝 {GUID}에서 확인.
  (아날로그 스피커/헤드폰 엔드포인트를 권장. HDMI/Bluetooth는 실패 가능.)

.PARAMETER Revert
  주입한 5개 값을 제거해 원상복구한다. (FxProperties 키 자체는 보존 — 원래 존재했고 Delete
  권한도 없음.)

.EXAMPLE
  # 1) 대상 GUID/이름 확인
  dotnet run
  # 2) 주입 (관리자 PowerShell에서)
  .\dev\Setup-LoudnessTestbed.ps1 -DeviceGuid '{55c803e1-1971-4764-b6dd-b3354567bc7c}'
  # 3) 확인: dotnet run -> [켜짐], mmsys.cpl -> Loudness EQ 체크박스 등장
  # 4) 복구
  .\dev\Setup-LoudnessTestbed.ps1 -DeviceGuid '{55c803e1-1971-4764-b6dd-b3354567bc7c}' -Revert
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DeviceGuid,

    [switch]$Revert
)

$ErrorActionPreference = 'Stop'

# --- 관리자 권한 확인 (audiosrv 재시작 + SetValue를 가진 관리자 토큰에 필요) ---
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole(
    [Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "관리자 권한으로 실행하세요. (HKLM 쓰기 + audiosrv 재시작 필요)"
    exit 1
}

# --- GUID 정규화: 중괄호 보정 ---
$guid = $DeviceGuid.Trim()
if ($guid -notmatch '^\{.*\}$') {
    $guid = '{' + $guid.Trim('{', '}') + '}'
}

$relBase = "SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\$guid"
$relFx = "$relBase\FxProperties"
$deviceKeyPs = "HKLM:\$relBase"
$fxKeyPs = "HKLM:\$relFx"

if (-not (Test-Path $deviceKeyPs)) {
    Write-Error "장치 키를 찾을 수 없습니다: $deviceKeyPs`n  GUID가 맞는지 ``dotnet run`` 출력으로 확인하세요."
    exit 1
}

# 주입/복구 대상 값 이름 (검증된 Falcosc 값과 동일)
$valueNames = @(
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1'   # MS stream effect CLSID
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2'   # MS mode effect CLSID
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},3'   # MS endpoint effect CLSID
    '{fc52a749-4be9-4510-896e-966ba6525980},3'   # Loudness EQ ON (VT_BOOL=TRUE)
    '{9c00eeed-edce-4cd8-ae08-cb05e8ef57a0},3'   # Loudness release-time 파라미터
)

function Open-FxForWrite {
    # FxProperties를 정확히 'SetValue, ReadKey' 권한으로만 연다.
    # (writable:$true 오버로드는 CreateSubKey까지 요구해 Administrators가 거부당한다.)
    $hklm = [Microsoft.Win32.Registry]::LocalMachine
    $rights = [System.Security.AccessControl.RegistryRights]'SetValue, ReadKey'
    $key = $hklm.OpenSubKey($relFx,
        [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, $rights)
    if ($null -eq $key) {
        throw "FxProperties 키를 열 수 없습니다(없거나 권한 부족): $fxKeyPs"
    }
    return $key
}

function Restart-AudioService {
    Write-Host "  audiosrv 재시작 중... (잠깐 오디오가 끊깁니다)"
    Restart-Service audiosrv -Force
    Write-Host "  완료."
}

if ($Revert) {
    Write-Host "복구: $fxKeyPs 에서 주입 값 제거"
    $key = Open-FxForWrite
    try {
        foreach ($name in $valueNames) {
            $key.DeleteValue($name, $false)   # throwOnMissingValue=$false → 없으면 무시
        }
    }
    finally {
        $key.Close()
    }
    Write-Host "  주입 값 제거 완료(FxProperties 키는 보존)."
    Restart-AudioService
    Write-Host "복구 완료. ``dotnet run`` 으로 [미지원]으로 돌아갔는지 확인하세요."
    return
}

# --- 주입 ---
Write-Host "주입 대상: $fxKeyPs"
$key = Open-FxForWrite
try {
    # MS 기본 APO 체인 (REG_SZ, GUID 문자열)
    $key.SetValue($valueNames[0], '{62dc1a93-ae24-464c-a43e-452f824c4250}',
        [Microsoft.Win32.RegistryValueKind]::String)
    $key.SetValue($valueNames[1], '{637c490d-eee3-4c0a-973f-371958802da2}',
        [Microsoft.Win32.RegistryValueKind]::String)
    $key.SetValue($valueNames[2], '{5860E1C5-F95C-4a7a-8EC8-8AEF24F379A1}',
        [Microsoft.Win32.RegistryValueKind]::String)

    # Loudness EQ ON — 직렬화 PROPVARIANT VT_BOOL=VARIANT_TRUE (REG_BINARY)
    $key.SetValue($valueNames[3],
        [byte[]](0x0b, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xff, 0xff, 0x00, 0x00),
        [Microsoft.Win32.RegistryValueKind]::Binary)

    # Loudness release-time 파라미터 (REG_BINARY, 기본값 4)
    $key.SetValue($valueNames[4],
        [byte[]](0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00),
        [Microsoft.Win32.RegistryValueKind]::Binary)
}
finally {
    $key.Close()
}

Restart-AudioService

Write-Host ""
Write-Host "주입 완료. 검증:"
Write-Host "  1) dotnet run        -> 해당 장치가 [켜짐] + raw hex 표시되어야 함"
Write-Host "  2) mmsys.cpl         -> 장치 속성에 'Loudness Equalization' 체크박스 등장 + 소리 변화"
Write-Host "  둘 다 확인되면 테스트베드 OK. 안 되면 -Revert 후 다른 장치로 재시도."
