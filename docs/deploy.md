# 배포 점검표

대상 구성: 웹은 Cloudflare Pages(정적), API는 Render 웹 서비스(Docker, `render.yaml`), DB는 Supabase Postgres입니다. `main`에 병합되면 `.github/workflows/deploy.yml`이 마이그레이션, API 배포, 웹 배포를 차례로 실행합니다. 아래 도메인, 키, 비밀번호는 자리표시자입니다.

## 순서

1. DB 역할 만들기 (Supabase SQL Editor)
2. API 환경 변수 준비
3. 마이그레이션과 역할 비밀번호 (처음 한 번은 로컬, 이후는 배포 워크플로)
4. Render 서비스
5. 배포 워크플로와 잠들지 않게 하기
6. Cloudflare Pages와 로그인 설정
7. GitHub App 주소 설정
8. 확인

## 1. DB 역할 (Supabase)

`0001_baseline.sql`은 `workboard_app`, `workboard_sync` 역할이 없을 때만 만듭니다. 이때 `workboard_sync`에 `bypassrls`를 줍니다. Supabase의 `postgres`는 슈퍼유저는 아니지만 자신이 `rolbypassrls`를 가지고 있어, PostgreSQL 16 이상에서는 가진 속성을 `createrole` 권한으로 다른 역할에 줄 수 있습니다(아래 "Supabase에서 BYPASSRLS"). 다만 0001이 실패하면 전체가 롤백되므로 첫 마이그레이션 전에 SQL Editor에서 역할을 먼저 만듭니다. 비밀번호는 임시값이어도 되고, 3단계에서 바뀝니다.

```sql
create role workboard_app  login password '<임시 비밀번호 1>';
create role workboard_sync login password '<임시 비밀번호 2>' bypassrls;
select rolname, rolbypassrls from pg_roles where rolname like 'workboard_%';
```

위 마지막 `select`에서 `workboard_sync`의 `rolbypassrls`가 `t`여야 합니다. "must be superuser" 오류가 나거나 `f`이면 `bypassrls` 없이 진행하지 말고 아래 대안 절을 먼저 읽습니다. 이 상태로 진행하면 동기화와 초대 합류가 행을 하나도 보지 못합니다. API는 마이그레이션 때 이 속성을 확인하고, Production에서는 없으면 실패합니다.

Supabase의 Data API(PostgREST)는 `public` 스키마 테이블을 `anon`/`authenticated` 역할로 노출합니다. 이 앱은 Data API를 쓰지 않으므로 Dashboard → Project Settings → Data API에서 끄거나, 최소한 다음을 실행합니다.

```sql
revoke all on all tables    in schema public from anon, authenticated;
revoke all on all sequences in schema public from anon, authenticated;
revoke all on all functions in schema public from anon, authenticated;
alter default privileges in schema public revoke all on tables    from anon, authenticated;
alter default privileges in schema public revoke all on sequences from anon, authenticated;
alter default privileges in schema public revoke all on functions from anon, authenticated;
```

## 2. API 환경 변수

