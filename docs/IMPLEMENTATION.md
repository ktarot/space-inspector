# 구현 현황

## Phase 1

- [x] 빈 GitHub 저장소 확인 및 로컬 연결
- [x] .NET 10 WPF 솔루션, App/Core/Infrastructure 구조
- [x] MVVM 기본 구성과 한국어 대시보드
- [x] 로컬 드라이브 목록 및 용량, 선택 상태
- [x] SQLite 초기화와 인덱스 건수 조회
- [x] 단위/통합 테스트 작성
- [x] Windows CI, 테스트 및 자체 포함형 x64 아티팩트 설정
- [x] README, 명세, 아키텍처, 검증 체크리스트
- [x] 로컬 Release 빌드 성공: 경고 0, 오류 0
- [x] 테스트 10개 통과: 단위/UI 6개, SQLite 통합 4개
- [x] WPF 기본 창 생성·로딩·렌더링 검증 (합성 드라이브, 임시 DB)
- [x] Windows x64 자체 포함형 publish 성공
- [x] GitHub main 반영 및 Actions 성공 확인
- [x] 실행 파일 아티팩트 업로드 확인 (다운로드 링크 제공)

검증된 구현 커밋: `1f90cf2be9616e5b97d9af48aa8ade5a2c680e90`.
[Windows 자동 빌드](https://github.com/ktarot/space-inspector/actions/runs/44056423480)에서 복원, 빌드, 테스트, publish 및 아티팩트 업로드가 모두 성공했습니다.
[SpaceInspector-win-x64 다운로드](https://github.com/ktarot/space-inspector/actions/runs/44056423480/artifacts/14316037635) (GitHub 로그인 필요, 보관 기간 14일).

로컬 검증일: 2026-11-17. .NET SDK 10.0.404 사용. 드라이브 사용률 ProgressBar의 기본 TwoWay 바인딩 오류를 실행 테스트에서 발견하여 OneWay로 수정했습니다. NuGet 취약점 검사에서 발견된 이전 SQLite 의존성은 Microsoft.Data.Sqlite 10.0.15로 갱신하여 복원을 통과했습니다. 테스트 도구가 VSTEST_ARTIFACTS_DIRECTORY 관련 안내 메시지를 출력하지만 테스트는 정상 완료됩니다. Windows 10/11 각각의 수동 호환성 및 고배율 검사는 아직 수행하지 않았습니다.

## Phase 2 — 파일 인덱스

- [x] 로컬 드라이브 선택/전체 선택, 여러 폴더 선택, 범위 중복 제거
- [x] 파일 메타데이터 수집, 256개 단위 트랜잭션 저장
- [x] 파일 수·오류 수·건너뜀·현재 경로 표시, 작업 중 상태 및 취소
- [x] SQLite 스키마 1 → 2 마이그레이션, 기존 데이터 보존
- [x] 범위별 재검사 및 원본 없음/접근 거부/장치 접근 불가/재검사 필요 상태
- [x] 검사 범위·이력·오류 기록, 앱 종료 시 검사 취소, 중복 실행 방지
- [x] 기존 인덱스 조회, 200개 페이지, UI 가상화
- [x] 메타데이터만 수집, 디렉터리 링크·회수 속성 폴더·앱 폴더 건너뛰기
- [x] 로컬 Release 빌드 및 x64 자체 포함형 publish
- [x] 로컬 테스트 29개 통과, Junction 1개는 샌드박스 제한으로 CI에서 검증
- [x] WPF 검사 버튼 → 결과 목록 렌더링 확인 (합성 데이터)
- [ ] GitHub Actions 전체 테스트 및 아티팩트 확인

주요 파일: `Core/Indexing/FileIndexingService.cs`, `Core/Indexing/ScanScope.cs`, `Infrastructure/FileSystem/MetadataOnlyFileSource.cs`, `Infrastructure/Database/SqliteIndexDatabase.Indexing.cs`, `App/ViewModels/MainViewModel.cs`, `App/Views/MainWindow.xaml`.

미검증/제약: 실제 OneDrive 무다운로드, 실제 ACL 접근 거부/장치 분리, Windows 10/11 각각의 고배율 수동 검증 및 수백만 파일 성능은 후속 확인이 필요합니다. 권한 실패와 장치 해제는 합성 오류/경로 제거로 검증했습니다. 파일 메타데이터는 검사 시점의 스냅샷이며 실행 중 파일 시스템 변경은 다음 재검사에서 갱신합니다. Windows의 일반적인 대소문자 비구분 경로를 기준으로 하며 별도로 활성화된 대소문자 구분 디렉터리는 지원하지 않습니다.

## 다음 단계: Phase 3 — 조건 검색

파일명·확장자·크기·날짜 조건, 복합 조건, 기준 파일 지정, 정렬, 경로 복사 및 Windows 탐색기 연결을 구현합니다. 파일 콘텐츠 해시는 Phase 4에서 별도 구현합니다.
