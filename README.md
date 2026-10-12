# Factory Switcher for Windows

Windows 本地 Factory / Droid 登录账号切换工具，支持官方 OAuth 登录、多账号额度查询，以及可恢复的会话共享。

参考 [lhfer/factory-switcher](https://github.com/lhfer/factory-switcher) 的 macOS 交互与备份流程，以及 [shariqriazz/droid-switcher](https://github.com/shariqriazz/droid-switcher) 的登录隔离和认证格式。这是独立的 C# / WinForms 实现，不是 Factory 官方产品。

## 功能

- 通过官方 Droid 和浏览器 OAuth 添加账号，保存当前已登录账号，手动切换登录态。
- 支持 Windows 凭据管理器的 `auth.v2.keyring`，以及 `auth.v2.file` + `auth.v2.key`。
- 账号与系统密钥快照使用当前 Windows 用户 DPAPI 加密；切换前备份，校验后应用，失败回滚，支持恢复中断操作。
- 直接查看每个账号的 5 小时、周、月剩余额度、Standard / Core 等额度组、重置时间与额外用量余额。支持刷新所选或全部账号、加密缓存与查询失败提示。
- 对未在本机使用的登录按需续期；只请求一次旋转刷新令牌，先保存新凭据，再查询额度。当前客户端正在使用的同一登录不会由工具同时续期。
- 勾选和预览本地会话，只移除选中会话头部的 `organizationId`；正文保持原样。先保存校验过的加密备份，可恢复组织归属并保留后来追加的消息。
- 添加和保存账号不要求关闭其他客户端；实际切换登录态或修改会话前需退出 Droid / Factory。工具不会强制结束任务。

## 下载与启动

在 [Releases](https://github.com/Shio0909/factory-switcher-for-windows/releases) 下载 Windows x64 压缩包，解压到稳定目录（例如 `D:\Apps\FactorySwitcher`），双击 `DroidSwitcher.exe`。

需要 Windows 10/11 x64、**.NET 9 Windows Desktop Runtime** 和官方 Droid CLI。桌面启动按钮还需要 Factory 桌面版。程序未进行代码签名。

程序优先识别本机既有的 `D:\Apps\FactoryDroid\droid.exe`、`D:\Apps\FactoryDesktop\Factory\factory-desktop.exe` 和 `D:\Apps\DroidSwitcher\data`；否则在 PATH 中查找 Droid，使用标准 Factory 目录，并把工具数据放到程序旁的 `data`。

如需自定义路径，将 `settings.example.json` 复制为程序旁的 `settings.json`，填写绝对路径。`null` 使用默认值。配置文件中不需要放令牌或密钥。

## 添加和切换账号

1. 点击“添加账号（官方登录）”，为账号命名。
2. 在新终端完成官方浏览器登录。浏览器自动选旧账号时，将授权链接复制到无痕窗口。
3. 登录后按 **Ctrl+C** 退出 Droid，**不要 `/logout`**；工具自动保存账号。
4. 添加其他账号后，退出正在运行的 Droid 和 Factory（包含托盘），选中账号并切换。再次打开客户端后确认邮箱与组织。

正常客户端里已登录的账号可以点击“保存当前登录”导入。登录后保存失败可点击“重试保存新登录”；不再需要的未完成登录可明确放弃。账号添加和保存不会切换正在使用的登录。

## 查询额度

在“账号与额度”页点击“刷新所选额度”或“刷新全部额度”。列表显示 Standard（如未提供则选第一组）的窗口摘要，选中账号查看全部组和重置时间。

查询仅访问 `https://api.factory.ai/api/billing/limits`，续期仅访问 `https://api.workos.com/user_management/authenticate`，禁止自动重定向。不会将登录信息发送到其他服务。接口返回缺失字段时显示未知，过期窗口显示待开启，不把旧窗口用量当作当前额度。失败保留上次结果及查询时间。

## 共享会话

在“会话共享”页刷新本地会话，勾选记录并预览，退出 Droid / Factory 后点击“共享勾选会话”。工具不会自动共享全部历史记录。

共享仅修改本地组织标记，不上传记录；**用另一个账号继续会话时，记录可能发送给该账号所属组织**。请根据会话内容选择要共享的记录。云端记录、附件和远程电脑上的记录不由此功能迁移。

恢复时选择相应备份，点击“恢复所选备份”。恢复只还原组织归属，不丢弃共享后追加的消息。若记录已被其他操作绑定到新组织，工具拒绝自动恢复。会话索引是缓存，修改后让 Droid 重建。单个会话上限 128MB。

## 数据与恢复

账号快照、额度缓存、登录恢复日志与会话备份都留在本机。工具数据目录限制为当前 Windows 用户访问，保存内容使用 DPAPI 加密。另一台机器或另一个 Windows 用户不能直接使用这些快照。

登录隔离目录中的认证文件由官方 Droid 按标准格式写入，成功保存后清理认证文件；未完成登录可重试或放弃。历史备份不自动删除。“移除所选快照”不注销当前客户端，也不删除历史备份。

切换中断后使用“恢复上次切换”；会话共享中断后，在共享页恢复标为“未完成”的备份。未知认证格式、无效密钥、多套认证文件、路径越界与链接路径会被拒绝。工具没有自动切号、账号注册、额度修改或遥测功能。

## 开发与验证

使用 .NET 9 SDK，在 Windows 上构建：

```powershell
dotnet publish .\src\DroidSwitcher.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\dist
.\dist\DroidSwitcher.exe --self-test .\test-results
```

报告写入 `test-results\self-test.json`，成功退出码为 0。发布前通过 31 项测试，覆盖加密互操作、认证事务、Windows 测试凭据项、额度接口模拟、刷新令牌旋转、会话正文保留和恢复、路径检查。模拟测试不读取真实账号或会话；使用独立的临时 Windows 测试凭据项，并在结束时清理。

开发时已验证 Windows keyring 真实登录解密，以及两个已保存账号的真实 Standard/Core 额度查询。真实会话只进行读取和预览；共享与恢复流程使用模拟会话验证。

GitHub Actions 已启用，在 Windows 上构建并运行上述模拟测试。`docs/windows-build.yml.example` 另提供可复用的工作流示例。

## 许可与致谢

MIT，见 [LICENSE](LICENSE)。Droid GCM 格式兼容使用 Bouncy Castle.Cryptography 2.7.0，许可见 [BouncyCastle-LICENSE.md](BouncyCastle-LICENSE.md)。参考项目代码未复制或捆绑。Windows 凭据管理器的目标名和 UTF-8 密钥格式与 [keytar 的 Windows 后端](https://github.com/atom/node-keytar/blob/master/src/keytar_win.cc) 一致。
