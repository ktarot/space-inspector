# Space Inspector

Windows 10/11용 로컬 파일 검색·인덱싱·중복 분석 도구입니다. 한국어 WPF UI와 C# / .NET 10, SQLite를 사용합니다.

현재 단계는 **Phase 1 — 프로젝트 초기화**입니다. 드라이브 선택, 로컬 드라이브 용량 조회, SQLite 초기화 및 인덱스 건수 조회를 구현했습니다. 파일 검사·검색·중복 분석은 아직 제공하지 않으며 화면에 준비 중으로 표시합니다.

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
- 앱의 네트워크 통신, 파일 콘텐츠 읽기, 해시 및 클라우드 다운로드 기능은 구현되어 있지 않습니다.
- SQLite, 검사 로그, 개인 설정 및 빌드 산출물은 Git에서 제외합니다.
- 테스트는 임시 폴더의 합성 데이터만 사용합니다.

## 구조

- `src/SpaceInspector.App`: WPF 화면, MVVM, 앱 구성
- `src/SpaceInspector.Core`: 모델과 서비스 계약
- `src/SpaceInspector.Infrastructure`: 로컬 드라이브 조회, SQLite 스키마
- `tests/SpaceInspector.UnitTests`: 대시보드 상태 및 드라이브 표시 테스트
- `tests/SpaceInspector.IntegrationTests`: SQLite 생성·재실행·취소·스키마 보호 테스트

[개발 명세](docs/PRD.md) · [아키텍처](docs/ARCHITECTURE.md) · [구현 현황](docs/IMPLEMENTATION.md) · [검증 체크리스트](docs/TEST_CHECKLIST.md)
