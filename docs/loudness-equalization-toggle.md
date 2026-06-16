---
title: Windows Loudness Equalization 프로그램 토글 — 사전 조사
status: 구현 진행 (슬라이스 0–2 완료)
created: 2026-06-15
updated: 2026-06-16
---

# Windows Loudness Equalization 프로그램 토글 — 사전 조사

## 목표

특정 프로그램이 실행되면 Windows 출력 사운드 장치의 **Loudness Equalization(음량 평준화)**
향상 기능을 자동으로 켜고, 그 프로그램이 종료되면 끄는 상주형 유틸리티를 만든다.

1차 범위:
- **이미 노출된** Loudness EQ 기능을 활성화/비활성화하는 것만 다룬다.
- 드라이버가 기능을 노출하지 않는 경우 **억지로 생성하지 않는다** (미지원 장치는 skip).
- 형태: **개별 Loudness EQ만 토글하는 트레이 앱**.

## 결론 (TL;DR)

- ✅ **가능하다.** 상태는 레지스트리(Core Audio FxProperties 저장소)에 있고, 코드로 읽고 쓸 수 있다.
- ✅ **서비스 재시작은 불필요하다.** `IPolicyConfig::SetPropertyValue`(GUI가 쓰는 비공개 경로)로
  쓰면 속성 변경 알림이 발생해 APO가 즉시 재로드된다. `audiosrv` 재시작은 *레지스트리 직접 쓰기*
  방식의 임시방편일 뿐이며, 부작용(오디오 끊김·앱 충돌·`AUDCLNT_E_DEVICE_INVALIDATED`)이 있어 피한다.
- ⚠️ **드라이버 의존적이다.** Realtek HD Audio 등 해당 APO를 제공하는 엔드포인트에서만 동작한다.
- 권장 스택: **C# / .NET 10 (LTS) 트레이 앱 + 이벤트 기반(WMI) 프로세스 감지 + IPolicyConfig**.

---

## 1. 설정 저장 위치

상태는 레지스트리에 저장되며 Core Audio의 속성 저장소(FxProperties)가 이를 읽는다.

```
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{장치GUID}\FxProperties
```

- `Render` = 출력, `Capture` = 입력.
- `{장치GUID}`는 엔드포인트별 고유 값(머신/장치마다 다름) → **하드코딩 금지**.
  `IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender, ...)`로 동적 식별한다.
- 구조는 Vista ~ Win11까지 안정적.

### Property Key

| 용도 | Property Key | 타입 | 값 |
|---|---|---|---|
| **개별 Loudness EQ** | `{fc52a749-4be9-4510-896e-966ba6525980},3`<br>(`MFPKEY_CORR_LOUDNESS_EQUALIZATION_ON`) | REG_BINARY (직렬화 PROPVARIANT) | ON = `hex:0b,00,00,00,01,00,00,00,ff,ff,00,00` |
| 향상 전체 마스터 스위치 | `{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5`<br>(`PKEY_AudioEndpoint_Disable_SysFx`) | DWORD (VT_UI4) | 0 = 활성, 1 = 전체 비활성 |

> 마스터 스위치는 사운드 설정의 "오디오 향상 사용" 체크박스에 매핑된다. **개별 Loudness EQ만**
> 토글하려면 첫 번째 키를 써야 한다.

### 값 형식 — 실측 완료 (2026-06-16)

직렬화 PROPVARIANT(VT_BOOL)이며, 슬라이스 2에서 IPolicyConfig로 켜고/끄며 read-back해 **두 값 모두 확정**:

- ON  = `hex:0b,00,00,00,01,00,00,00,ff,ff,00,00` (VARIANT_TRUE)
- OFF = `hex:0b,00,00,00,01,00,00,00,00,00,00,00` (VARIANT_FALSE)

마지막 워드만 `0xFFFF ↔ 0x0000`으로 바뀐다. (당초 OFF 형식 미확정 → 추측 없이 read→flip→write로 해소.)

---

## 2. 적용(reload) 메커니즘 — 서비스 재시작 불필요

GUI에서 "적용"을 누르면 어떤 서비스도 재시작하지 않고 즉시 반영된다. 메커니즘 차이:

