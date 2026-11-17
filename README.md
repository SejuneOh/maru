# work-board

팀 업무 보드. GitHub 이슈·PR·댓글과 회의록을 한 화면에 모아 "누가, 어느 프로젝트에서, 무엇을, 어디까지 했는지"를 보여 줍니다.

## 구성 (로컬 개발)

| 폴더 | 내용 | 주소 |
| --- | --- | --- |
| `db/` | Postgres 17 마이그레이션(`db/migrations/`)과 샘플 데이터(`db/seed/seed.sql`) | `localhost:5434` |
| `server/` | C# (.NET 10) API | `http://localhost:5080` |
| `web/` | Next.js 정적 사이트 | `http://localhost:3000` |
| `docs/api-contract.md` | API 형식. 프론트와 백엔드는 이 문서를 기준으로 맞춥니다 | |

브라우저 → C# API → Postgres 순서로 연결합니다. 회사별 데이터 분리는 DB의 RLS가 맡습니다. API는 요청마다 `app.tenant_id`를 설정합니다.
로그인은 Supabase Auth의 GitHub 로그인입니다. 화면은 `NEXT_PUBLIC_SUPABASE_URL`과 `NEXT_PUBLIC_SUPABASE_ANON_KEY`가 있으면 "GitHub으로 로그인"을 쓰고,
없으면 화면 오른쪽 위의 **개발용 사용자 선택**이 로그인을 대신합니다(`X-Dev-User` 헤더). 화면 설정은 [web/README.md](web/README.md)에 있습니다.

## 실행

```bash
# 1. DB (Docker Desktop의 WSL 연동이 켜져 있어야 합니다)
docker compose up -d

# 2. API (프로젝트 폴더에서 실행해야 appsettings.Development.json을 읽습니다)
#    Development에서는 시작할 때 마이그레이션을 적용하고, 빈 DB면 샘플 데이터도 넣습니다
cd server/src/WorkBoard.Api && dotnet run

# 3. 화면
cd web && pnpm install && pnpm dev

# 테스트 (DB가 떠 있으면 RLS 통합 테스트까지 실행)
cd server && WORKBOARD_TEST_DB=1 dotnet test
```

DB 포트는 다른 프로젝트와 겹치지 않도록 5434를 씁니다.
`docker-compose.yml`, `db/migrations/0001_baseline.sql`, `appsettings.Development.json`의 비밀번호는 로컬 개발 전용 값입니다. 배포 환경에 그대로 쓰지 마세요. `X-Dev-User` 헤더 인증도 Development 환경에서만 켜집니다.

## 로그인 설정 (API)

로컬에서는 `server/src/WorkBoard.Api/appsettings.Local.json`에 넣습니다. 이 파일은 `.gitignore`에 있어 커밋되지 않고, Development 환경에서만 읽습니다. 양식은 `appsettings.Local.example.json`을 복사해 씁니다. 배포 환경에서는 같은 이름의 환경 변수(`Auth__Supabase__Url` 등)로 넣습니다. 개발 환경에서는 이 파일 값이 환경 변수보다 우선하고, 빌드·배포 결과물에는 복사되지 않습니다.

| 이름 | 내용 |
| --- | --- |
| `Auth__Supabase__Url` | Supabase 프로젝트 주소. 발급자는 `{Url}/auth/v1`, 대상은 `authenticated` |
| `Auth__Supabase__ServiceRoleKey` | 서비스 롤 키. 사용자의 GitHub identity 조회(Admin API)에만 쓰고 브라우저에 내보내지 않음 |
| `Auth__Supabase__JwtSecret` | 선택. 공유 키(HS256) 프로젝트일 때만. 비우면 `{Url}/auth/v1/.well-known/jwks.json`의 공개 키로 검증 |

Development가 아닌 환경에서 `Url`이나 `ServiceRoleKey`가 없으면 API가 시작되지 않습니다. Development에서는 없어도 시작하고, 이때는 개발용 헤더 경로만 동작합니다.

```bash
cd server/src/WorkBoard.Api
cp appsettings.Local.example.json appsettings.Local.json   # 값을 채운 뒤 API 재시작
```

