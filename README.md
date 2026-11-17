# Space Inspector

Windows 10/11용 로컬 파일 검색·인덱싱·중복 분석 도구입니다. 한국어 WPF UI와 C# / .NET 10, SQLite를 사용합니다.

현재 단계는 **Phase 2 — 파일 인덱스**입니다. 로컬 드라이브·여러 폴더의 메타데이터 검사, 진행 상태와 취소, 범위별 재검사, 저장 인덱스 페이지 조회를 제공합니다. 조건 검색·중복 분석은 다음 단계에서 구현합니다.

## 사용 순서

1. 드라이브를 선택하거나 **폴더 추가**로 검사 범위를 지정합니다. 전체 로컬 드라이브는 **전체 선택**을 누릅니다.
2. **검사 시작 / 재검사**를 누르면 파일명, 경로, 크기, 생성·수정 시각, 파일 속성을 수집합니다. 겹치는 상위·하위 폴더는 한 번만 검사합니다.
3. **취소**하면 현재 저장 작업을 마치고 중단합니다. 이미 저장된 결과는 남습니다. 중단된 검사에서 미검사 파일을 삭제·없음으로 처리하지 않습니다.
4. **저장된 파일**에서 200개씩 조회하고, **이번 검사 오류 / 건너뜀**에서 최대 100개 기록을 확인합니다. 전체 오류·범위·검사 결과는 DB에 남습니다.
5. 같은 범위로 다시 검사하면 기존 항목을 갱신합니다. 다른 범위의 인덱스는 유지합니다. 접근하지 못한 영역은 별도 상태를 표시하며 기존 행을 삭제하지 않습니다.

다시 실행하면 저장된 파일 목록을 불러옵니다. 새로고침은 저장 인덱스를 읽으며 자동으로 파일 검사를 시작하지 않습니다. 대량 목록의 정렬/조건 검색은 Phase 3 범위입니다.

## 실행

개발 환경: Windows 10/11 x64, .NET 10 SDK.

```powershell
dotnet restore SpaceInspector.slnx
dotnet build SpaceInspector.slnx -c Release --no-restore
dotnet test SpaceInspector.slnx -c Release --no-build
dotnet run --project src/SpaceInspector.App
```

자체 포함형 실행 파일 생성:

```powershell
dotnet publish src/SpaceInspector.App -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

`artifacts/win-x64/SpaceInspector.exe`를 실행합니다. 배포 시 해당 폴더 전체가 필요합니다. GitHub Actions의 성공한 `Windows build` 실행에서 `SpaceInspector-win-x64` 아티팩트를 내려받아 압축을 풀고 실행할 수도 있습니다. .NET 런타임을 별도 설치할 필요는 없습니다. 자동 Release 생성은 하지 않습니다.

## 데이터와 안전성

- 앱 데이터베이스: `%LOCALAPPDATA%/SpaceInspector/index.db`
- 원본 파일의 내용, 권한, 이름 또는 저장 위치를 변경하지 않습니다.
- 현재 드라이브 조회는 로컬 고정·이동식 드라이브만 대상으로 합니다. 매핑된 네트워크 드라이브는 제외합니다.
- 검사 엔진은 파일 콘텐츠를 열지 않습니다. 디렉터리 링크/Junction 및 오프라인·회수 속성이 있는 폴더는 건너뛰며, 명시적으로 선택한 경로의 상위 폴더도 확인합니다.
- 앱의 네트워크 통신, 해시 및 클라우드 다운로드 기능은 없습니다. OneDrive 실환경 무다운로드 검증 및 상세 상태 분석은 Phase 6에서 수행합니다. 속성만으로 클라우드 여부를 판단할 수 없는 파일은 미검증으로 표시합니다.
- 앱 데이터 폴더와 실행 폴더는 검사에서 제외합니다. 인덱스를 사용하는 앱은 파일 잠금으로 중복 실행을 방지합니다.
- SQLite, 검사 로그, 개인 설정 및 빌드 산출물은 Git에서 제외합니다.
- 테스트는 임시 폴더의 합성 데이터만 사용합니다.

## 구조

- `src/SpaceInspector.App`: WPF 화면, MVVM, 앱 구성
- `src/SpaceInspector.Core`: 모델과 서비스 계약
- `src/SpaceInspector.Infrastructure`: 로컬 드라이브 조회, SQLite 스키마
- `tests/SpaceInspector.UnitTests`: 대시보드/검사 명령 상태 및 WPF 창에서 검사 후 결과 표시
- `tests/SpaceInspector.IntegrationTests`: SQLite 마이그레이션, 실제 임시 파일 검사, 재검사·취소·페이지 조회·Junction 등

Junction 생성 테스트는 기본 로컬 환경에서 건너뛰며, 이를 허용하는 Windows 환경에서 `$env:SPACE_INSPECTOR_JUNCTION_TEST='1'`로 실행합니다. GitHub Actions에서는 항상 활성화합니다. 일반 테스트는 사용자 파일이 아닌 임시 합성 데이터만 사용합니다.

[개발 명세](docs/PRD.md) · [아키텍처](docs/ARCHITECTURE.md) · [구현 현황](docs/IMPLEMENTATION.md) · [검증 체크리스트](docs/TEST_CHECKLIST.md)
