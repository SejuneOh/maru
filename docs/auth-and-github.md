# 로그인과 GitHub 연결

2026-11-10 팀 회의에서 정한 방향입니다. 구현은 #3, #2, #32, #33, #34, #35에서 나눠 합니다.

## 결정

| 항목 | 결정 |
|---|---|
| 로그인 | "GitHub으로 로그인" 하나. Supabase Auth GitHub provider(OAuth App, 프로필만) |
| 사람 식별 | GitHub 사용자 id. 서버가 `auth.identities`에서 읽음. `user_metadata`는 사용자가 바꿀 수 있어 쓰지 않음 |
| 저장소 연결 | GitHub App 설치. 조직과 개인 계정 모두 |
| 회사와 연결 | 회사 1 : 연결 N (`github_connections`). 같은 GitHub 계정은 한 회사에만 |
| 권한 | 읽기 전용(metadata, issues, pull requests, 조직 구성원 읽기)으로 시작. 보드에서 이슈를 쓰는 기능(#59)부터 Issues는 읽기와 쓰기 |
| 데이터 읽기 | 서버가 App 개인 키로 발급한 설치 토큰만 사용. 사용자 토큰으로 데이터를 읽지 않음 |
| 지금(M1) | 개인 토큰(fine-grained, 이 저장소 한정)으로 동기화. `IGitHubSource` 뒤에 두고 M3에서 설치 토큰으로 교체 |

## Jira와 비교

| 항목 | Jira Cloud | work-board |
|---|---|---|
| 로그인 | Atlassian 계정(이메일, Google, Microsoft, Apple, Slack). GitHub 로그인 없음 | GitHub 로그인만 |
| 사람 연결 | 이메일 일치. GitHub 이메일이 비공개면 실패 | GitHub 사용자 id. 이메일과 무관 |
| 연결 대상 | GitHub 조직 | 조직과 개인 계정 |
| 연결 자격 | Jira 사이트 관리자 + GitHub 조직 소유자 | 회사 관리자 + GitHub 소유자 |
| 소유자가 아닐 때 | 소유자에게 승인 링크 | GitHub 설치 요청 후 "다시 확인" |
| 권한 | 코드, 이슈, PR 쓰기까지 한 번에 | 읽기 전용부터 |

## 처음 쓰는 흐름

1. "GitHub으로 로그인"
2. 소속 회사가 없으면 "회사 만들기"(만든 사람이 관리자). 초대받았으면 그 회사로 합류
3. "GitHub 연결" → GitHub 설치 화면에서 내 계정 또는 조직, 모든 저장소 또는 선택
4. 돌아오면 "가져오는 중…", 지난 기록을 가져온 뒤 보드 표시
5. 동료는 초대 링크로 들어와 초대받은 GitHub 계정으로 로그인

## 초대와 내보내기 (#39)

- 관리자가 GitHub 로그인으로 초대하면 서버가 그 자리에서 `GET /users/{login}`으로 GitHub 사용자 id를 찾아 저장합니다. 로그인은 바뀌거나 다른 사람이 다시 쓸 수 있어 표시용으로만 둡니다. 회사에 연결된 App 설치 토큰을 쓸 수 있으면 그것으로(시간당 5000회), 아니면 인증 없이(서버 IP당 시간당 60회) 부릅니다.
- 초대 링크는 `/join#<token>`입니다. 토큰은 32바이트 난수이고 DB에는 SHA-256만 둡니다. 링크는 만들 때 한 번만 보여 주며, 다시 필요하면 같은 사람을 다시 초대해 새 링크를 만듭니다(이전 링크는 취소). 유효 기간은 7일, 한 번만 쓸 수 있고, 역할은 항상 `member`입니다.
- 합류에는 토큰과 로그인한 사람의 GitHub id가 둘 다 맞아야 합니다. id는 다른 요청과 같은 경로(`auth_links`, Supabase 신원의 `provider_id`)로 얻습니다. 링크가 새어도 다른 계정은 `invite_other_account`만 받고 회사 이름도 보지 못합니다.
- 합류 요청은 아직 구성원이 아니므로 `workboard_sync` 역할에서 `token_hash`로 한 행만 찾습니다. `invitations`는 RLS와 FORCE RLS가 켜져 있어 앱 역할로는 자기 회사 초대만 보입니다.
- 내보내기는 `members.left_at`을 찍습니다. 다음 요청부터 그 회사에 접근할 수 없고, 쓴 노트, 감사 기록, 담당 기록은 이름과 함께 남습니다. 그 사람이 보낸 열린 초대는 함께 취소합니다. 다시 들어오려면 새 초대가 필요합니다. 마지막 관리자는 내보낼 수 없습니다.
- `left_at`(회사에서 내보냄, 접근 차단)과 `removed_at`(GitHub 조직에서 빠짐, #35, 접근은 그대로)은 다른 값입니다.

## 연결한 사람 확인

웹훅의 `sender`는 소유자 승인 경로에서 승인한 소유자로 찍히므로 요청한 사람 확인에 쓸 수 없습니다. 대신:

1. GitHub App에서 "설치 중 사용자 인증 요청"을 켬
2. 설치 후 돌아올 때 받은 `code`를 서버가 사용자 토큰으로 교환
3. 그 토큰으로 `GET /user/installations`에 방금 설치가 있는지 확인
4. 확인 후 토큰은 버림

## 조직 연결 (#40)

조직은 두 경로로 연결됩니다. 어느 쪽이든 회사 관리자만 할 수 있고, 조직 구성원 여부는 클라이언트가 보낸 값이 아니라 로그인한 사람의 GitHub id로 확인합니다. 확인에는 그 설치의 토큰으로 `GET /orgs/{org}/memberships/{login}`을 부르고, 응답의 `state`가 `active`이고 `user.id`가 로그인한 사람의 id와 같아야 합니다(로그인 이름은 주소에만 쓰므로 바뀌어도 다른 사람으로 통과하지 않음). 그래서 App에 조직 권한 "Members: read"가 필요하고, 없으면 `org_members_not_granted`입니다.

1. 소유자가 직접 설치: 위 1~3단계(사용자 토큰으로 본인 확인, `GET /user/installations`에 설치가 있는지)에 더해 그 조직의 **소유자**(`role=admin`)여야 합니다. 일반 구성원은 `not_org_owner`로 막고 아래 요청 경로를 쓰게 합니다.
2. 승인 대기 후 연결: 소유자가 아니면 GitHub이 `setup_action=request`로 돌려보내고 설치 id가 없습니다. 웹이 `POST /api/connections/github/request`로 회사에 요청 하나를 저장합니다(`github_connection_requests`: 요청한 구성원, 요청 시각, 연결되면 조직 로그인과 연결 id). 화면은 "관리자에게 승인을 요청했습니다"입니다. 요청은 7일 동안만 열려 있습니다(지나면 읽을 때 없는 것으로 보고, 확인·새 요청 때 닫음). 소유자가 승인한 뒤 "다시 확인"(`POST .../request/check`)을 누르면 서버가 App JWT로 `GET /app/installations`를 읽어, 조직이고, 정지되지 않았고, 요청 5분 전 이후에 만들어졌고, 어느 회사에도 연결되지 않은 설치만 고릅니다. 그중 누른 관리자가 **활성 소유자**인 조직이 정확히 하나면 직접 설치와 같은 저장 경로로 연결합니다. 일반 구성원이면 `not_org_owner`("조직 소유자가 승인한 뒤 보드에 로그인해 다시 확인을 눌러야 합니다")이고, 해당 조직이 없으면 "아직 승인되지 않았습니다", 둘 이상이면 어느 조직인지 고르게 합니다. 이미 다른 회사에 연결된 설치는 누른 사람이 그 조직의 소유자일 때만 `account_connected`로 알리고, 아니면 "아직 승인되지 않았습니다"로 답해 연결 여부를 드러내지 않습니다. 조직 소유자가 초대로 합류한 구성원이면, 관리자가 설정의 구성원 목록에서 "관리자로 지정"한 뒤 소유자가 "다시 확인"을 누릅니다(`PATCH /api/members/{id}`).

다른 회사가 가로챌 수 없는 이유:

- 설치 id만 알아서는 연결되지 않습니다. 직접 경로는 사용자 토큰과 소유자 확인이, 요청 경로는 설치 id를 받지 않고 서버가 고른 후보와 구성원 확인이 필요합니다.
- 요청 경로는 요청 뒤에 생긴 설치만 보고, 그 조직의 소유자만 연결할 수 있습니다. 같은 조직에 설치를 요청한 다른 회사의 일반 구성원이 소유자의 승인을 가로챌 수 없습니다.
- 활성 연결은 설치와 계정마다 하나(`github_connections`의 부분 유일 인덱스)입니다. 이미 다른 회사에 연결된 설치는 후보에서 빠지고, 동시에 저장하면 한쪽은 `account_connected`입니다.

개인 계정에는 요청 경로가 없습니다.

### 조직 구성원

연결하면 설치 토큰으로 `GET /orgs/{org}/members`를 읽어 `github_org_members`(연결별 GitHub id, 로그인, `removed_at`)에 둡니다. 화면에는 연결 행의 구성원 수로만 보여 줍니다. 이 목록은 회사 접근 권한과 무관하며, 회사에 들어오려면 여전히 초대(#39)가 필요합니다. 읽기에 실패해도(권한 승인 전 등) 연결은 유지되고 구성원 수만 비어 있습니다. 다시 읽기는 같은 조직을 다시 연결할 때 일어납니다.

## 소유자 승인

조직 소유자 승인은 조직에 처음 설치할 때 한 번만 필요합니다. 설치 뒤의 사용, 개인 계정 연결, 설치 요청에는 필요 없습니다. 첫 사례인 이 저장소는 개인 계정이라 승인 없이 끝까지 만들고 검증합니다.

## 웹훅으로 바로 반영 (#35)

`POST /api/github/webhook`이 GitHub App 웹훅을 받습니다. 요청 형식과 응답 코드는 `docs/api-contract.md`의 "GitHub webhook"에 있습니다.

- 진위 확인은 `X-Hub-Signature-256`(원문 바이트에 대한 HMAC-SHA256, 상수 시간 비교)뿐입니다. 비밀은 `GitHub__App__WebhookSecret`이고, 없거나 `Sync:Enabled=false`(큐를 비울 워커가 꺼짐)이면 엔드포인트는 503으로 아무것도 저장하지 않습니다. 서명이 틀리거나 없으면 401이며 DB에 닿지 않습니다. 본문은 2 MB까지입니다.
- 중복은 `X-GitHub-Delivery`로 거릅니다. `webhook_deliveries`의 기본 키가 곧 중복 방지이고, 요청은 행 하나만 넣고 바로 응답합니다(GitHub 제한 10초). 본문은 저장하지 않고 라우팅에 필요한 값(이벤트, 동작, 설치 id, 저장소 id, 구성원 id, 조직 id)만 둡니다. 처리는 `WebhookWorker`가 큐처럼 비웁니다. 처리된 행은 14일 뒤 지웁니다.
- 이슈, 댓글, PR, 리뷰, 마일스톤, 라벨 이벤트는 페이로드를 쓰지 않고 해당 저장소를 마지막 동기화 이후로 다시 읽어 `MirrorWriter`로 반영합니다. 일부 필드만 담긴 페이로드를 믿지 않고, 주기 동기화와 같은 경로라 "오래된 것이 새것을 덮지 않음", `issue_events`, `PendingLink` 규칙이 그대로 적용됩니다.
- `installation.deleted`, `installation.suspend`는 연결에 `disconnected_at`을 찍어 동기화와 쓰기를 멈춥니다. 연결 화면은 "연결 끊김"으로 보여 줍니다. `installation.unsuspend`는 되돌립니다. 새 설치(`created`)는 기존 연결 흐름이 맡으므로 웹훅은 무시합니다.
- `organization.member_removed`/`member_added`는 연결의 `account_id`가 `organization.id`와 같을 때 그 연결의 `github_org_members` 행에 `removed_at`을 찍거나 지웁니다(행이 없으면 만듦). `members.removed_at`은 여기서 계산합니다: 회사의 활성 조직 연결 중 하나라도 현재 구성원이면 null, 아니면 GitHub이 빠졌다고 알린 시각. 그래서 조직 A에서 빠졌어도 연결된 조직 B에 남아 있으면 표시하지 않습니다. 구성원 행과 접근 권한은 그대로이며 `GET /api/members`의 `removedAt`과 새 이슈 담당자 목록에만 반영됩니다. `installation.deleted`/`suspend`/`unsuspend`로 연결이 끊기거나 되살아나면 그 회사의 `members.removed_at`을 활성 연결 기준으로 다시 계산합니다. 그래서 조직 X에서 빠진 사람도 X의 연결이 끊기면 더는 빠진 사람으로 표시되지 않습니다.
- 주기 동기화는 웹훅이 놓친 변경을 보정하는 용도로 남습니다. 기본 10분이고, 최근 30분 안에 검증된 전달이 있을 때만 60분으로 늘어납니다. `Sync:IntervalMinutes`를 지정하면 그 값을 그대로 씁니다. 간격은 다음 실행을 기다리기 직전에 정하므로, 웹훅이 끊긴 직후에는 보정이 최대 약 90분 늦을 수 있습니다.

## 확인할 것

- Supabase 프로젝트의 JWT 서명 방식(JWKS 비대칭 키인지 공유 키인지): 프로젝트를 만들 때 확인하고 #32에 기록
- `setup_action=request`와 승인 후 동작: 개인 테스트 조직으로 시험(README "조직 연결"). 실제 회사 조직은 회사 정책이 정해질 때까지 연결하지 않음
- 로그인 앱과 데이터 앱을 GitHub App 하나로 합칠 수 있는지: Supabase 문서는 OAuth App만 안내하므로 지금은 나눔

## 설치 토큰으로 읽기

`IGitHubSource`(`Infrastructure/GitHub/`)가 설치 토큰으로 GitHub을 읽습니다. DB에는 쓰지 않고 `Source*` 레코드만 돌려주며, 필드는 `issues`, `pull_requests`, `milestones`, `labels`, `comments` 컬럼에 대응합니다.

- 토큰: App JWT로 `POST /app/installations/{id}/access_tokens`를 호출해 받고, 설치 id별로 메모리에만 캐시합니다. 만료 5분 전에 새로 받으며 로그와 DB에 남기지 않습니다. 401을 받으면 캐시를 버리고 한 번 다시 시도합니다.
- 저장소 목록: `ListRepositoriesAsync` (`GET /installation/repositories`, 페이지 순회)
- 저장소 읽기: `ReadRepositoryAsync(installationId, owner, name, since)`. `since`가 null이면 전체입니다. 이슈, PR, 댓글은 `updatedAt` 오름차순이며 `since` 이후 것만 담깁니다. 마일스톤과 라벨은 항상 전체 목록입니다.
- GraphQL: 이슈(하위 이슈 부모, 이슈 유형, 라벨, 담당자), PR(리뷰 요청, `closingIssuesReferences`). GraphQL에 DB id가 없는 마일스톤, 라벨, 댓글은 REST로 읽습니다.
- 속도 제한: 결과의 `RateLimit`(남은 횟수, 초기화 시각). 실패는 `GitHubSourceException`의 `Kind`(Unauthorized, Forbidden, NotFound, RateLimited, GraphQl, Unexpected)로 구분합니다.

동기화 쓰기(#6)가 알아야 할 것:

- `/issues/comments`는 PR 댓글도 돌려줍니다. `IssueNumber`로 걸러야 합니다.
- 댓글은 `page=`로 순회해서, 긴 읽기 중에 수정된 댓글을 놓칠 수 있습니다. `since`에 겹치는 구간을 둡니다.
- 연결 크기 상한: 라벨 50, 담당자 20, 리뷰 요청 20, `closingIssuesReferences` 20.
- `GitHubSource`는 transient 타입드 클라이언트입니다. 스코프마다 받아 쓰고 싱글톤에 들고 있지 않습니다.
- `closingIssuesReferences`는 기본 브랜치로 향하는 PR에만 채워집니다. `dev`로 가는 PR은 비어 오므로, 레코드의 `Body`에서 `Closes/Fixes/Resolves #N`도 직접 읽어야 합니다.
- GraphQL 중첩 노드 오류(권한 없는 부모 이슈 등)는 무시하고 나머지 데이터를 씁니다. 저장소 루트 오류와 `RATE_LIMITED`만 예외로 올립니다.

## 미러 쓰기 규칙

`MirrorWriter`(`Infrastructure/Sync/`)가 `RepositorySnapshot`을 `workboard_sync` 역할로 테넌트 테이블에 씁니다. 저장소 하나가 트랜잭션 하나입니다.

- GitHub id로 upsert합니다. 저장된 `github_updated_at`보다 오래된 이슈, PR, 댓글은 자식 행(라벨, 담당자, PR 링크)과 함께 무시합니다(같은 시각은 다시 씀). 같은 스냅샷을 두 번 적용해도 결과가 같습니다.
- 마일스톤과 라벨은 전체 목록이므로 목록에 없는 행은 삭제합니다. 라벨 이름 교환은 이름 유일성을 커밋 시점에 검사해 허용합니다.
- 이슈와 PR, 댓글, 저장소는 GitHub에서 사라져도 삭제하지 않습니다(삭제·이전된 항목 정리는 이후 과제).
- 부모 이슈는 두 번째 단계에서 연결합니다. 부모의 GitHub id를 `issues.parent_github_issue_id`에 두므로, 부모 저장소가 나중에 동기화돼도 다음 실행에서 이어집니다.
- `issue_pull_requests`: `closingIssuesReferences`(GitHub id 기준)와 PR 본문의 `Closes|Fixes|Resolves #N`(같은 저장소, 대소문자 무시, close/closed/fix/fixed/resolve/resolved 포함)을 합쳐 PR마다 통째로 교체합니다. 이슈가 아직 없으면 그 링크는 PR이 다시 갱신될 때 생깁니다.
- 댓글은 이슈 번호가 이슈 행에 대응하는 것만 씁니다(PR 댓글 제외).
- 다른 테넌트의 행은 읽지도 쓰지도 않습니다. 모든 문장이 `tenant_id`로 거릅니다.

## GitHub 쓰기

`IGitHubWriter`(`Infrastructure/GitHub/`)가 설치 토큰으로 이슈를 만들고, 담당자를 바꾸고, 댓글을 달고, 닫고(`completed` | `not_planned`), 다시 엽니다. GitHub에는 App 봇이 쓴 것으로 보이므로 실제 사람은 본문과 댓글 끝의 한 줄(`` \n\n---\n_작성: `@<login>` (work-board)_ ``)과 `audit_log`에 남깁니다. 로그인은 백틱으로 감싸서 쓸 때마다 알림이 가지 않게 합니다.

- 응답은 읽기와 같은 `SourceIssue`, `SourceComment`로 돌려줍니다. 호출한 쪽이 `MirrorWriter.ApplyWrittenIssueAsync` / `ApplyWrittenCommentAsync`로 바로 넘기면 다음 동기화를 기다리지 않고 보드에 보입니다. 저장된 `github_updated_at`보다 오래된 응답은 무시하고(자식 행과 이벤트 포함), 응답에 없는 부모 이슈 연결은 건드리지 않습니다.
- 닫기와 다시 열기 호출만 `IssueStateChange`를 넘기며, 그 이벤트(`closed`는 to_value = state_reason, 또는 `reopened`)를 같은 트랜잭션에서 호출한 사람 이름으로 `issue_events`에 남깁니다. 저장된 상태가 이미 같거나 응답이 더 오래돼도 남기고, 다른 쓰기는 상태가 달라 보여도 남기지 않습니다.
- 대상 저장소는 `IGitHubWriteTargets.ResolveAsync`로 확인합니다. 호출자 회사의 저장소이고, 연결이 끊기지 않았고, App 설치 id가 있어야 합니다. 아니면 null(`404 not_found`로 답함).
- 실패는 `GitHubWriteException`: 403 → 메시지가 권한 부족(`Resource not accessible by integration` 등)이면 `github_write_not_granted`, 속도 제한(2차 포함) 문구이거나 그 밖의 403(보관된 저장소 등)이면 502 `github_unavailable`, 422 → `assignee_not_collaborator` 또는 `invalid_request`, 404 → `not_found`, 5xx와 속도 제한 → 502 `github_unavailable`, 응답 없음(네트워크, 30초 타임아웃) → 504 `github_unavailable`. `GitHubWriteErrors.ToResult/ToOutcome`이 응답으로 바꿉니다. 메시지에는 상태 코드만 들어가고 토큰과 응답 내용은 없습니다. 응답 없음 예외는 `Idempotency.RunExternalAsync`에 그대로 던지면 키를 예약 상태로 두고 504를 돌려줍니다.
- 담당자: GitHub는 push 권한이 없는 사용자를 조용히 빼는 경우가 있으므로, 호출한 쪽이 응답의 `Assignees`가 요청과 같은지 확인합니다.

### App 소유자가 할 일: Issues 쓰기 권한

권한을 올려도 설치가 승인하기 전에는 쓰기 호출이 `403`(`github_write_not_granted`)입니다.

1. GitHub → Settings → Developer settings → GitHub Apps → work-board App → **Permissions & events**
2. Repository permissions의 **Issues**를 `Read-only`에서 **Read and write**로 바꾸고 **Save changes**
3. 설치한 계정(개인이면 본인, 조직이면 조직 소유자)에게 권한 변경 요청이 갑니다. Settings → Applications → Installed GitHub Apps → work-board(또는 조직의 Installed GitHub Apps) 화면 위의 안내에서 **Review request** → **Accept new permissions**
4. 승인 뒤 설치 토큰은 새 권한으로 발급됩니다. 캐시된 토큰이 있으면 최대 1시간 안에 바뀌고, 서버를 다시 띄우면 바로 바뀝니다.