웹은 빌드 시점 공개 값 `NEXT_PUBLIC_SUPABASE_URL`, `NEXT_PUBLIC_SUPABASE_ANON_KEY`를 씁니다.

## GitHub App 설정 (API)

저장소 연결(`POST /api/connections/github`)에 씁니다. 같은 `appsettings.Local.json`의 `GitHub:App` 항목이고, 배포 환경에서는 `GitHub__App__*` 환경 변수입니다. 비어 있으면 그 엔드포인트만 `503 github_app_not_configured`를 돌려주고 나머지는 동작합니다.

| 이름 | 내용 |
| --- | --- |
| `GitHub__App__AppId` | App ID(숫자). App JWT의 발급자 |
| `GitHub__App__ClientId` | App의 Client ID |
| `GitHub__App__ClientSecret` | App의 Client secret. 설치 후 받은 `code`를 사용자 토큰으로 바꿀 때만 사용 |
| `GitHub__App__PrivateKeyPath` | 내려받은 `.pem` 경로. 절대 경로 또는 API 폴더 기준 상대 경로. `*.pem`은 `.gitignore`에 있음 |
| `GitHub__App__Slug` | App 주소의 이름. **필수**: API가 `<Slug>[bot]`으로 App이 쓴 댓글·본문을 알아보고(작성자 표시), 중단된 요청을 다시 보낼 때 이미 만든 이슈·댓글을 찾는 데 씀. 비우면 작성자가 봇으로 보이고, 이어받는 재시도는 504로 멈춤. 화면의 설치 링크에도 씀 |

토큰 연결의 저장 토큰을 암호화하는 키는 `GitHub__Credentials__KeyFile`(키 파일 경로, 모드 600, 저장소 밖)로 넣습니다. 개발용 키 파일 만드는 법과 교체 절차는 [docs/auth-and-github.md](docs/auth-and-github.md)의 "연결별 GitHub 자격 증명"에 있습니다. `*.keyring.json`, `credential-keys*.json`은 `.gitignore`에 있습니다. 토큰으로 조직을 연결하는 법과 테스트 조직용 fine-grained 토큰 만드는 법은 같은 문서의 "토큰으로 조직 연결"에 있습니다.

### 웹훅 (실시간 반영)

`GitHub__App__WebhookSecret`이 있어야 웹훅이 켜집니다. 비어 있거나 `Sync:Enabled=false`이면 `POST /api/github/webhook`은 503이고 아무것도 저장하지 않습니다. 주기 동기화는 기본 10분이고, 최근 30분 안에 검증된 전달이 있을 때만 60분 간격의 보정 작업이 됩니다. 전달이 끊기면 다시 10분으로 돌아갑니다(`Sync:IntervalMinutes`로 직접 지정하면 그 값만 씁니다).

1. 임의의 긴 문자열을 만들어 `GitHub:App:WebhookSecret`(환경 변수 `GitHub__App__WebhookSecret`)에 넣고 API를 다시 시작합니다. 예: `openssl rand -hex 32`
2. 로컬에서 받으려면 터널로 API(`localhost:5080`)를 공개합니다(예: `cloudflared tunnel --url http://localhost:5080`).
3. GitHub → Settings → Developer settings → GitHub Apps → App → General → Webhook: **Active**를 켜고, Webhook URL에 `https://<터널 주소>/api/github/webhook`, Secret에 1번의 값을 넣고 저장합니다.
4. Permissions & events의 Subscribe to events에서 Issues, Issue comment, Pull request, Pull request review, Pull request review comment, Milestone, Label, Sub issues, Organization(조직 구성원 읽기 권한 필요)을 선택합니다. `installation` 이벤트는 항상 전달됩니다.
5. Advanced 탭의 Recent Deliveries에서 `ping`이 200인지 확인합니다. 서명이 틀리면 401입니다.

자세한 동작은 [docs/auth-and-github.md](docs/auth-and-github.md)와 `docs/api-contract.md`의 "GitHub webhook"에 있습니다.

### 조직 연결

조직 연결은 설치 토큰으로 조직 구성원을 확인하므로 App에 조직 권한이 하나 더 필요합니다.

