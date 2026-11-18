# 배포 점검표

대상 구성: 웹은 Cloudflare Pages(정적), API는 소유자 PC에서 Production 모드로 실행하고 Cloudflare Tunnel로 공개, DB는 Supabase Postgres입니다. 아래 도메인, 키, 비밀번호는 모두 자리표시자입니다.

## 순서

1. DB 역할 만들기 (Supabase SQL Editor)
2. API 환경 변수 준비
3. 마이그레이션과 역할 비밀번호 적용 (`dotnet run -- migrate`)
4. API를 서비스로 실행
5. Cloudflare Tunnel
6. Cloudflare Pages
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
| `ASPNETCORE_URLS` | `http://127.0.0.1:5080` (터널만 접근하도록 loopback) |
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

Supabase 연결 방식: 직접 연결(`db.<project-ref>.supabase.co:5432`)은 IPv6만 지원하므로 IPv6가 없는 PC에서는 Session pooler(`aws-0-<region>.pooler.supabase.com:5432`, 사용자 이름 `<role>.<project-ref>`)를 씁니다. Transaction pooler(6543)는 쓰지 않습니다. 마이그레이션이 세션 단위 advisory lock을 쓰고 API가 트랜잭션 안에서 `set_config`를 씁니다.

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

Production에서 API는 시작할 때 자동으로 마이그레이션하지 않습니다. 새 버전을 배포할 때마다 이 명령을 먼저 실행합니다.

## 4. 기동 점검과 서비스

Development가 아닌 환경에서 `ASPNETCORE_URLS` 등으로 loopback 밖에 바인딩하면 경고만 남깁니다. API는 시작하면서 다음을 검사하고, 하나라도 어긋나면 이유를 출력하고 시작하지 않습니다.

- `ConnectionStrings:App`/`Sync`가 비밀번호 없음, 기본값(`workboard_app`, `workboard_sync`, `postgres`), 사용자 이름과 같은 비밀번호
- `Web:BaseUrl`, `Web:AllowedOrigins` 누락 또는 https가 아님(loopback 제외)
- `Auth:Supabase:Url`, `ServiceRoleKey` 누락
- GitHub App이 일부만 설정됨, 개인 키 또는 자격 증명 키 파일이 없음

경고만 하는 항목: App 미설정, `WebhookSecret` 없음, `KeyFile` 없음.

반대로 Development로 실행 중인 API는 `localhost` 밖에 바인딩하면 시작하지 않고, Cloudflare를 거친 요청(`CF-Connecting-IP`, `CF-Ray` 헤더)에는 `X-Dev-User` 로그인을 받지 않습니다. 운영 PC에서 `ASPNETCORE_ENVIRONMENT`를 `Development`로 두지 마세요.

systemd 예시(WSL 또는 Linux). Windows 네이티브면 NSSM이나 작업 스케줄러로 같은 환경 변수를 주고 시작 시 실행합니다.

```ini
# /etc/systemd/system/work-board-api.service
[Unit]
Description=work-board API
After=network-online.target

[Service]
WorkingDirectory=/opt/work-board/api
ExecStart=/usr/bin/dotnet /opt/work-board/api/WorkBoard.Api.dll
EnvironmentFile=/etc/work-board/api.env
Restart=always
User=workboard

[Install]
WantedBy=multi-user.target
```

`/etc/work-board/api.env`는 `KEY=value` 줄로 위 표의 값을 넣고 `chmod 600` 합니다. 게시: `dotnet publish -c Release -o /opt/work-board/api`.

## 5. Cloudflare Tunnel

```bash
cloudflared tunnel login
cloudflared tunnel create work-board-api
cloudflared tunnel route dns work-board-api api.example.com
```

```yaml
# ~/.cloudflared/config.yml
tunnel: <tunnel-uuid>
credentials-file: /home/<user>/.cloudflared/<tunnel-uuid>.json
ingress:
  - hostname: api.example.com
    service: http://127.0.0.1:5080
  - service: http_status:404
```

`cloudflared service install`로 서비스로 등록합니다. PC가 꺼지거나 잠자기에 들어가면 API가 내려가므로 전원 설정을 확인합니다.

### 도메인이 없을 때: quick tunnel + workers.dev Worker

named tunnel은 Cloudflare에 연결된 도메인이 있어야 합니다. 도메인이 없으면 quick tunnel(`<무작위>.trycloudflare.com`)을 쓰는데, 이 주소는 켤 때마다 바뀝니다. 그래서 고정 주소인 workers.dev에 Worker(`deploy/api-proxy`)를 두고 Worker가 현재 터널로 요청을 넘깁니다. 웹의 `NEXT_PUBLIC_API_URL`과 GitHub 웹훅은 Worker 주소를 씁니다. CORS 허용 출처(`Web__AllowedOrigins__0`)는 그대로 Pages 주소입니다.