| 이름 | 내용 |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ASPNETCORE_HTTP_PORTS` | 이미지 기본값 `10000`. Render가 이 포트로 보냄 |
| `ConnectionStrings__App` | `Host=<host>;Port=<port>;Database=postgres;Username=workboard_app.<project-ref>;Password=<앱 비밀번호>;SSL Mode=Require;Trust Server Certificate=false` |
| `ConnectionStrings__Sync` | 위와 같고 `Username=workboard_sync.<project-ref>`, 동기화 비밀번호 |
| `ConnectionStrings__Migrate` | 위와 같고 `Username=postgres.<project-ref>`, Supabase `postgres` 비밀번호. 마이그레이션 때만 필요하므로 서비스 환경에는 넣지 않습니다 |
| `Database__AppPassword`, `Database__SyncPassword` | 3단계에서 역할에 설정할 비밀번호. 각 16자 이상, 서로 달라야 하고 기본값이면 거부. `openssl rand -base64 24` 권장. 마이그레이션 때만 필요 |
| `Auth__Supabase__Url`, `Auth__Supabase__ServiceRoleKey` | Supabase 프로젝트 URL과 service role 키 |
| `Web__BaseUrl` | 웹의 공개 주소. 예: `https://board.example.com` (필수, https) |
| `Web__AllowedOrigins__0` | CORS 허용 출처. 웹의 출처와 같은 값 (필수, https). 여러 개면 `__1`, `__2` |
| `GitHub__App__AppId`, `ClientId`, `ClientSecret`, `PrivateKeyPath`, `Slug`, `WebhookSecret` | GitHub App 값. 하나라도 넣으면 `WebhookSecret` 외에는 모두 필요. 설명은 README "GitHub App 설정" |
| `GitHub__Credentials__KeyFile` | 토큰 암호화 키 파일 절대 경로(모드 600, 저장소 밖). 없으면 토큰 연결 저장 불가(경고) |
| `Sync__Enabled` | 기본 `true` |
| `Maintenance__IdempotencyRetentionHours` | 만료된 멱등성 키 보관 시간. 기본 48, 최소 24 |
| `Maintenance__CleanupIntervalMinutes` | 정리 주기. 기본 60 |

Supabase 연결 방식: 직접 연결(`db.<project-ref>.supabase.co:5432`)은 IPv6만 지원하므로 Render와 IPv6가 없는 PC에서는 Session pooler(`aws-0-<region>.pooler.supabase.com:5432`, 사용자 이름 `<role>.<project-ref>`)를 씁니다. Transaction pooler(6543)는 쓰지 않습니다. 마이그레이션이 세션 단위 advisory lock을 쓰고 API가 트랜잭션 안에서 `set_config`를 씁니다.

`GitHub:Credentials:KeyFile` 키 파일은 README의 "연결별 GitHub 자격 증명" 절차로 만들고 `chmod 600` 합니다. 백업 없이 잃으면 저장된 토큰을 복호화할 수 없습니다.

## 3. 마이그레이션과 역할 비밀번호

```bash
cd server/src/WorkBoard.Api
dotnet run --no-launch-profile -- migrate
```

`--no-launch-profile`이 없으면 launchSettings가 Development로 바꿉니다. 게시한 실행 파일이면 `WorkBoard.Api migrate`입니다. Production(Development가 아닌 환경)에서 이 명령은 다음을 합니다.

- `Database__AppPassword`/`Database__SyncPassword`가 없거나 짧거나 기본값이면 아무것도 적용하지 않고 실패
- 대기 중인 마이그레이션 적용
- `alter role workboard_app/workboard_sync password ...`로 두 역할 비밀번호 설정
- `workboard_sync`가 RLS를 우회하지 못하면 실패

비밀번호를 바꾸려면 값을 바꿔 이 명령을 다시 실행하고 API 서비스의 연결 문자열도 갱신합니다. 마이그레이션 파일에는 비밀번호가 없고, 이미 적용된 `0001`의 `workboard_app`/`workboard_sync` 임시값은 이 단계에서 반드시 바뀝니다. `alter role` 문장에는 평문 비밀번호 대신 SCRAM-SHA-256 검증값만 실리므로 서버 로그와 `pg_stat_statements`에 비밀번호가 남지 않습니다.

Production에서 API는 시작할 때 자동으로 마이그레이션하지 않습니다. 배포 워크플로가 API 배포 전에 이 명령을 실행합니다(5장). 로컬에서 직접 실행할 때는 값이 담긴 파일을 `scripts/with-env.py <파일> dotnet run --no-launch-profile -- migrate`로 넘깁니다. 연결 문자열에 `;`와 공백이 있어 셸에서 `source`하면 깨집니다. Supabase가 보여 주는 `postgresql://...` URI 형식은 Npgsql이 받지 않으므로 `Host=...;Port=...;Username=...` 형식으로 옮겨 적습니다.

