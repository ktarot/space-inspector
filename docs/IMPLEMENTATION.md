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
- [x] GitHub Actions 테스트 30개 통과 (건너뜀 0), x64 아티팩트 생성·다운로드 확인

검증된 구현 커밋: `732abdc565664032cdd0bacb0df73b5435481b5c`.
[Windows CI 결과](https://github.com/ktarot/space-inspector/actions/runs/44059580174)에서 빌드, 단위/UI 8개, 통합 22개, 자체 포함형 배포와 업로드가 모두 성공했습니다.
[Phase 2 실행 파일 다운로드](https://github.com/ktarot/space-inspector/actions/runs/44059580174/artifacts/14317223143) (GitHub 로그인 필요, 보관 기간 14일).

주요 파일: `Core/Indexing/FileIndexingService.cs`, `Core/Indexing/ScanScope.cs`, `Infrastructure/FileSystem/MetadataOnlyFileSource.cs`, `Infrastructure/Database/SqliteIndexDatabase.Indexing.cs`, `App/ViewModels/MainViewModel.cs`, `App/Views/MainWindow.xaml`.

미검증/제약: 실제 OneDrive 무다운로드, 실제 ACL 접근 거부/장치 분리, Windows 10/11 각각의 고배율 수동 검증 및 수백만 파일 성능은 후속 확인이 필요합니다. 권한 실패와 장치 해제는 합성 오류/경로 제거로 검증했습니다. 파일 메타데이터는 검사 시점의 스냅샷이며 실행 중 파일 시스템 변경은 다음 재검사에서 갱신합니다. Windows의 일반적인 대소문자 비구분 경로를 기준으로 하며 별도로 활성화된 대소문자 구분 디렉터리는 지원하지 않습니다.

## Phase 3 — 조건 검색

- [x] 파일명 부분 문자열, 복수 확장자, 최소/최대 크기, 생성/수정 날짜
- [x] AND 복합 조건, 선택 드라이브/폴더 범위 필터
- [x] 전체 결과 기준 이름/크기/생성일/수정일/경로 정렬과 200개 페이징
- [x] 기준 파일 이름·확장자 가져오기 (콘텐츠 비교와 구분)
- [x] 선택 파일 경로 복사 및 Explorer 위치 열기
- [x] 재실행 후 마지막 검사 요약·오류/건너뜀 복원
- [x] 기존 Phase 2 DB 재사용, 스키마 추가 변경 없음
- [x] Release 빌드 경고/오류 0, 로컬 테스트 37개 통과/Junction 1개 건너뜀
- [x] WPF 검색 적용·기준 파일·결과 선택·경로 명령·검사 요약 복원 검증 및 렌더링
- [x] Windows x64 자체 포함형 publish
- [ ] GitHub CI 및 아티팩트 확인

주요 파일: `Core/Models/FileSearch.cs`, `Core/Interfaces/IFileSearchRepository.cs`, `Infrastructure/Database/SqliteIndexDatabase.Search.cs`, `App/ViewModels/MainViewModel.cs`, `App/Views/MainWindow.xaml`, `App/App.xaml.cs`, 검색 통합 및 WPF 테스트.

한계: 실제 836,224개 사용자 DB의 검색 시간은 측정하지 않았습니다. 부분 이름과 날짜 변환 조건은 전체 인덱스를 읽을 수 있으므로 대규모 결과에서 시간이 걸릴 수 있습니다. UI는 작업 스레드에서 검색하며 화면 목록은 페이지 단위로 로드합니다. Explorer/실제 클립보드 실행은 수동 확인이 필요하며 자동 테스트는 호출 인자 전달을 검증합니다. OneDrive·OS별 고배율 등 기존 미검증 항목은 계속 유지합니다.

## 다음 단계: Phase 4 — 중복 파일 검색

크기 후보, 부분 해시, 전체 SHA-256 검증, 검사 중 변경 감지, 하드링크 구분, 중복 그룹 및 예상 공간을 구현합니다. 클라우드 파일의 콘텐츠 검사 방지 조건을 먼저 적용합니다.
