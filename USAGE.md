# 用法手册（授权服务 + 交付包）

> 面向第一次接手这套东西的人：**怎么把服务起起来、起对了没有、日常怎么发一张许可证给客户、出问题先查哪里**。
> 部署到云服务器的完整操作（证书、crontab 续期）在 `DEPLOY.md`；本文不重复那部分，只讲用法与判读。
> 客户端侧（bot-cs）的规矩在 `D:\07-games\bot-cs\src\BotCs\Infrastructure\Licensing\AGENTS.md`，交付/发码/验收手册在 `D:\07-games\bot-cs\docs\licensing.md`。

## 1. 这套东西由哪几块组成

| 块 | 在哪跑 | 干什么 |
|---|---|---|
| `LicenseServer` | 云服务器（Docker） | 三个公开 API + Razor 管理后台；发令牌、记激活设备、存产品密钥 |
| PostgreSQL 16 | 云服务器（Docker） | 两张表：`LicenseKeys`（激活码 + `ModelKeyBase64`）、`Activations`（机器指纹、最后心跳） |
| nginx | 云服务器（Docker） | 唯一对外的进程：80 跳 443 + ACME 校验，443 反代到 `licenseserver:8080` |
| `ModelProtector` | **你的打包机** | BSMO 容器加解密（`keygen/encrypt/decrypt/encrypt-dir/verify-dir/inspect`），不进交付包 |
| bot-cs 客户端 | 客户机 | 授权闸门 + 心跳 + 读 BSMO 密文模型（`Infrastructure/Licensing`） |

先说清一件最容易搞错的事：**模型加密不在服务器上**。加密发生在你跑 `publish-portable.ps1` 的那台机器，
服务器只在客户通过校验后，把那把产品密钥随令牌下发。所以客户数再涨，服务器也不会因为“解密模型”而吃 CPU。

## 2. 启动（生产形态，推荐）

前置：一台 Linux（Ubuntu 22.04 之类）+ Docker + **域名已解析到这台机器的公网 IP**。

```bash
git clone <本仓库> OnnxLicenseDemo && cd OnnxLicenseDemo
cp .env.example .env
# 1) DB_PASSWORD：随手一个强密码
# 2) ADMIN_PASSWORD_SHA256：后台登录口令的 SHA-256（无换行的那种）
printf '%s' '你的后台口令' | sha256sum    # 结果填进 .env
# 3) 把 nginx/license.conf 里的 your-domain.com 换成真实域名
```

然后**先申请证书再起 compose**：证书路径在 `nginx/license.conf` 里写死为
`/etc/letsencrypt/live/<域名>/fullchain.pem|privkey.pem`，没证书 nginx 起不来（具体命令见 `DEPLOY.md` 第二步）。

```bash
docker compose up -d --build            # db → licenseserver → nginx
docker compose logs -f licenseserver    # 看到 Now listening: http://[::]:8080 即成功
```

首次启动它自己会做两件事：`EnsureCreated()` 建表（`LicenseServer/Program.cs:32`）、在 `jwt_keys` volume 里生成一对
**RSA-2048**（`private_key.pem` / `public_key.pem`）用于给令牌签名。`db_data` 与 `jwt_keys` 这两个 volume 就是这台机器的全部状态，见第 9 节。

## 3. 不用 Docker 的跑法（联调用）

`LicenseServer` 启动即连库，Postgres 连不上就抛异常退出——所以**没有“只跑一个 exe 就能用”的形态**。两个可行姿势：

- **只要后台能用**：`docker compose up -d db`，然后设两个环境变量再跑进程：
  `ConnectionStrings__Default="Host=localhost;Port=5432;Database=licenses;Username=license_app;Password=<DB_PASSWORD>"`、
  `ADMIN_PASSWORD_SHA256=<哈希>`，`dotnet run --project LicenseServer`（或 publish 后跑 dll）。
- **只要客户端能连**：上面那条 + 让它监听 `localhost:5000`（db 占的是 5432），客户端用 `BOTCS_LICENSE_URL=http://localhost:5000` 指过去。
  这条能成立是因为客户端对**本机回环**放行 http（`LicenseDefaults.IsAcceptableScheme`）；非回环地址一律必须 https。