- **레지스트리 직접 쓰기**: 실행 중인 오디오 엔진에 알림이 가지 않음 → PowerShell 스크립트들이
  `Restart-Service audiosrv`로 우회. (dechamps/APO: "레지스트리 항목은 audiosrv가 소비하므로
  적용 보장을 위해 보통 서비스 재시작 필요".)
- **속성 저장소 API로 쓰기**: 변경 시 `AUDIO_ENDPOINT_PROPERTY_CHANGE_NOTIFICATION` 발생 →
  `audiodg.exe`의 APO가 `IAudioProcessingObjectNotifications::HandleNotification`을 받아 **즉시 재로드**.
  이것이 GUI가 쓰는 방식.

| | 레지스트리 직접 쓰기 | IPolicyConfig API |
|---|---|---|
| 즉시 적용 | ❌ (audiosrv 재시작 필요) | ✅ (GUI와 동일) |
| 부작용 | 오디오 끊김, 독점 모드 앱 충돌, Win11 `AUDCLNT_E_DEVICE_INVALIDATED` | 없음 |
| 권한 | 관리자 필요 (HKLM) | 대화형 사용자 권한 가능 |

### IPolicyConfig (권장)

비공개/비문서 인터페이스지만 Win7 ~ Win11에서 매우 안정적으로 쓰임(EarTrumpet 등 다수).

```cpp
// CLSID_PolicyConfig = {870af99c-171d-4f9e-af0d-e63df40c2bc9}
// IID IPolicyConfig    = {f8679f50-850a-41cf-9c72-430f290290c8}
IPolicyConfig::GetPropertyValue(deviceId, /*bFxStore=*/TRUE, key, &pv); // 현재 상태 읽기
IPolicyConfig::SetPropertyValue(deviceId, /*bFxStore=*/TRUE, key, &pv); // 쓰기 → 즉시 적용
```

- `bFxStore = TRUE` → FxProperties 저장소에 직접 쓰기 = GUI와 동일 경로.
- `key`에 Loudness EQ 키(`{fc52a749-...},3`)를 지정.

### 정식 대안 (참고)

Win11의 `IAudioSystemEffectsPropertyStore`가 문서화된 정식 API지만,
**패키지 앱 + `audioDeviceConfiguration` restricted capability**를 요구해 데스크톱 트레이 앱에는 과함.
→ 현 단계에서는 IPolicyConfig 채택.

---

## 3. 드라이버 의존성

- Loudness Equalization 자체는 **Windows Vista부터 내장된 Microsoft 제공 DSP/APO**.
- 그러나 Vista 문서가 명시하듯 OEM/드라이버가 이 기본 효과를 **수정·교체·제거 가능** →
  실제 가용성은 장치/드라이버에 달림.
- ✅ 동작: **Realtek HD Audio** 등 해당 APO를 제공하는 엔드포인트.
- ❌ 미동작 가능: **Intel Smart Audio**, 다수 **Bluetooth / HDMI / USB DAC**,
  **Nahimic / Dolby / DTS** 등 벤더 프리미엄 오디오 — FxProperties 키가 없거나 쓰기가 무시됨.

→ 구현 시 **쓰기 전 키 존재/현재 값을 먼저 읽어** 미지원 장치는 graceful하게 skip.

---

## 4. 기술 스택 결정

요구사항: 항상 상주, 특정 프로세스 시작/종료 감지 → Loudness ON/OFF, 최소 오버헤드.

> **감지 방식 결정: 셀프 폴링(`Process.GetProcessesByName`, 1~2초).** (2026-06-16 갱신)
> 후보 비교 결과 — WMI `__InstanceCreationEvent WITHIN n`은 "이벤트"처럼 보이지만 내부적으로 WMI
> 서비스가 전체 프로세스를 주기 스냅샷(=폴링)해 무겁고, edge 트리거라 한 번 놓치면 상태가 어긋난다.
> `Win32_ProcessStartTrace`(ETW)는 진짜 push지만 **관리자 권한**을 요구해 무권한 원칙과 상충한다.
> 셀프 폴링은 **순수 BCL(외부 의존성 0)** + 이름만 열거해 가볍고, **level 트리거라 매 틱 현재 상태를
> 재평가해 자가 치유**(절전/재개·이벤트 누락에 강함)된다. loudness 토글은 1~2초 지연이 무방하다.

| 스택 | 평가 |
|---|---|
| **C# / .NET 10 (LTS)** ⭐ | COM 상호운용 1급(IPolicyConfig 선언 + NAudio/CSCore), 프로세스 열거 BCL 내장(`Process.GetProcessesByName`), 트레이 앱 쉬움. AOT/trim 시 ~15–30MB. 2025-11 출시 LTS(2028-11까지 지원)라 장기 상주 앱에 적합. **개발 속도·효율 균형 최적 → 채택.** |
| Rust + windows-rs | 최소 풋프린트(바이너리 ~1–3MB, RAM ~2–5MB, GC 없음). windows-rs는 제로코스트 바인딩이라 "추상 레이어" 우려 없음. 단 COM 보일러플레이트·unsafe 비용. |
| Go | ❌ 런타임(GC, 스케줄러) 오버헤드 + COM(go-ole) 상호운용 빈약. COM 중심 작업에 부적합. |
| PowerShell 상주 | ❌ 런타임 통째 상주(~30–60MB+), 장기 서비스로 투박. "최소 오버헤드"와 반대. |

**결정: C# / .NET 10 (LTS) 트레이 앱.**
- 숨김 트레이 앱 → 사용자 세션에서 동작하므로 IPolicyConfig 권한 문제 없음.
- 셀프 폴링(`Process.GetProcessesByName`, 1~2초)으로 대상 프로세스 시작/종료 감지(무권한·level 트리거·자가치유).
- 상태 변화 시 IPolicyConfig로 Loudness EQ 키 토글(즉시 적용, 서비스 재시작 없음).

### 구현 골격
1. COM(STA) 초기화.
2. `IMMDeviceEnumerator`로 기본 출력 엔드포인트 ID 획득.
3. 타이머로 대상 프로세스 존재를 1~2초마다 폴링해 시작/종료 감지(존재 여부 edge 검출).
   (종료 감지는 이후 `Process.Exited` 이벤트로 최적화 가능 — 무권한·즉시.)
4. 시작 → `SetPropertyValue(id, TRUE, loudnessKey, ON)`, 종료 → `... OFF`.
5. 쓰기 전 `GetPropertyValue`로 키 존재/현재 값 확인, 미지원 시 skip.

### 개발 환경 — Windows 네이티브 (WSL 비권장)

**결정: Windows 파일시스템에 클론하여 Windows 네이티브 툴체인으로 개발한다.**

이 앱이 의존하는 요소(Core Audio COM `IPolicyConfig`, 레지스트리 FxProperties,
`audiodg.exe`/`audiosrv`, WMI `Win32_Process` 이벤트, 시스템 트레이 + WinForms/WPF 런타임)는
모두 **WSL2(리눅스 VM) 안에 존재하지 않는다.** 코드는 어디서든 컴파일되지만,
`read → flip → write-back`이 실제로 먹히는지·APO가 즉시 재로드되는지 등의 **실행·검증이 불가능**하다.
이 프로젝트는 "빌드 → 실행 → 오디오 토글 → 레지스트리/소리 변화 관찰"의 반복이 핵심이라
실행 환경이 곧 Windows여야 한다.

- 저장소는 **Windows 파일시스템(NTFS, 예: `C:\dev\loudswitch`)** 에 둔다.
  `/mnt/c/...`를 WSL에서 빌드하면 I/O가 느리고 파일 감시(inotify)가 부실하므로 피한다.
- 툴체인: Visual Studio / Rider / Windows용 VS Code + Windows `dotnet`.

### 개발 환경 실측 메모 (2026-06-16)

이 개발 PC는 활성 출력 3개 모두 Loudness EQ를 **네이티브로 노출하지 않음**(기본 "스피커"는 FX 값
138개의 벤더 APO 스택, 나머지는 빈 FxProperties). 따라서 검증용으로만 빈 엔드포인트
`{3ecd2d07-...}`("스피커")에 MS 기본 APO + Loudness 키를 **force-create**해 테스트베드를 구성했다.

- 스크립트: `dev/Setup-LoudnessTestbed.ps1` (주입 / `-Revert`). **앱 범위 아님** — 앱은 노출된 경우만 토글.
- 권한 함정: MMDevices 키는 SYSTEM 소유이고 Administrators엔 `SetValue, ReadKey`만 있음(CreateSubKey 없음).
  PowerShell `New-ItemProperty`는 키를 `KEY_WRITE`(=SetValue+CreateSubKey)로 열어 거부됨 →
  .NET `RegistryKey`를 정확히 `SetValue` 권한으로 열어 써야 함.

## 구현 진행 현황

- ✅ 슬라이스 0+1: .NET 10 콘솔 스캐폴딩 + **다중** 출력 장치 Loudness 상태 읽기(레지스트리, friendly name 포함).
- ✅ 슬라이스 2: `IPolicyConfig::SetPropertyValue(bFxStore=TRUE)`로 ON/OFF 토글 —
  **HRESULT S_OK, 관리자 권한 불필요**(§2 주장 검증), read-back 즉시 반영, ON/OFF 바이트 확정(§1).
- ⏭ 다음: 슬라이스 3(WMI 프로세스 감지) → 4(통합) → 5(트레이 UI).
- ⚠ 미검증(의도적 descope): "**귀에 들리는**" 즉시 APO 재로드. API 쓰기 경로 자체는 검증됨.

---

## 5. 미해결 질문 (구현 중 실측 필요)

- [x] ~~Loudness EQ **끄기** 값의 정확한 PROPVARIANT 형식?~~ ✅ **해소 (2026-06-16)**:
  `hex:0b,00,00,00,01,00,00,00,00,00,00,00` (VT_BOOL=FALSE). §1 "값 형식" 참고.
- [ ] `{fc52a749-...},3` 키가 Realtek 외 드라이버(또는 MS 기본 HD Audio 드라이버)에서도 동일하게 동작하는가?
- [ ] Win11 24H2+에서 FxProperties 쓰기가 새 설정 UI에 의해 덮어써지지 않고 영속되는가?
- [ ] 다중 출력 장치 환경에서 "기본 장치 변경" 시 토글 대상 갱신 처리.

---

## 6. 반증된 주장 (재조사 방지용 기록)

조사 중 적대적 검증으로 **기각된** 주장들 — 채택하지 않음:

- ❌ Loudness EQ 키가 `{E0A941A0-88A2-4df5-8D6B-DD20BB06E8FB},4` 이다. (1-2 기각;
  올바른 키는 `{fc52a749-...},3`)
- ❌ 토글 로직이 별도 네이티브 exe를 호출해야 한다. (0-3 기각)
- ❌ 레지스트리 값만 고치면 서비스 재시작 없이 적용된다. (0-3 기각 — 레지스트리 직접 쓰기는
  재시작 필요. 단, *API 경로*는 즉시 적용되는 별개 사실.)
- ❌ FxProperties 내 설정이 단순히 0/1 데이터를 갖는 또 다른 GUID 값이다. (0-3 기각 —
  실제론 직렬화 PROPVARIANT)
- ❌ WMI `__InstanceCreationEvent WITHIN` 구독은 "이벤트 기반이라 0% CPU"다. (2026-06-16 기각 —
  내부적으로 전체 프로세스를 주기 폴링해 무겁고 edge 트리거. 진짜 push인 `Win32_ProcessStartTrace`는
  관리자 필요. → 무권한·경량·자가치유를 위해 **셀프 폴링** 채택. §4 참고.)

---

## 7. 참고 자료

### 1차 출처 (Microsoft / 동작 코드)
- [Microsoft Learn — Loudness Equalization DSP](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/loudness-equalization-dsp)
- [Microsoft Learn — PKEY_AudioEndpoint_Disable_SysFx](https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-audioendpoint-disable-sysfx)
- [Microsoft Learn — Audio Processing Object Architecture](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-processing-object-architecture)
- [Microsoft Learn — Windows 11 APIs for Audio Processing Objects](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/windows-11-apis-for-audio-processing-objects)
- [Microsoft Learn — AUDIO_ENDPOINT_PROPERTY_CHANGE_NOTIFICATION](https://learn.microsoft.com/en-us/windows/win32/api/audioengineextensionapo/ns-audioengineextensionapo-audio_endpoint_property_change_notification)
- [Microsoft Learn — IAudioSystemEffectsPropertyStore](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-iaudiosystemeffectspropertystore)
- [Microsoft Q&A — how to control enable audio enhancements with code](https://learn.microsoft.com/en-us/answers/questions/669471/how-to-control-enable-audio-enhancements-with-code) (IPolicyConfig 예제 + "GUI처럼 즉시 적용" 보고)
- [dechamps/APO — Windows Audio Processing Objects 노트](https://github.com/dechamps/APO/blob/master/README.md)
- [Falcosc/enable-loudness-equalisation (EnableLoudness.ps1)](https://github.com/Falcosc/enable-loudness-equalisation/blob/main/EnableLoudness.ps1) — 레지스트리 키/값의 동작 참조 구현(단, force-create + 서비스 재시작 방식)
- [TomasBisciak/Windows-Loudness-Equalization-toggle](https://github.com/TomasBisciak/Windows-Loudness-Equalization-toggle)

### 보조 / 토론
- [EarTrumpet Discussion #656 — Loudness Equalization toggle 요청](https://github.com/File-New-Project/EarTrumpet/discussions/656)
- [ethano8225/Force-Loudness-EQ](https://github.com/ethano8225/Force-Loudness-EQ)
- [Equalizer APO — Developer documentation](https://sourceforge.net/p/equalizerapo/wiki/Developer%20documentation/?version=2)
- IPolicyConfig 선언 참고: [tartakynov/audioswitch (IPolicyConfig.h)](https://github.com/tartakynov/audioswitch/blob/master/IPolicyConfig.h), [ThiefMaster/coreaudio-dotnet (IPolicyConfig.cs)](https://github.com/ThiefMaster/coreaudio-dotnet/blob/master/CoreAudio/Interfaces/IPolicyConfig.cs)
