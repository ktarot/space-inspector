# 아키텍처

App → Infrastructure → Core 의존성으로 구성합니다. Core는 UI 및 SQLite 패키지에 의존하지 않습니다. App은 시작 시 구체 서비스를 조립하고 ViewModel은 인터페이스를 통해 호출합니다.

`IDriveService`는 로컬 드라이브 스냅샷을 제공합니다. 연결 해제 및 접근 오류를 용량 0과 구분합니다. UI는 드라이브와 여러 폴더를 검사 대상으로 선택하며, 검사는 명시적인 시작 명령으로만 실행합니다.

`IIndexDatabase`는 초기화 및 요약 조회를 담당합니다. SQLite 동기 I/O는 작업 스레드에서 실행하며 UI 컬렉션 갱신은 UI 스레드에서 수행합니다. 초기화는 트랜잭션과 `PRAGMA user_version`으로 관리합니다. 지원 범위를 초과하는 스키마는 거부하며, 기존 데이터를 자동 삭제하지 않습니다. 파일별 알 수 없는 값은 NULL 또는 Unknown 상태를 허용합니다.

`FileIndexingService`는 UI와 독립된 검사 조정자입니다. `IFileMetadataSource`에서 스트리밍 열거한 결과를 `IScanRepository`에 최대 256개씩 저장합니다. 진행 알림은 약 150ms 간격으로 제한합니다. 최종 파일 수를 미리 알 수 없으므로 가짜 백분율을 표시하지 않습니다. 취소 시 관찰된 마지막 배치를 저장하고 남은 파일을 없음으로 판정하지 않습니다.

`Files.LastSeenScanId`, `ScanScopes`, `ScanIssues`로 재검사 영역과 탐색 실패 영역을 추적합니다. 정상 종료한 검사에서만 선택 영역의 미관찰 행을 Missing으로 표시하며, 접근 실패·건너뜀 경로와 그 하위는 제외합니다. 실제 행을 삭제하지 않습니다. 경로 접두사 비교는 경계 구분자를 포함하며 SQL LIKE 와일드카드를 사용하지 않습니다. SQLite NOCASE 비교는 연결마다 OrdinalIgnoreCase로 등록해 비ASCII 대소문자도 일관되게 처리합니다.

스키마 v2는 v1 데이터를 유지한 채 열과 테이블을 추가합니다. 메타데이터 갱신 시 기존 해시/중복 판정은 무효화합니다. 앱 데이터 폴더의 배타적 잠금으로 한 데이터베이스에 여러 앱 인스턴스가 쓰지 못하게 하며, 시작 시 남아 있는 Running 검사는 Interrupted로 바꿉니다. UI는 최대 200개씩 로드하며 DB에서는 1~1000개로 페이지 크기를 제한합니다.

메타데이터 소스는 파일 스트림을 생성하지 않습니다. UNC/장치 경로와 네트워크 드라이브는 검사하지 않으며, 디렉터리 링크 및 클라우드 회수 속성 폴더는 건너뜁니다. 명시한 폴더의 상위 경로도 루트부터 확인합니다. OFFLINE/RECALL 속성은 [Microsoft 파일 속성 정의](https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants)를 따릅니다. OneDrive 상태를 확정하거나 클라우드 동기화 설정을 변경하지 않습니다.

원본 파일 변경 API는 두지 않습니다. 후속 해시·미디어 분석은 클라우드 상태, reparse point, 변경 여부를 확인한 뒤 별도 서비스로 구현합니다. 경로를 실제 파일 식별자로 간주하지 않으며 향후 볼륨/파일 식별자로 하드링크를 구별합니다. 권한 미확인은 읽기 가능으로 간주하지 않습니다.

후속 태그·분류 규칙·저장 검색은 별도 테이블과 스키마 마이그레이션을 추가합니다. 검사 로그나 사용자 경로는 CI 또는 GitHub 업로드 대상에 포함하지 않습니다.

## Phase 3 검색

`FileSearch`는 UI에 독립된 조건 모델이며 `IFileSearchRepository`를 통해 저장 인덱스를 조회합니다. SQL 값은 모두 매개변수로 전달하고 정렬 열은 enum 화이트리스트로 선택합니다. 이름은 OrdinalIgnoreCase 부분 문자열 함수, 확장자는 NOCASE 비교, 경로는 디렉터리 경계를 포함한 비교를 사용합니다. 조건은 AND, 확장자 및 범위 목록 내부는 OR입니다. 날짜는 julianday로 UTC/오프셋을 비교하고 UI 날짜 끝값은 다음 날 자정 미만으로 바꿉니다.

COUNT와 페이지 조회는 같은 읽기 트랜잭션을 사용합니다. 모든 정렬은 FileId를 추가해 같은 값의 페이지 순서를 안정화합니다. 입력한 필터와 적용된 검색 모델을 분리해 페이지를 넘길 때 미적용 입력이 섞이지 않도록 합니다. 재실행 시 마지막 ScanRuns 행과 해당 ScanIssues를 복원합니다.

클립보드, 파일 선택, Explorer 호출은 앱 구성에서 주입하며 테스트에서 OS 동작을 대체합니다. 기준 파일 선택은 이름·확장자만 가져오며 파일 콘텐츠를 읽지 않습니다. 파일 상세 권한, 분류/시스템 포함 필터, 미디어 길이는 이후 Phase의 분석 결과와 연결합니다.

조회 취소는 검사 취소와 별도 토큰을 사용합니다. Microsoft.Data.Sqlite의 Command.Cancel은 실행을 중단하지 않으므로 `SqliteQueryCancellation`에서 [SQLite progress handler](https://www.sqlite.org/c3ref/progress_handler.html)를 등록해 1000 VM 명령마다 취소를 확인합니다. SQLITE_INTERRUPT는 토큰이 취소된 경우 OperationCanceledException으로 바꿉니다. 읽기 트랜잭션을 해제하기 전에 핸들러를 제거합니다. UI는 성공한 조회만 결과와 적용 조건/페이지에 반영합니다.
