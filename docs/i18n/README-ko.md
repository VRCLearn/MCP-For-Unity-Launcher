# MCP for Unity Launcher

[English](../../README.md) | [日本語](README-ja.md) | **한국어** | [繁體中文](README-zh-TW.md) | [简体中文](README-zh.md)

[MCP for Unity](https://github.com/VRCLearn/unity-mcp)를 위한 Unity 에디터 확장 패키지이며, VPM 또는 UPM으로 설치할 수 있습니다. Unity 프로젝트를 열면 로컬 서버를 자동으로 시작하고, 여러 에디터가 서버를 공유하는 동안 계속 실행되도록 유지합니다.

- **자동 시작**: 프로젝트를 열면 서버를 시작하고 Unity 브리지를 연결합니다.
- **서버 공유**: A와 B가 같은 서버를 사용할 때 A를 닫아도 B는 서버를 계속 사용할 수 있습니다.
- **장애 복구**: 관리 대상 서버가 종료되거나 응답하지 않으면 다시 시작합니다. 감독 프로세스가 종료된 경우에도 다시 시작합니다.

**이 기능이 필요한 모든 프로젝트에 Launcher를 설치하세요.** MCP for Unity만 설치된 프로젝트는 기존의 시작 및 종료 동작을 유지합니다.

## 설치

Unity 2021.3 이상과 [uv](https://docs.astral.sh/uv/getting-started/installation/)가 필요합니다. Launcher는 MCP for Unity 10.3.x에 의존하며 Windows, macOS, Linux 데스크톱 에디터를 대상으로 합니다.

### VPM

1. uv가 설치되어 있지 않다면 공식 안내에 따라 설치하세요. 설치 후 Unity를 다시 시작하여 에디터가 `uv`와 `uvx`를 찾을 수 있도록 합니다.
2. [VPM 설치 페이지](https://vrclearn.github.io/MCP-For-Unity-Launcher/)를 열고 **Add to VCC/ALCOMD**를 클릭하거나, 다음 저장소 URL을 직접 추가하세요.

   ```text
   https://vrclearn.github.io/MCP-For-Unity-Launcher/index.json
   ```

   이 저장소 하나에서 **MCP for Unity**와 **MCP for Unity Launcher**를 모두 제공합니다.
3. 각 프로젝트의 패키지 관리 화면에서 **MCP for Unity Launcher**(`com.vrclearn.mcp-for-unity-launcher`)를 설치하세요. VPM은 종속 패키지인 MCP for Unity도 같은 저장소에서 설치합니다.
4. 프로젝트를 열고 **Window → MCP for Unity**에서 **HTTP Local**을 사용하세요. AI 클라이언트의 HTTP 연결을 한 번 설정해야 합니다. 기본 엔드포인트는 `http://127.0.0.1:8080/mcp`입니다.

이후 Launcher가 로컬 서버를 시작하고 에디터를 자동으로 연결합니다. 처음 실행할 때는 uv가 Python과 서버 종속 패키지를 다운로드하므로 시간이 더 걸릴 수 있습니다. AI 클라이언트의 MCP 연결은 별도로 설정해야 합니다.

### UPM (모든 Unity 프로젝트)

UPM으로 설치할 때는 VRChat, VCC, ALCOMD가 필요하지 않습니다.

1. uv를 설치하고 Unity를 다시 시작하여 에디터가 `uv`와 `uvx`를 찾을 수 있도록 합니다.
2. [MCP for Unity 설치 안내](https://github.com/VRCLearn/unity-mcp)에 따라 같은 프로젝트에 **MCP for Unity 10.3.x**를 설치하세요. UPM은 이 패키지의 `vpmDependencies`를 처리하지 않으므로 Launcher보다 먼저 종속 패키지를 별도로 설치해야 합니다.
3. **Window → Package Manager**를 열고 **+ → Add package from git URL**을 선택한 뒤 다음 URL을 입력하세요.

   ```text
   https://github.com/VRCLearn/MCP-For-Unity-Launcher.git?path=/Packages/com.vrclearn.mcp-for-unity-launcher#main
   ```

4. **Window → MCP for Unity**에서 **HTTP Local**을 사용하고 AI 클라이언트의 HTTP 연결을 설정하세요. 기본 엔드포인트는 `http://127.0.0.1:8080/mcp`입니다.

참여하는 각 프로젝트에 Launcher를 설치하세요. 이 Git URL은 `main`의 패키지 하위 디렉터리를 지정합니다. 한 프로젝트에서는 한 가지 설치 방법만 사용하세요.

MCP for Unity가 설치되지 않았거나 버전이 호환되지 않으면 Launcher가 VPM과 UPM 설치 방법을 안내하는 창을 엽니다. 종속 패키지 누락으로 컴파일 오류를 일으키지 않고 호환되는 패키지가 설치될 때까지 기다립니다. **Window → MCP for Unity Launcher**에서 안내를 다시 열 수 있으며, 설치와 재컴파일이 완료되면 Launcher가 자동으로 활성화됩니다.

## 여러 에디터에서 사용하기

프로젝트 A와 B에 두 패키지를 모두 설치하고 같은 로컬 서버 주소를 사용하세요. 두 프로젝트를 연 뒤 A를 닫아도 B는 공유 서버를 계속 사용할 수 있습니다. 마지막 관리 대상 에디터를 닫으면 Launcher는 10초의 유예 시간이 지난 후 자신이 시작한 서버를 종료합니다.

**Window → MCP for Unity Launcher**를 열면 서비스 상태, 참여 중인 에디터, 로그를 확인할 수 있습니다. 자동 관리 설정은 현재 프로젝트에만 적용됩니다.

두 Launcher 창의 **Language** 선택기에서 영어, 일본어, 한국어, 중국어 번체, 중국어 간체를 선택할 수 있습니다. 처음에는 시스템 언어를 따르고 지원하지 않는 언어는 영어로 표시합니다. 선택은 MCP for Unity 설정과 별도로 저장되어 다른 프로젝트에서도 유지됩니다. 진단 로그와 실행 중 오류 메시지는 항상 영어로 표시됩니다.

## 관리 범위 및 검증 상태

Launcher는 로컬 HTTP 서비스를 관리합니다. 원격 HTTP 설정은 기존 관리 방식을 유지합니다. 이미 실행 중인 외부 서버는 재사용하지만 해당 프로세스의 관리 권한을 가져오지는 않습니다.

0.3.0은 macOS의 네이티브 프로세스 식별과 감독 프로세스 종료 시 Linux/macOS 자식 프로세스 정리를 추가합니다. CI는 세 운영체제에서 프로세스 관리, C#/Python 식별 정보의 호환성, 배포 패키지를 검증합니다. Unity 에디터 검증 환경은 Windows와 Unity 2022.3.22f1입니다. 여러 에디터의 대화형 사용과 macOS/Linux에서의 Unity 동작은 아직 검증되지 않았습니다. 자세한 내용은 [검증 기록](../verification.md)을 확인하세요.

## 다운로드 및 문서

- [릴리스](https://github.com/VRCLearn/MCP-For-Unity-Launcher/releases): VPM ZIP, UnityPackage, 패키지 매니페스트를 제공합니다. VPM 사용을 권장합니다. UnityPackage를 사용할 경우 MCP for Unity를 별도로 설치해야 합니다. 한 프로젝트에서는 한 가지 배포 형식만 사용하세요.
- [개발 및 배포](../development.md)
- [아키텍처](../architecture.md)
- [문제 보고](https://github.com/VRCLearn/MCP-For-Unity-Launcher/issues)

## 라이선스

[MIT](../../LICENSE). MCP for Unity는 별도의 종속 패키지이며 해당 기여자들이 유지 관리합니다.
