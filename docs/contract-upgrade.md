# 当前 NoCTF 契约与安全重跑

脚本只使用平台原有的认证、资源读取和 CRUD 接口，不增加同步/校验接口。
本地检查与服务端写入验证是两个阶段；不通过试写后回滚实现预检。

## Manifest 调整

- 移除 `baseScore`，把分值放进模式规则。CTF 使用 `rules.scoreCurve`，AWDP 使用 `rules.break` 和
  `rules.fix`，AWD 使用 `rules.attackPoints` 等，KoH 使用 `rules.controlPointsPerInterval`。
- CTF Definition schemaVersion 为 3、Rules schemaVersion 为 2；AWD/AWDP Definition 与 Rules 均为 4；KoH 均为 1。
- `customTitle: null` 表示使用题库标题。更新 API 始终显式发送该字段。
- 不使用 revision / expectedRevision。可变资源遵循 last-write-wins。
- 比赛详情从 `competition` 与 `capabilities` 聚合响应读取；比赛题目详情从 `challenge` 与
  `rulesJson` 聚合响应读取。题库模板和比赛题目分别使用 `content`、`presentation/rules`
  section 的 PATCH；不再调用旧 PUT 或独立 `/configuration`。
- `flagTemplate` 配置在比赛题目 Rules，不再属于 Challenge Definition。
- AWDP Runtime 必须为 PerTeam Flag、配置 FLAG 注入环境变量及仅所属队伍可见的访问入口。
- 可选 `checkerFixInput: true` 位于 AWDP `challenge.yml` 根节点，要求配置 Checker。
- 访问入口可用 `urlTemplate` 自定义显示文本；未设置时沿用 Http/Tcp 默认模板。
- 带引号的 YAML 数字/布尔值保持字符串，适合环境变量；端口等未加引号数值按数值处理。

例如 CTF 计分：

```yaml
customTitle: null
order: 10
published: false
rules:
  schemaVersion: 2
  scoreCurve:
    initialPoints: 500
    minimumPoints: 100
    decayTeamCount: 10
    decayMode: 2
```

Issue 中的 Base Score 仅用于生成初始模式规则，不会成为平台字段。

## 预检与应用

```sh
dotnet run --file .github/scripts/repository.cs -- self-test
dotnet run --file .github/scripts/repository.cs -- apply --dry-run
dotnet run --file .github/scripts/repository.cs -- apply
```

dry-run 会解析镜像 digest、检查比赛/题库权限、验证不可变附件身份及本地 Manifest，
对 NoCTF 只发送 GET；它不会执行完整服务端模式校验，实际 CRUD 仍可能返回业务校验错误。
它不会创建、删除、恢复、发布或修改题目资源，不是跨资源事务，也不锁定后续写入。
镜像须先完成构建/推送；纯静态题无需 Docker 镜像。

GitOps 对整场比赛的题目集合及受管模板的附件、静态 Flag、Hint 集合进行收敛。
不要把尚未纳入 Manifest 的资源留在这些集合中，否则下一次 Apply 会软删除它们。
题库模板删除仍必须提供配对的 `--base`、`--head`，只处理明确的 Git 删除记录。

初始化必须确认 Bot 有比赛 Owner/Manager 权限，而不是仅能读取比赛。
比赛 Manager 权限不包含题库编辑权限；接管模板时须由原 Owner 单独授权。

## 失败恢复

- GitOps 使用的集合接口返回完整 `items`，每次只读取一次；不会把 offset 分页或签名 cursor 协议套用到这些接口。
- 不盲目重试 POST/PUT/DELETE。创建响应丢失时按稳定 UUID 重读，只有父资源和内容吻合才算成功。
- 中途失败后重新运行同一 Apply，按当前状态继续；新 UUID 不由重试生成。
- 资源 ID、附件内容、权限与生命周期冲突不会被当作可重试网络错误。
- 附件内容、文件名或 ContentType 改变时分配新附件 UUID。上传只允许 attachments/ 中的普通文件。
- 附件上传使用批量 multipart 契约 `DeliveryPolicy=All`、`AttachmentIds` 与 `Files`；GitOps 不接管 RandomOnePerTeam 附件集合。
- 交换题目顺序会先挪到空闲序号，再写入目标顺序，避免唯一约束冲突。
- 恢复题目会先避让原序号，再依次恢复并腾空旧槽位，最后统一写入目标序号，支持中断重跑。
- 更新已发布题目不再临时取消发布，Shared 模板也不临时降级为 Private；依赖失败保持原发布状态。
- Container、Checker 和 Compose serviceImages 都必须使用唯一的 build/external 引用，外部镜像必须
  是 sha256 digest，普通字符串不能绕过此规则。
- 不记录 Bot JWT、完整 Flag 或任意 API 响应正文。错误输出保留请求方法、路径、状态码及稳定业务码。

只改题面、附件或计分规则时，不重建无关镜像。Runtime/Checker 构建上下文、Dockerfile 或对应
构建定义变化时才构建对应镜像；改动构建脚本时全量重建以避免缓存规则变化。

`contract-fixtures` 仅用于平台契约测试，输出的 example.invalid 镜像不可部署，不能用于 Apply。

## source hash 升级

新 hash 使用 `noctf-source-v2` 域、长度前缀、文件内容摘要及 Git 模式，修复不同文件树产生同一
输入字节串的问题。升级脚本后首次运行应全量构建（手动 workflow 勾选 rebuild_all）；旧 src tag
不删除，现有题目已引用的 digest 保持有效。该身份描述源码输入，不宣称 Docker 构建本身完全可重复。