## 4. 起对了没有：两条自检

```bash
curl -i https://<域名>/api/public-key     # 期望 200 + 一段 PUBLIC KEY PEM（内容要和 <jwt_keys>/public_key.pem 一致）
curl -i -X POST https://<域名>/api/heartbeat -H 'Content-Type: application/json' \
     -d '{"code":"NOPE-0000-0000-0000","machineId":"probe"}'
# 期望 404 + {"error":"激活码不存在"} —— 说明反代、TLS、EF、JSON 契约全通
```

请求体的字段名就三个：`code` / `machineId` / `machineName?`（`Program.cs:108` 的 `ActivateRequest`；绑定大小写不敏感，
客户端发的是小写三个键）。响应：`200 {token, expiresUtc}`，拒绝一律带 `{error}`（`LicenseClient` 正是靠 "4xx + 有 error" 才判成硬拒绝）。

浏览器开 `https://<域名>/Admin`：能跳登录页、口令能进、列表能看到刚建的证，交付环境就算就绪。

⚠ 第二条是**故意给一张不存在的码**。别拿真码去试——真码 + 一个假指纹会在该证的名额里占掉一台设备。
已经占了就去后台撤销那台设备的激活——能释放名额，但**撤销后这台机器再也用不了这张码**（不可逆，见 5.5）。

## 5. 日常用法：完整走一圈

### 5.1 一个产品做一次：生成产品密钥

```powershell
dotnet run --project ModelProtector -c Release -- keygen --out D:\keys\bot-cs.model.key
```

文件里是一行 base64（32 字节 AES-256）。不给 `--out` 就打印到标准输出。它**不进仓库、不进交付包**，
只登记进服务器数据库 + 用来加密权重。丢了它 = 已发出的密文模型全部作废。

### 5.2 在后台登记这把密钥

`https://<域名>/Admin` → 创建新许可证，四个字段：产品名称、有效天数、最多可激活设备数、
**模型密钥（Base64，留空则自动生成新的）**。

- 这把密钥**必须**是 5.1 那把。留空会让服务器另生成一把：客户端能激活、进得去界面、**模型加载失败**——
  这是最容易发生也最难从日志看出来的一类故障，`--license-check` 的最后一步正好能查它。
- 提交后页面显示 `已生成激活码：XXXX-XXXX-XXXX-XXXX`，字母表是 `ABCDEFGHJKLMNPQRSTUVWXYZ23456789`（不含 `0/O/1/I`），
  所以客户端做大写归一是无损的。

### 5.3 打交付包（在 bot-cs 仓库根）

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\publish-portable.ps1 `
  -ModelKeyFile D:\keys\bot-cs.model.key `
  -ServerUrl https://<你的授权域名>
