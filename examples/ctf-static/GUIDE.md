# 题目编写指南

这份 `GUIDE.md` 随每个新题目一起生成，留在题目目录中供出题人与 Reviewer 使用。请按下面的清单替换脚手架内容；不要把示例 Dockerfile 或占位 Flag 当成可发布题目。

## 先确认两个配置文件

- 本目录的 `challenge.yml` 是可复用的 Challenge 模板：题目名称、题面、附件、静态 Flag，以及 Runtime / Checker 定义写在这里。保持脚手架生成的 `id` 不变。
- 仓库根目录的 `competition.yml` 是这场比赛里的题目实例：`challenge` 路径、`order`、`published`、提示和模式专属 `rules` 写在那里。保持该条目的 `id` 不变；不要把分数或提示放回 `challenge.yml`。
- 两处 `mode` 必须和比赛模式一致。`definition` 与 `rules` 分别只能有一个对应的模式分支；Manifest 使用 `gitops.noctf.dev/v2`，没有 `schemaVersion`、`definitionJson` 或 `rulesJson`。

## 编写题面与选手附件

1. 将 `statement.md` 改成选手会看到的完整题面，写清目标、访问方式和允许的交互；不要在题面中泄露 Flag 或解法。
2. 把需要下载的文件放进 `attachments/`，并逐个登记到 `challenge.yml`。路径相对本题目录，不能指向 `solution/`、`runtime/` 或目录外，也不要使用符号链接。例如：

   ```yaml
   attachments:
     - id: 11111111-1111-4111-8111-111111111111 # 实际使用新生成的 UUID
       path: attachments/handout.zip
       contentType: application/zip
   ```

3. 附件 ID 对应不可变的文件内容、文件名和 Content-Type。修改任何一项时，生成新 UUID 并替换旧条目，不能沿用原 ID。`solution/` 放内部解法，`tests/` 放验证脚本；这两个目录不会作为选手附件上传。

## 选择 Flag 与运行方式

- 无 Runtime 的静态 CTF 题可在 `challenge.yml` 中填 `flags`。例如：

  ```yaml
  flags:
    - id: 22222222-2222-4222-8222-222222222222 # 实际使用新生成的 UUID
      value: flag{replace_with_real_value}
      matchKind: Exact
  ```

  Flag 以明文进入 Git 历史；仅在访问受控的私有比赛仓库中使用，勿把真实 Flag 提交到公开仓库。
- 只要 `definition.runtime` 存在，就保持模板层 `flags: []`。动态 CTF/AWDP Flag、AWD 轮换 Flag 与 KoH Control Flag 由 NoCTF 生成。按照脚手架中的 `flagSource`、注入命令、环境变量和控制检查配置，让服务真正接收并使用这些值。
- `runtime/` 放服务代码、Dockerfile 和 Compose 文件；`checker/` 放 AWD/AWDP 的 Checker。脚手架中的 BusyBox 文件只是占位，请替换成可运行、可检查的实现。`build.images[].context` 与 `dockerfile` 都相对本题目录，`image.build` 引用对应的 build key。使用外部镜像时必须指定 `registry/image@sha256:...`，不能使用浮动 tag。
- Container 的公开端口映射必须使用 `hostPort: 0`，由 Docker 分配宿主机端口；`urlBindings` 指向容器端口。Compose 要维护 `runtime/compose.yml` 与 `serviceImages` 的服务名对应关系。不要在 Manifest 中配置 Runtime provider、Runner pool、Ingress 或 Checker 的目标 URL/端口。
- AWD 要实现 `definition.awd.flagInjection` 和 Checker；AWDP 要实现 Checker 及补丁入口/命令，Break 来自正确 Flag 提交，Fix 是独立的归档补丁提交；KoH 需要共享 Runtime 与 `isControlCheck: true` 的控制检查 URL 绑定，不使用 Checker job。

## 配置本场比赛并验证

1. 在根目录 `competition.yml` 找到 `challenge: <direction>/<slug>` 的条目，设置顺序、模式专属评分 `rules` 和提示。提示的 `id` 使用新 UUID，`content` 非空、`cost` 非负；`publishedAt: null` 表示尚未定时发布。
2. 准备好之前保持 `published: false`。需要向选手开放时，再明确改成 `true` 并走 PR 审核。
3. 在仓库根目录运行：

   ```bash
   dotnet build .github/scripts/repository.cs
   dotnet run --file .github/scripts/repository.cs -- validate
   dotnet run --file .github/scripts/repository.cs -- self-test
   ```

4. 为题目编写并运行 `tests/` 中的实际测试：至少检查解法可达、正确/错误 Flag、重启或重置后的行为；运行题还要检查镜像、端口、Flag 注入和 Checker。脚本的 `validate` 只检查仓库契约，不代替题目功能测试。
5. 提交到本题 Draft PR，等待 CI 与 Reviewer。PR 不会部署；合并到 `main` 后才由仓库工作流构建并 Apply。确认题面、附件、Flag 和发布状态后再合并。
