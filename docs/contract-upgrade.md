# GitOps v2 与 NoCTF 0.3.0 契约

本仓库只支持 `gitops.noctf.dev/v2`，不读取或迁移 v1 Manifest。脚本通过 NoCTF 现有认证、
资源读取和 CRUD 接口工作，不增加同步接口、Revision 字段或服务端仓库解析。

## 强类型 Manifest

- `challenge.yml.definition` 使用 `mode` 和唯一的 `ctf`、`awd`、`awdp` 或 `koh` 分支。
- Runtime 使用 `kind` 和唯一的 `container`、`compose` 或 `ova` 数据分支；本模板脚手架只生成
  Container 与 Compose。Container 端口映射的 `hostPort` 必须为 `0`。
- CTF 必须显式设置 `definition.ctf.interactionKind`。
- AWD Flag 注入位于 `definition.awd.flagInjection`；Checker 是 `definition.checker` 的直接 Runner Job。
- AWDP Patch、`checkerFixInput` 与 `maximumPatchUploadBytes` 都属于 Definition。
- `competition.yml.rules` 同样使用 `mode` 和唯一模式分支；AWDP 曲线名为
  `breakScoreCurve` 与 `fixScoreCurve`。
- Flag Template 位于对应的比赛题 Rules 模式分支，不放入 Challenge Definition。
- Manifest 不包含 `schemaVersion`、`definitionJson`、`rulesJson`、`baseScore`、provider 或 runnerPool。

构建镜像仍使用 Manifest 专用的 `{ build: key }` 或 `{ external: digest }` 引用。Apply 前会把这些
引用解析为不可变 digest，并把 Compose 文件物化成 API 的 `composeYaml`。

## 预检与应用

```sh
dotnet run --file .github/scripts/repository.cs -- self-test
dotnet run --file .github/scripts/repository.cs -- apply --dry-run
dotnet run --file .github/scripts/repository.cs -- apply
```

dry-run 只执行本地 Manifest、镜像 digest、权限和不可变附件身份检查；它不会试写后回滚，也不能
替代写入时的模式、生命周期和活动 Runtime 验证。

`competition.yml.challenges`、受管模板附件、静态 Flag 和 Hint 都是完整集合。仓库之外对这些集合
的 UI 修改会在下一次 Apply 被仓库期望状态覆盖。每个比赛仓库通过 Issue scaffold 生成独立的
Challenge UUID；不支持多个仓库共同管理同一个全局 Challenge UUID。

## 失败恢复与并发

- main Deploy workflow 使用完整 FIFO 等待队列，同一仓库的 validate、build 与 apply 不会并发。
- POST、PUT、PATCH 和 DELETE 不进行盲目传输重试。创建响应丢失时按稳定 UUID 重读并核对父资源。
- 中途失败保留此前已提交的资源；修复权限、生命周期或 Runtime 冲突后重跑同一 Apply 继续收敛。
- 顺序交换先移动到空闲序号；软删除恢复先避让墓碑原序号，再写最终序号。
- 活动 Runtime、比赛生命周期和唯一约束冲突保持 409，不绕过平台规则。
- 不记录 Bot JWT、Flag 或 API 响应正文；错误只包含方法、路径、状态码和稳定业务码。

PR workflow 不读取 Bot Token 或 Registry Secret；只有 main Deploy 的 Apply job可以读取 Bot JWT。