```

脚本顺序：`dotnet publish -p:LicenseEnforce=true` → `encrypt-dir`（原地加密 `portable\BotCs\data\weights\**\*.onnx`，幂等、文件名不变）→
`verify-dir`（**用同一把密钥真解密每个文件**）→ 写 `portable\BotCs\license.override.json` → 断言全包没有明文 `.onnx`。
任一步失败就 throw。不给 `-ModelKeyFile`/`-ModelKey` 就是内部明文包，构建时会打印
`Licensing: DISABLED -> ... Internal build only.`，这种包**不得外发**。

### 5.4 交给客户

整个 `portable\BotCs` 目录压 zip，客户解压覆盖即可。**不需要**再给 `license.lic`、`public_key.pem` 或密钥文件——
公钥由客户端自动向服务器取并缓存，激活状态存在客户本机。客户第一次运行会弹激活窗口，把 5.2 那张码发给他。

### 5.5 发码 / 席位 / 撤销

- **席位按设备算**，不是按客户算：一张证最多 `最多可激活设备数` 台机器。
- **同一台机器重复激活不占新名额**：服务器按（这张证 + `machineId`）找已有激活记录，找到就直接续发令牌；
  只有**新** `machineId` 才去挤 `最多可激活设备数` 这个上限。所以客户换硬件/重装导致指纹变了，才会真的多占一个名额。
- **撤销单台设备 = 释放一个名额**（`activeCount` 只数未撤销的设备），客户换电脑就走这一步。
- ⚠ **撤销是不可逆的，而且没有“恢复”按钮**：同一台被撤销的机器再拿这张码来激活，服务器直接 403
  `此设备的激活已被管理员撤销`（`LicenseServer/Program.cs:60`）。误撤之后只有两条路——手工把数据库里那条 `Activations.IsRevoked`
  改回 `false`，或者重建一张新证发新码。别信后台确认框里“撤销这台设备”这句话听起来像可撤销。
- **撤销整张证** = 该证下所有设备在下一次心跳时全部失效（403 `激活码已被吊销`）。
- **到期** = 服务器拒绝 403 `订阅已过期，请续费`。⚠ 后台**没有改有效期的入口**（只有创建/撤销两类操作），
  续费要手工 `UPDATE "LicenseKeys" SET "ExpiryUtc" = ... WHERE "Code" = '...'`（表名大小写敏感，要带引号），
  改完客户下一次心跳自然通过，**客户端顶栏的「授权 剩余 N 天」也跟着刷新**（想立刻看到就让他重启程序）。
- 生效延迟：客户端心跳节拍 **10 分钟**（`LicenseDefaults.HeartbeatInterval`），断网时按 **60 秒**重试，所以撤销最迟 10 分钟见效。
- ⚠ 已经在内存里的那份权重停不掉（要换图或重启才失效）——**这条要提前对客户讲清楚**，别当成 bug。
- 一张证别横向卖给多个客户：共用一张证意味着一个人的欠费会连带所有人掉线。

## 6. 客户端地址与本机文件（排障必须知道的事实）

服务器地址解析优先级，从高到低：

1. 环境变量 `BOTCS_LICENSE_URL`（客服远程指导时最省事，不用动包）；
2. 包内 `license.override.json`（由 `publish-portable.ps1 -ServerUrl` 写；路径可用 `BOTCS_LICENSE_OVERRIDE_PATH` 指走）；
3. 编译进 exe 的内置值（正常交付包里指向 `license.invalid` 占位，会被判成“未配置”并要求联网激活）。

- **都不进 `config.json`**：那是入库文件，而 GUI 保存写的是整份配置。服务器地址与激活码一律按凭证对待。
- 服务器地址也**不上客户端 GUI**（激活窗口、失效弹框都只显示中性词，见 `bot-cs` 的
  `Infrastructure/Licensing/AGENTS.md`）。要让客户核对连的是哪台，让他跑 `BotCs.exe --license-check`，
  那是唯一会打印地址的地方。日志里也只打 host。
- 非回环地址必须 `https://`，否则解析直接失败（不是“凑合用”）。
- 本机状态目录 `%LOCALAPPDATA%\BotCs\license\`（整目录可用 `BOTCS_LICENSE_HOME` 指走）：
  `activation_state.bin`（DPAPI 加密，存**客户输入的那张码**）、`server_public_key.pem`、`machine_id.json`（兜底 id）。
  刻意不放 exe 旁边：交付包按目录整体覆盖升级，放 exe 旁等于每次发版全体客户重新激活、自己把席位刷爆。
- 令牌默认有效期 **5 天**（`LicenseServer/Services/JwtService.cs` 里的 `TimeSpan.FromDays(5)`）= 最多容忍断网 5 天；
  离线宽限用的仍是**签名有效**的缓存令牌，拿不到公钥就判“离线不可用”，不会退化成只看本地时间。
  这个 5 天与客户买的时长无关，别混（客户看到的是令牌里的 `subExp`）。
- 客户端 HTTP 超时 12 秒。后台登录是**单一管理员口令**（只存 SHA-256），没有多账号、没有操作日志。

## 7. 机器规格与负载

负载模型：一个在跑的客户端 = 每 10 分钟一次心跳 = 6 请求/小时。一次心跳的代价是 2~3 个带索引的点查 +
1 行 UPDATE + 一次本地 RSA-2048 签名（亚毫秒），响应体几百字节 JSON。

- 100 台在线 ≈ 0.17 req/s；1 000 台 ≈ 1.7 req/s；**10 000 台 ≈ 17 req/s** —— 这个量级 CPU 基本躺平。
- 常驻内存才是大头（常规量级估算，未在目标机器实测）：`licenseserver`（ASP.NET 8）约 150~250MB、
  `postgres:16`（默认 `shared_buffers=128MB`）约 150~250MB、`nginx:alpine` 约 5~10MB。
- 结论：**1 vCPU / 1GB 能跑**（顺手把 `shared_buffers` 调到 64MB 更稳），**2C2G / 20~40GB SSD 是舒适区**。
  磁盘增长以 MB/年 计（两张表的行数 = 激活码数 + 设备数）。再往上配对这套东西没意义。
- ⚠ 当前 `docker-compose.yml` **没有任何** `mem_limit`/`cpus`/`logging`/`healthcheck`。风险不是资源不够，而是
  json-file 日志无上限，跑几个月能把小盘填满。上线后补一条：
  `docker update --log-opt max-size=10m --log-opt max-file=3 $(docker compose ps -q)`。
- 同理 `nginx/license.conf` 里**没有** `limit_req`：`/api/activate` 是公网可访问、可被脚本轮着猜码的端点，
  建议加按 IP 的 `limit_req`（如 10 r/s、burst 20）。**这是已知缺项，不是“已经配好了”**。

## 8. 上线前必须满足的 4 个硬条件

1. **必须有域名，不能只给 IP**：签不出 `https://1.2.3.4/` 这类证书，而客户端对非回环地址只认 https。
2. **证书必须被系统信任**：客户端用 `HttpClient` 默认 TLS 校验，自签证书握手直接失败，别指望“自签先凑合”。
3. **80/443 真的对外可达**：80 还兼着 ACME http-01 校验，被安全组/运营商拦掉就签不到也续不了。
4. **`-ServerUrl` 写的域名必须与证书 CN/SAN 完全一致**（含端口）。这是打包参数写错时后果最大的一条：包发出去了，全体客户连不上。