1. 한 번만: `npx wrangler@4 login`, 그다음 `npx wrangler@4 deploy --config deploy/api-proxy/wrangler.toml`. 출력된 `https://maru-api.<계정>.workers.dev`가 API 주소입니다. 아래 6, 7, 8장의 `https://api.example.com` 자리에 이 주소를 씁니다.
2. API를 켠 뒤 `scripts/api-tunnel.sh`를 실행합니다. quick tunnel을 열고 새 주소를 Worker의 `UPSTREAM` 값에 넣습니다. 터널이 끝날 때까지 실행 상태로 둡니다(systemd 사용자 서비스로 등록 가능). Cloudflare 계정이 여러 개면 `CLOUDFLARE_ACCOUNT_ID`를 지정해야 비대화형 `wrangler secret put`이 실패하지 않습니다.
3. 터널이 꺼져 있으면 Worker는 `503 {"error":"api_offline"}`을 돌려줍니다. Worker는 `/api/` 경로만 넘깁니다.

quick tunnel은 Cloudflare가 시험용으로 안내하는 기능이라 가동 보장이 없고 동시 요청 수에 제한이 있습니다. 소수 인원 사용을 넘으면 도메인을 연결해 named tunnel로 옮깁니다.

## 6. Cloudflare Pages

저장소를 연결하고 설정합니다. 웹은 정적 내보내기(`output: "export"`)입니다.

| 항목 | 값 |
| --- | --- |
| Root directory | `web` |
| Build command | `pnpm install --frozen-lockfile && pnpm build` |
| Build output directory | `out` |
| Node 버전 | 24 (`NODE_VERSION=24`) |

환경 변수(빌드 때 값이 코드에 박히므로 바꾸면 다시 빌드):

| 이름 | 값 |
| --- | --- |
| `NEXT_PUBLIC_API_URL` | `https://api.example.com` |
| `NEXT_PUBLIC_SUPABASE_URL` | `https://<project-ref>.supabase.co` |
| `NEXT_PUBLIC_SUPABASE_ANON_KEY` | Supabase anon 키 |
| `NEXT_PUBLIC_GITHUB_APP_SLUG` | GitHub App slug |

`NEXT_PUBLIC_*` 값은 브라우저에 공개되므로 service role 키를 넣지 않습니다. Supabase Dashboard → Authentication → URL Configuration의 Site URL과 Redirect URLs에 웹 주소(`https://board.example.com`)를 추가하고, GitHub 로그인 provider의 callback은 Supabase가 보여 주는 값을 GitHub OAuth App에 넣습니다.

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
- Supabase의 `postgres`는 슈퍼유저가 아니고 `createrole`과 `rolbypassrls`를 가집니다. PostgreSQL 16부터는 `createrole` 역할이 자신이 가진 속성(`bypassrls`)을 다른 역할에 줄 수 있으므로 1단계의 `create role ... bypassrls`가 됩니다. 이 저장소의 코드로 Supabase에서 실행해 본 것은 아니므로 1단계의 마지막 `select`가 최종 확인입니다. 되면 추가 작업이 없습니다.
- 안 되면 대안: `workboard_sync`에 우회 대신 모든 테넌트 테이블에 `create policy sync_all on <table> to workboard_sync using (true) with check (true)`를 거는 마이그레이션을 새 번호로 추가합니다(테이블 목록은 `FORCE ROW LEVEL SECURITY`가 켜진 테이블 전부). 이 경우 `RoleSetup.CheckSyncRoleAsync`의 검사도 함께 바꿔야 합니다. 현재 코드는 이 대안을 구현하지 않았고, `bypassrls`가 없으면 Production 마이그레이션이 실패합니다. RLS를 조용히 끄는 경로는 없습니다.

## 마이그레이션 역할과 RLS (0015 backfill)

`0015_issue_event_seq.sql` 같은 데이터 마이그레이션은 `force row level security`가 켜진 테이블을 갱신합니다. 이 테이블은 소유자에게도 정책이 적용되므로 마이그레이션 역할이 RLS를 우회하지 못하면 `app.tenant_id`가 없어 0행이 갱신됩니다. 0015는 그 뒤 `seq`를 `not null`로 바꾸므로 기존 행이 있으면 실패로 드러나고(조용히 틀리지 않음), 새 Supabase DB는 행이 없어 영향이 없습니다. Supabase의 `postgres`가 우회하는지는 `select rolsuper or rolbypassrls from pg_roles where rolname = current_user`로 확인합니다. 우회하지 못하면 `migrate`가 경고를 남깁니다. 앞으로 기존 데이터를 갱신하는 마이그레이션을 추가할 때는 이 경고가 없는 환경인지 확인하고, 정책에 의존하지 않도록 `alter table ... no force row level security`를 앞뒤로 두거나 정책을 명시합니다.