## 4. 기동 점검과 Render 서비스

API는 시작하면서 다음을 검사하고, 하나라도 어긋나면 이유를 출력하고 시작하지 않습니다.

- `ConnectionStrings:App`/`Sync`가 비밀번호 없음, 기본값(`workboard_app`, `workboard_sync`, `postgres`), 사용자 이름과 같은 비밀번호
- `Web:BaseUrl`, `Web:AllowedOrigins` 누락 또는 https가 아님(loopback 제외)
- `Auth:Supabase:Url`, `ServiceRoleKey` 누락
- GitHub App이 일부만 설정됨, 개인 키 또는 자격 증명 키 파일이 없음

경고만 하는 항목: loopback 밖 바인딩(Render는 `0.0.0.0:10000`), App 미설정, `WebhookSecret` 없음, `KeyFile` 없음.

Development로 실행 중인 API는 `localhost` 밖에 바인딩하면 시작하지 않고, Cloudflare를 거친 요청(`CF-Connecting-IP`, `CF-Ray` 헤더)에는 `X-Dev-User` 로그인을 받지 않습니다. 운영에서 `ASPNETCORE_ENVIRONMENT`를 `Development`로 두지 마세요.

이미지는 `deploy/api/Dockerfile`(저장소 루트에서 빌드, 마이그레이션 SQL은 어셈블리에 포함)입니다. Render 서비스는 `render.yaml`로 만듭니다.

1. Render Dashboard → New → Blueprint에서 이 저장소를 연결하면 `render.yaml`의 `maru-api`가 만들어집니다(무료, Singapore, `main`, 자동 배포 끔).
2. `sync: false` 값(2장의 연결 문자열 App/Sync, Supabase URL과 service role 키, `Web__*`)을 입력합니다. `ConnectionStrings__Migrate`와 `Database__*`는 Render에 넣지 않습니다.
3. GitHub App을 쓰면 `GitHub__App__*` 값을 환경 변수로, 개인 키와 자격 증명 키 파일은 Secret Files(`/etc/secrets/<이름>`)로 넣고 경로를 `GitHub__App__PrivateKeyPath`, `GitHub__Credentials__KeyFile`에 줍니다.
4. Settings → Deploy Hook 주소를 복사해 5장의 `RENDER_DEPLOY_HOOK_URL`에 넣습니다.

서비스 주소는 `https://<서비스 이름>.onrender.com`(이름이 이미 있으면 뒤에 문자가 붙음)입니다. 이 주소가 API 주소입니다.

## 5. 배포 워크플로와 잠들지 않게 하기

`.github/workflows/deploy.yml`은 `main` push(또는 수동 실행) 때 다음을 합니다.

1. `migrate`: 운영 DB에 마이그레이션과 역할 비밀번호 적용
2. `api`: Render Deploy Hook 호출(마이그레이션이 끝난 뒤에만)
3. `web`: 운영 값으로 웹을 빌드하고, 결과물이 운영 Supabase와 API를 가리키는지 확인한 뒤 Pages에 올림

Deploy Hook은 배포를 Render 대기열에 넣고 바로 돌아오므로, 웹이 새 API보다 먼저 올라갈 수 있고 Render 빌드가 실패해도 워크플로는 성공으로 끝납니다. API를 바꾸는 변경은 이전 웹과도 동작하게 만들고, 배포 뒤 Render Dashboard의 Events에서 결과를 확인합니다.

저장소 Settings → Environments → `production`에 넣을 값:

| 종류 | 이름 | 값 |
| --- | --- | --- |
| secret | `MIGRATE_CONNECTION_STRING` | 2장의 `ConnectionStrings__Migrate` |
| secret | `DATABASE_APP_PASSWORD`, `DATABASE_SYNC_PASSWORD` | 2장의 두 역할 비밀번호 |
| secret | `RENDER_DEPLOY_HOOK_URL` | 4장의 Deploy Hook |
| secret | `CLOUDFLARE_API_TOKEN` | Cloudflare API 토큰(권한: Account → Cloudflare Pages → Edit) |
| secret | `CLOUDFLARE_ACCOUNT_ID` | Cloudflare 계정 ID |
| variable | `API_URL` | 4장의 Render 주소 |
| variable | `SUPABASE_URL`, `SUPABASE_ANON_KEY` | Supabase 프로젝트 URL과 anon 키(브라우저에 공개되는 값) |
| variable | `PAGES_PROJECT` | Pages 프로젝트 이름 |
| variable | `APP_SLUG` | GitHub App slug(없으면 비움). `GITHUB_`로 시작하는 이름은 GitHub가 막음 |

Render 무료 웹 서비스는 15분 동안 요청이 없으면 잠듭니다. 잠들면 GitHub 동기화가 멈추고 첫 요청과 웹훅이 깨어날 때까지 늦습니다. `deploy/keepalive`는 10분마다 `/api/health`를 부르는 Cloudflare Worker(Cron Trigger)입니다. `wrangler.toml`의 `TARGET`을 Render 주소로 맞추고 `npx wrangler@4 deploy --config deploy/keepalive/wrangler.toml`로 한 번 올립니다. 무료 요금제의 월 750시간은 서비스 하나를 한 달 내내 켜 두기에 맞는 양입니다. `wrangler deploy`가 업로드 뒤 `[code: 10007]`로 끝나도 Worker는 올라가 있을 수 있으니 Dashboard에서 확인합니다.

## 6. Cloudflare Pages

웹은 정적 내보내기(`output: "export"`)입니다. 보통은 5장의 워크플로가 올립니다. 프로젝트는 처음 한 번 `npx wrangler@4 pages project create <이름> --production-branch main --force`로 만듭니다(`--force`가 없으면 최신 wrangler가 Workers 빌드로 넘기려다 실패합니다).

로컬에서 직접 올리기: `scripts/deploy-web.sh [~/.config/maru/web.env] [maru-board]`. 아래 `NEXT_PUBLIC_*` 값을 담은 파일로 빌드하고, 결과물이 그 Supabase와 API를 가리키는지 확인한 뒤 올립니다. 로컬에서는 `web/.env.local`을 빌드 동안 잠시 치웁니다. 같은 변수를 환경 변수로 넘겨도 `.env.local` 값이 결과물에 들어가, 운영 사이트가 개발용 Supabase로 로그인한 적이 있습니다.

빌드 값(빌드 때 코드에 박히므로 바꾸면 다시 빌드):

| 이름 | 값 |
| --- | --- |
| `NEXT_PUBLIC_API_URL` | `https://api.example.com` |
| `NEXT_PUBLIC_SUPABASE_URL` | `https://<project-ref>.supabase.co` |
| `NEXT_PUBLIC_SUPABASE_ANON_KEY` | Supabase anon 키 |
| `NEXT_PUBLIC_GITHUB_APP_SLUG` | GitHub App slug |

`NEXT_PUBLIC_*` 값은 브라우저에 공개되므로 service role 키를 넣지 않습니다. anon 키와 service role 키는 Settings → API Keys의 Legacy API Keys 탭에 있습니다(API는 service role 키를 `apikey`와 `Authorization: Bearer` 양쪽에 보냅니다).

GitHub 로그인:

| 위치 | 설정 |
| --- | --- |
| GitHub → Settings → Developer settings → OAuth Apps | 새 OAuth App. Homepage URL은 웹 주소, Authorization callback URL은 `https://<project-ref>.supabase.co/auth/v1/callback` |
| Supabase → Authentication → Sign In / Providers → GitHub | Enable, OAuth App의 Client ID와 Client Secret |
| Supabase → Authentication → URL Configuration | Site URL은 웹 주소, Redirect URLs에 `<웹 주소>/**` |