国内服务器补充：主域名走 80/443 对外提供服务一般要**备案**。不想处理备案，可以用已备案域名的二级域名，
或放境外节点再前置 CDN/反代——那时 `-ServerUrl` 填对客户公开的那个域名，仍然必须 https。

## 9. 备份与恢复

只有三样东西不可再生，一起备份、异地各存一份、并且**验证过能恢复**：

| 东西 | 怎么取 | 丢了会怎样 |
|---|---|---|
| 产品密钥（`D:\keys\bot-cs.model.key`） | 你本机那个文件 | 已发出的密文模型全部作废，只能重新加密发新包 |
| PostgreSQL（`db_data` volume） | `docker compose exec -t db pg_dump -U license_app licenses > backup.sql` | 授权记录 + 密钥登记一起丢，等于服务失忆 |
| 令牌签名密钥（`jwt_keys` volume） | `docker run --rm -v <项目名>_jwt_keys:/k -v $PWD:/out alpine tar czf /out/jwt_keys.tgz -C /k .` | 客户端缓存的离线令牌验签失败（能联网自愈，断网期间用不了） |

恢复顺序：新机器 `docker compose up -d db` → 灌 `backup.sql` → 还原 `jwt_keys` volume → `docker compose up -d` →
在干净机器上跑一次 `BotCs.exe --license-check` 确认全链路回来了。

## 10. 出问题先查这里