1. GitHub → Settings → Developer settings → GitHub Apps → App → **Permissions & events** → Organization permissions → **Members**를 **Read-only**로 바꾸고 저장합니다.
2. 이미 설치한 조직이 있으면 그 조직 소유자가 Installed GitHub Apps 화면에서 **Review request** → **Accept new permissions**로 승인합니다. 승인 전에는 조직 연결이 `422 org_members_not_granted`입니다.
3. General → **Request user authorization (OAuth) during installation**이 켜져 있어야 합니다(개인 연결과 같음). Setup URL은 웹의 `/connect/github`입니다.

개인 테스트 조직으로 확인하는 방법(실제 회사 조직에는 설치하지 않습니다):

1. 개인 계정으로 GitHub에서 무료 조직을 하나 만듭니다(New organization → Free). 만든 사람이 소유자입니다.
2. 소유자가 직접 연결: 업무 보드 설정 → "GitHub 연결하기" → 설치 화면에서 그 조직 선택 → 돌아오면 "연결되었습니다"와 조직 구성원 수가 보입니다.
3. 승인 대기 후 연결: 두 번째 GitHub 계정을 그 조직의 일반 구성원(Member)으로 초대하고, 그 계정으로 업무 보드에 회사를 만들어 "GitHub 연결하기"로 같은 조직에 설치를 요청합니다. 돌아오면 "관리자에게 승인을 요청했습니다"가 보입니다. 소유자 계정으로 조직 Settings → GitHub Apps의 요청을 승인합니다. "다시 확인"으로 연결할 수 있는 사람은 **조직 소유자이면서 이 회사의 관리자**뿐이므로, 소유자 계정을 회사에 초대해 합류시킨 뒤, 관리자가 설정 → 구성원에서 그 사람을 "관리자로 지정"합니다. 그다음 소유자 계정으로 설정에서 "다시 확인"을 누르면 연결됩니다. 두 번째 계정이 누르면 "조직 소유자만 "다시 확인"으로 연결할 수 있습니다"가 나옵니다. 요청은 7일 뒤 만료됩니다. 같은 조직은 한 회사에만 연결되고 이미 설치된 조직에는 요청이 생기지 않으므로, 2번과 3번은 서로 다른 테스트 조직으로 시험합니다.

샘플 사용자: `haneul-dev`(acme-health 관리자), `other-org-dev`(globex-labs). 두 번째 사용자로 바꾸면 첫 번째 회사의 데이터가 하나도 보이지 않아야 합니다.

## DB 마이그레이션

스키마는 `db/migrations/NNNN_이름.sql`에 번호 순서로 쌓입니다. 파일 하나가 트랜잭션 하나이고, 적용 기록은 `schema_migrations` 테이블에 남습니다. 이미 적용한 파일은 고치지 않고(내용이 바뀌면 거부됩니다) 새 번호 파일을 추가합니다. 실패하면 그 파일만 롤백하고 이유를 출력한 채 종료합니다.

- 적용에는 스키마 소유자 연결(`ConnectionStrings:Migrate`)을 씁니다. 로컬 기본값은 `appsettings.Development.json`의 `postgres` 계정입니다. `workboard_app`/`workboard_sync`로는 적용할 수 없습니다.
- Development의 `dotnet run`은 시작할 때 자동으로 적용합니다. 그 밖의 환경은 배포 때 직접 실행합니다.

```bash
cd server/src/WorkBoard.Api
dotnet run -- migrate        # 적용하고 종료. 환경 변수: ConnectionStrings__Migrate=...
```

- 새 DB: 비어 있는 DB에 적용하면 0001부터 전부 적용하고, 개발 환경에서는 샘플 데이터도 넣습니다.
- 기존 DB 업그레이드(init 스크립트로 만든 로컬 DB): 데이터는 그대로 두고 `dotnet run -- migrate`만 실행합니다. 기록이 없는 DB에 0001 테이블이 이미 있으면 0001은 실행하지 않고 "적용됨"으로만 기록하고, 0002부터 적용합니다.
- 처음 상태로 되돌리기: `docker compose down -v && docker compose up -d` 뒤에 API를 한 번 실행합니다(마이그레이션과 샘플 데이터).