Redirect URLs에 없으면 Supabase는 로그인 뒤 Site URL(기본 `http://localhost:3000`)로 보냅니다. 설정 확인: `curl -H "apikey: <anon 키>" https://<project-ref>.supabase.co/auth/v1/settings`의 `external.github`가 `true`.

## 7. GitHub App 주소

| 항목 | 값 |
| --- | --- |
| Setup URL | `https://board.example.com/connect/github` ("Request user authorization (OAuth) during installation" 켬) |
| Webhook URL | `https://api.example.com/api/github/webhook` |
| Webhook secret | `GitHub__App__WebhookSecret`과 같은 값 |

## 8. 확인

- `curl https://api.example.com/api/health`가 200
- `X-Dev-User` 헤더를 붙인 `GET /api/me`가 401
- 웹에서 GitHub 로그인, 회사 만들기, 연결, 회의록의 이슈 링크가 `Web__BaseUrl` 주소로 나오는지
- GitHub App의 Recent Deliveries에서 `ping`이 200
- DB에서 `select rolname, rolbypassrls from pg_roles where rolname like 'workboard_%'`

## Supabase에서 BYPASSRLS

- `workboard_sync`는 테넌트를 가로질러 쓰므로 RLS를 우회해야 합니다. 일반 Postgres에서는 슈퍼유저만 `bypassrls`를 줄 수 있습니다.
- Supabase의 `postgres`는 슈퍼유저가 아니고 `createrole`과 `rolbypassrls`를 가집니다. PostgreSQL 16부터는 `createrole` 역할이 자신이 가진 속성(`bypassrls`)을 다른 역할에 줄 수 있으므로 1단계의 `create role ... bypassrls`가 됩니다. 2026-11-18 Supabase(PostgreSQL 17)에서 1단계 문장으로 `workboard_sync`의 `rolbypassrls`가 `true`가 되고, `migrate`의 우회 검사도 통과하는 것을 확인했습니다. 다른 프로젝트에서도 1단계의 마지막 `select`로 확인합니다.
- 안 되면 대안: `workboard_sync`에 우회 대신 모든 테넌트 테이블에 `create policy sync_all on <table> to workboard_sync using (true) with check (true)`를 거는 마이그레이션을 새 번호로 추가합니다(테이블 목록은 `FORCE ROW LEVEL SECURITY`가 켜진 테이블 전부). 이 경우 `RoleSetup.CheckSyncRoleAsync`의 검사도 함께 바꿔야 합니다. 현재 코드는 이 대안을 구현하지 않았고, `bypassrls`가 없으면 Production 마이그레이션이 실패합니다. RLS를 조용히 끄는 경로는 없습니다.

## 마이그레이션 역할과 RLS (0015 backfill)

`0015_issue_event_seq.sql` 같은 데이터 마이그레이션은 `force row level security`가 켜진 테이블을 갱신합니다. 이 테이블은 소유자에게도 정책이 적용되므로 마이그레이션 역할이 RLS를 우회하지 못하면 `app.tenant_id`가 없어 0행이 갱신됩니다. 0015는 그 뒤 `seq`를 `not null`로 바꾸므로 기존 행이 있으면 실패로 드러나고(조용히 틀리지 않음), 새 Supabase DB는 행이 없어 영향이 없습니다. Supabase의 `postgres`가 우회하는지는 `select rolsuper or rolbypassrls from pg_roles where rolname = current_user`로 확인합니다. 우회하지 못하면 `migrate`가 경고를 남깁니다. 앞으로 기존 데이터를 갱신하는 마이그레이션을 추가할 때는 이 경고가 없는 환경인지 확인하고, 정책에 의존하지 않도록 `alter table ... no force row level security`를 앞뒤로 두거나 정책을 명시합니다.