| 现象 | 大概率原因 | 怎么确认 |
|---|---|---|
| compose 起了但 nginx 起不来 | 证书还没签 / 域名没换成真的 | `docker compose logs nginx`：看是找不到文件还是 `cannot load certificate` |
| `licenseserver` 反复重启 | 连不上 Postgres 或 `.env` 缺变量 | `docker compose logs licenseserver`（`EnsureCreated` 失败会直接抛） |
| `curl /api/public-key` 超时但容器在跑 | 安全组没放行 443 | 从机器**外面** `curl -v`，分清 TCP 不通还是 TLS 报错 |
| 客户端提示未配置授权服务器地址 | 包里 `license.override.json` 被删/被安全软件清 | `--license-check` 看 `授权服务器 : 未配置 —— …` |
| 客户端报 `非本机地址必须用 https://` | `-ServerUrl` 填成 http，或有人手改了 override | 同上，看它把哪一级解析出来了 |
| TLS 握手失败 | 自签 / 链不全 / 域名不匹配 | `openssl s_client -connect <域名>:443 -servername <域名>` |
| 客户“能激活但模型加载失败” | 后台登记的密钥与包内密文不是同一把 | `--license-check` 的 `模型解密 : 失败`；改登记或用对的密钥重打包 |
| 客户突然不能开 | 到期 / 被撤销 / 超席位后旧设备被撤 | 后台看这张证的设备列表与最后心跳；让他跑 `--license-check` |
| 撤了但客户还在跑 | 还没到下一次心跳（≤10 分钟），或他在离线宽限期内 | 看后台最后心跳时间是否还在往前走 |
| 后台明明有这张码，服务器却回 `激活码不存在` | 服务器**不做任何归一化**：`/api/activate` 用 `Code` 原样比对（`LicenseServer/Program.cs:51`）。客户端只去空格/制表符 + 转大写（`LicenseActivationWindow.NormalizeCode`），**横杠不能少** | 让客户按后台显示的 `XXXX-XXXX-XXXX-XXXX` 重打，或直接把后台那行复制给他 |
| 客户说“上次你帮我撤销过，现在激活不上了” | 撤销不可逆，那台机器的记录还挂着 `IsRevoked` | 见 5.5：手工改回 `false` 或换新证 |


客户报障统一先让他执行并把输出贴回来：`BotCs.exe --license-check`（本机没存过码就 `BotCs.exe --license-check <激活码>`）。
输出会依次打印：程序目录、状态目录、交付包模式（`开（模型必须是密文，启动需授权）` / `关（日常开发构建，不校验授权）`）、
解析到的服务器 host、本机设备号、状态文件与公钥有无、服务器答复、离线宽限、**模型能否真解密**（成功会打印类别数）。

## 11. 已知缺项（别当已实现）

- **没有 EF Migrations**：schema 靠启动 `EnsureCreated()`，所以**给实体加字段不会自动改已有库**——改表要手工 `ALTER TABLE`
  并同步 `LicenseServer/Data/Models.cs`（这条在 bot-cs 侧被列为 P2）。
- **单一管理员口令**：无多账号、无操作日志。
- **后台只有“创建 / 撤销”两类操作**：既不能改有效期（续费要手工 `UPDATE`），也不能恢复已撤销的设备（5.5）。
  这两件事一定会在客户身上发生，先把那两条 SQL 写进运维笔记并演练一遍。
- **激活码没有归一化**：库里存的就是后台生成的 `XXXX-XXXX-XXXX-XXXX`，`/api/activate` 与 `/api/heartbeat` 都拿 `Code` 做原样字符串比对。客户手打漏横杠、
  或把 `L` 看成 `I`（字母表已排除 `0O1I`）都会 404，而后台没有“按前缀查码”的入口，只能整条对照。所以要么给他**复制**而不是手打，要么把归一化补到查询侧。
- 没有强制升级（`MinAppVersion`）、没有按图分密钥（按图卖/按图到期做不到）、没有客户端代码混淆。
- `ClientApp/Program.cs` 里的 `ServerBaseUrl` 是**硬编码常量**：那只是最小演示客户端，真正的交付客户端是 bot-cs
  （地址走 override / 环境变量）。别把两者混为一谈；`ClientApp` 也仍兼容旧版裸头 `.onnx.enc`，新交付包别再那么发。
- 日志轮转、限流、健康检查都没配，见第 7 节末尾两条。

## 12. 相关文件

- `DEPLOY.md`：从零部署到云服务器（证书、续期 crontab、后台登录、打包给客户）。
- `README.md`：BSMO v1 容器格式、`ModelProtector` 全部命令、诚实的安全边界说明。
- `LicenseServer/Program.cs`：三个公开 API 与判定逻辑；`LicenseServer/Pages/Admin/Index.cshtml{,.cs}`：后台页面。
- `nginx/license.conf`：80→443、ACME 路径、`proxy_pass http://licenseserver:8080`。
- 客户端侧：`D:\07-games\bot-cs\docs\licensing.md`（交付/发码/排障 + 第 4 节端到端验收清单）、
  `D:\07-games\bot-cs\src\BotCs\Infrastructure\Licensing\AGENTS.md`（模块规矩与历史踩坑）。
