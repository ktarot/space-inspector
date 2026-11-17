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

## 다음 단계: Phase 2

선택 드라이브/폴더의 메타데이터 인덱싱, 진행률, 취소, 오류 기록, 범위별 재검사, 저장 인덱스 페이지 조회를 구현합니다. 먼저 합성 파일로 검사하고 실제 사용자 파일 자동 검사는 수행하지 않습니다. 온라인 전용 파일 콘텐츠와 디렉터리 링크를 열지 않는 정책을 검사 시작 전에 적용해야 합니다.
