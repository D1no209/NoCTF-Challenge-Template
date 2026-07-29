# NoCTF Competition Repository

一场比赛对应一个由本模板创建的 GitHub 仓库。题目通过 Issue 创建分支和 Draft PR；合并到 `main` 后，仓库 Action 构建镜像并使用普通 NoCTF Bot JWT 调用管理 API。

## 初始化

1. 使用 GitHub 的 **Use this template** 创建比赛仓库，不要 fork。
2. 在 NoCTF 创建比赛，修改 `competition.yml` 的 `competitionId` 和 `mode`。
3. 删除或替换 `examples/ctf-static` 示例；Challenge 与 Competition 必须使用相同 Mode。
4. 由平台 Administrator 创建 `Organizer` Bot：

   ```http
   POST /api/v1/admin/platform/bots

   {
     "userName": "summer-ctf-gitops",
     "role": "Organizer"
   }
   ```

5. 把 Bot 加入比赛的 `ManagerIds`。Bot 创建的新 Challenge 会由该 Bot 自动成为 Owner；接管已有 Challenge 时，需要原 Owner 把 Bot 加入 Challenge `ManagerIds`。
6. 为 Bot 签发所需时长的普通 Access JWT：

   ```http
   POST /api/v1/admin/platform/bots/{botUserId}/tokens

   {
     "expiresInSeconds": 31536000
   }
   ```

7. 把 JWT 保存为 Repository Secret `NOCTF_BOT_TOKEN`。撤销时在 NoCTF 递增该 Bot 的 TokenVersion。

Bot 的不可用 `.invalid` Email 和随机 dummy PasswordHash 由服务端生成；禁止密码登录是
`UserKind.Bot` 的认证规则，不依赖 dummy password 保密。Bot 不能 Refresh，也没有 OIDC
或仓库专用密钥。

## Repository 配置

必须配置：

- Variable `NOCTF_API_URL`：NoCTF API 根地址。
- Secret `NOCTF_BOT_TOKEN`。

镜像默认推送 GHCR，并使用 GHCR digest。可选配置：

- `NOCTF_IMAGE_REGISTRY=custom`
- `CUSTOM_REGISTRY_HOST`
- `CUSTOM_REGISTRY_NAMESPACE`
- Secrets `CUSTOM_REGISTRY_USERNAME`、`CUSTOM_REGISTRY_PASSWORD`

只要配置自定义 Registry，main 构建就会同时推送 GHCR 和自定义 Registry；任一失败都不会 Apply。Registry pull credential由部署好的 NoCTF 平台配置，不进入题目 Manifest。

## 创建题目

从 **Create challenge** Issue Form 创建 Issue。Action 会：

- 规范化 direction 和 slug；
- 创建 `<direction>/<slug>` 分支和目录；
- 生成 Challenge 与 CompetitionChallenge UUID；
- 更新 `competition.yml`；
- 创建带有 `Closes #n` 的 Draft PR。

题目根目录包含：

```text
<direction>/<slug>/
├─ challenge.yml
├─ statement.md
├─ attachments/
├─ runtime/
├─ checker/
├─ tests/
└─ solution/
```

`solution/` 不上传 NoCTF。静态 Flag直接明文写在 `challenge.yml`。动态 Runtime Flag、AWD 轮换 Flag和 KoH Control Flag由平台生成。

## 本地命令

`.github/scripts/repository.cs` 是很小的 file-based app 入口，通过 `#:include` 组合
同目录按职责拆分的 `RepositoryApp.*.cs` 与 `NoCtfClient.cs`；工具不增加 `.csproj`。

```bash
dotnet build .github/scripts/repository.cs
dotnet run --file .github/scripts/repository.cs -- validate
dotnet run --file .github/scripts/repository.cs -- self-test
dotnet run --file .github/scripts/repository.cs -- discover --base origin/main --head HEAD
dotnet run --file .github/scripts/repository.cs -- plan --base origin/main --head HEAD
dotnet run --file .github/scripts/repository.cs -- build --challenge web/sql-notes --key runtime
```

本地 `build` 不推送 Registry，也不调用 NoCTF。

## Manifest 边界

- `challenge.yml` 管理 Challenge metadata、题面、附件、静态 Flag、Runtime、Checker和动态 Flag注入。
- `competition.yml` 管理 CompetitionChallenge ID、顺序、BaseScore、发布状态、Hints和 Rules。
- Manifest 禁止 provider、runnerPool、hostPort、namespace、Ingress和 Checker target URL/port。
- Attachment 内容不可原位替换；内容变化时必须生成新的 Attachment ID。
- 外部镜像必须使用 `registry/image@sha256:...`。
- 仓库构建镜像通过 source hash tag解析 digest，digest 不写回 Git。

## 分支保护

建议保护 `main`：

- 禁止直接 push和 force push；
- 要求 `Challenge CI / validate`；
- 新 commit撤销旧批准；
- 必须解决 review conversation；
- `.github/**`、`competition.yml`、Runtime和 Checker分别配置 CODEOWNERS；
- 合并后自动删除题目分支。

PR 不读取 NoCTF 或 Registry Secret。只有 main 的最终 Apply job可以读取 Bot JWT。
