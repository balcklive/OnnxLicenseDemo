# 用 IP + 自签证书部署（无域名、免备案）—— 完整跑通手册

> **本文是自包含的**：从一台空服务器开始，到把能外发的交付包打出来为止，全部步骤在这里。
> 不需要来回翻 `DEPLOY.md`（那份是"有域名 + Let's Encrypt"的另一条路线，两者互不干扰，
> 但**同一台机器上只能选一条**——它们的 nginx 监听端口和证书来源不一样）。
>
> 客户端（bot-cs）**只信任你自己签的这把 CA**，客户机上不需要安装任何证书、不需要管理员权限，
> 也不会看到"是否信任此 CA"的确认框。

## 目录

| 步骤 | 在哪台机器 | 干什么 |
|---|---|---|
| 1 | 服务器 | 前置检查：**先确认哪个端口能用** |
| 2 | 服务器 | 拉代码、配 `.env` |
| 3 | 服务器 | 签 CA 与服务器证书 |
| 4 | 打包机 | 把 CA 证书贴进客户端源码 |
| 5 | 服务器 | 改 nginx、改 compose、删掉明文覆盖文件 |
| 6 | 服务器 | 起服务 |
| 7 | 服务器 | 服务端自检 |
| 8 | 服务器/浏览器 | **生成产品密钥 + 后台建证**（发码前必做） |
| 9 | 打包机 | 打交付包 |
| 10 | 干净机器 | 客户机验收 |
| 11 | 服务器 | 上线收尾（限流、日志上限） |
| 12 | — | 运维：备份、续期、换 IP、换 CA |
| 13 | — | 故障对照 |

---

## 0. 这条路换来了什么、代价是什么

| | 说明 |
|---|---|
| **换来的** | 不买域名、不做 ICP 备案、不需要任何公共 CA 证书；**客户机零安装** |
| **代价 1** | **换 CA 必须重新发布客户端**（客户端 pin 的是 CA）。反过来**服务器证书续期、换 IP 都不用重发客户端**——这是这套做法最值钱的地方 |
| **代价 2** | 自签体系没有 CRL/OCSP，**没有吊销机制**。CA 私钥泄露只能换 CA + 重发客户端 |
| **代价 3** | 客户端做了证书钉扎，**企业网络里如果有 SSL 拦截代理会连不上**（代理会替换证书）。家用网络无此问题 |

> 为什么不走"给每台客户机装根证书"：Windows 装根证书只有两种姿势——装机器级要**管理员权限**；
> 装用户级不要管理员，但**强制弹一个写着"安装此根证书等于信任它签发的任何证书，这是安全风险"的
> 确认框，且无官方绕过**（这正是 Windows 防"程序偷偷给自己装信任"的设计）。两条对"发给外部玩家"
> 的产品都不成立；而且"管理员运行的程序 + 向系统根证书库写入"是杀软的典型行为特征。

---

## 1. 前置检查（**必须先做，否则后面全是白干**）

### 1.1 确认哪个端口能用——要分清"超时"和"拒绝"

| 现象 | 含义 | 能不能用 |
|---|---|---|
| `Connection timed out` | 包**被静默丢弃**，端口被挡了 | ❌ 用不了 |
| `Could not connect` / `Connection refused` | 包**到了主机**、只是暂时没人监听 | ✅ **能用** |

自测（把 `<IP>` 换掉，端口逐个试）：

```bash
curl -sS -o /dev/null --max-time 6 "http://<IP>:<端口>/" ; echo " <- 看这行的报错文案"
```

**本服务器（182.42.59.49）的实测结论**：`80` / `443` / `8080` / `8443` 全部**静默超时**
（机房对未备案 IP 的常见做法），**`9443` 回的是"拒绝"= 可用**。本文一律用 **9443**。

> **判据的对照组**：同一台机器上 `12345` / `4443` / `3000` 这些没人监听的端口都老老实实回了
> "拒绝"，唯独 Web 端口族被丢包。所以"被丢包"≠"没服务"——别看到连不上就以为"起了 nginx 就好了"。
>
> ⚠️ **换端口不用改证书**：证书的 SAN 里只有 IP，**端口从来不进证书**。

### 1.2 服务器基本要求

- Linux（Ubuntu 22.04 之类）+ Docker + docker compose 插件
- 如果要走本文的 9443，安全组/防火墙放行 **9443**（不是 443）
- 能访问 GitHub（`git clone`）；国内机器拉 Docker Hub 不通时见第 5.2 节的镜像源备注

```bash
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER   # 之后重新登录一次生效
```

---

## 2. 拉代码、配 `.env`

```bash
git clone https://github.com/balcklive/OnnxLicenseDemo.git
cd OnnxLicenseDemo
cp .env.example .env
nano .env
```

`.env` 只有两个值，**两个都必须填**（compose 里用的是 `${VAR:?}`，缺了直接起不来）：

```
DB_PASSWORD=一个强密码，随手生成
ADMIN_PASSWORD_SHA256=后台登录口令的 SHA-256（无换行的那种）
```

生成密码哈希（把 `你的后台口令` 换成实际口令）：

```bash
printf '%s' '你的后台口令' | sha256sum
```

> ⚠️ 这两项**不进仓库**：`.env` 已在 `.gitignore` 里。别把它提交上去。

---

## 3. 签 CA 与服务器证书（在服务器上跑，git bash 自带 openssl）

```bash
mkdir -p certs && cd certs

# ---- ① 自建 CA（一辈子做一次，签 10 年）----
openssl genrsa -out ca.key 4096
openssl req -x509 -new -key ca.key -sha256 -days 3650 \
  -subj "/CN=BotCs License CA" -out ca.crt
# ⚠ ca.key 拷进 U 盘/密码管理器后，从服务器上删掉。它是"万能钥匙"：
#   谁拿到它就能给任何域名签证书，从而对装了你这把 CA 的机器做中间人。

# ---- ② 给服务器签证书 ----
# SAN 必须是 IP。没有域名就只能这样，写漏了客户端报"名称不匹配"，
# 而且这个错很容易被误当成"证书没装对"。
printf 'subjectAltName=IP:182.42.59.49\n' > san.ext
openssl genrsa -out server.key 2048
openssl req -new -key server.key -subj "/CN=182.42.59.49" -out server.csr
openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
  -days 3650 -sha256 -extfile san.ext -out server.crt

# ---- ③ 必须验一下 SAN 真的写进去了 ----
openssl x509 -in server.crt -noout -text | grep -A1 "Subject Alternative Name"
#   期望：X509v3 Subject Alternative Name:  IP Address:182.42.59.49

cd ..   # 回到仓库根
```

产出的三个文件各去哪：

| 文件 | 去哪 | 是不是秘密 |
|---|---|---|
| `certs/ca.crt` | 贴进客户端的 `LicenseTrustAnchor.cs`（第 4 步） | 否，公开信息 |
| `certs/ca.key` | **离线保存**，签完就从服务器删掉 | **是，最高级机密** |
| `certs/server.crt` `certs/server.key` | nginx 用（第 5 步） | 公钥 / 私钥 |

> ⚠️ **确认 `certs/` 整个目录在 `.gitignore` 里**。只靠 `*.key` 挡不住——它只管私钥，
> `ca.crt` / `server.crt` / `ca.srl` / `san.ext` 这几个会被 git 当成未跟踪文件逮住。
> 签名机上补一行 `certs/` 即可（这是一次性的，补完就不用管了）。

---

## 4. 把 CA 证书贴进客户端（在打包机上，**漏了这步包发出去全部连不上**）

打开 bot-cs 仓库的 `src/BotCs/Infrastructure/Licensing/LicenseTrustAnchor.cs`，把 `ca.crt` 全文贴进 `CaCertificatePem`：

```csharp
    private const string CaCertificatePem = """
        -----BEGIN CERTIFICATE-----
        ...ca.crt 的全部内容（含首尾两行）...
        -----END CERTIFICATE-----
        """;
```

把内容打出来的办法：在服务器上 `cat certs/ca.crt`，把输出整个复制过去。

> `publish-portable.ps1` 在授权包模式下会**检查这里是否为空**，空着直接 `throw` 不出包——
> 与"宁可不出包，也不能出一个漏加密的包"是同一条规矩。因为空锚的包**必然连不上服务器**，
> 而且症状只会在客户机上暴露（`--license-check` 会打印 `服务器信任锚 : 未内置`）。

---

## 5. 改服务器上的两个配置

### 5.1 `nginx/license.conf` —— 整个文件换成：

```nginx
# 自签证书 + IP 直连形态。没有域名，所以不需要 80 端口，也不需要 ACME 校验。
# conf.d/*.conf 是被 include 进 http 块的，所以下面这行 limit_req_zone 就是 http 作用域。

# /api/activate 是公网可访问、可被脚本轮着猜码的端点，按 IP 限一下速率。
limit_req_zone $binary_remote_addr zone=license_activate:10m rate=10r/s;

server {
    listen 9443 ssl;
    server_name 182.42.59.49;

    ssl_certificate     /etc/nginx/certs/server.crt;
    ssl_certificate_key /etc/nginx/certs/server.key;

    # 激活端点单独限流；正常客户一次激活只打一发，burst 给足不会误伤。
    location /api/activate {
        limit_req zone=license_activate burst=20 nodelay;
        proxy_pass http://licenseserver:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    location / {
        proxy_pass http://licenseserver:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

### 5.2 `docker-compose.yml` —— 只改 `nginx` 那一段：

```yaml
  nginx:
    image: nginx:alpine
    restart: unless-stopped
    depends_on: [licenseserver]
    ports:
      - "9443:9443"                      # 原来写的是 "80:80" / "443:443"
    volumes:
      - ./nginx/license.conf:/etc/nginx/conf.d/default.conf:ro
      - ./certs:/etc/nginx/certs:ro      # 新增：第 3 步签出来的证书
    logging:                             # 新增：json-file 日志默认无上限，跑几个月能把小盘填满
      options:
        max-size: "10m"
        max-file: "3"
    networks: [internal]
```

> `db` 与 `licenseserver` 两段**一个字都不用动**。原 nginx 段里的 `certbot_www` / `certbot_certs`
> 两个 volume 用不到了，删掉更清爽（留着无害）。
>
> **国内机器拉 Docker Hub 不通时**（表现为 `docker compose up` 卡在 pulling），给 `db` 段加一行
> 镜像源，例如 `image: docker.m.daocloud.io/library/postgres:16`。`mcr.microsoft.com`（Dockerfile
> 里的 dotnet 基础镜像）一般直连正常，不用改。

同样建议给 `licenseserver` 段也加上那段 `logging:`（它的日志量比 nginx 大）。

### 5.3 删掉 `docker-compose.override.yml`

那个文件把 `licenseserver` 的 8080 **明文**映射到公网 18080，是当初本机联调用的。
**它是被 compose 自动加载的**，留着就等于把一个明文端口敞在公网上：

```bash
mv docker-compose.override.yml docker-compose.override.yml.disabled
```

上线形态下管理后台走 `https://182.42.59.49:9443/Admin` 访问，不需要那个口子。

> ⚠️ **服务器上这份 clone 当部署目录用，改了 `nginx/license.conf` 和 `docker-compose.yml` 之后
> 就别再直接 `git pull` 了**（会冲突）。真想保持能拉更新，用：
> `git update-index --skip-worktree nginx/license.conf docker-compose.yml`
> 之后这两个文件本地的改动不会被 pull 覆盖（同样地，仓库里的更新也拉不到这两个文件——要更新时
> 先 `git update-index --no-skip-worktree`）。

---

## 6. 起服务

```bash
docker compose up -d --build
docker compose logs -f licenseserver     # 看到 Now listening: http://[::]:8080 即成功
docker compose logs nginx                # 证书有问题会在这里报
```

**首次启动它自己会做两件事**（不用手工建库）：

1. `EnsureCreated()` 建表（`LicenseKeys` / `Activations`）；
2. 在 `jwt_keys` volume 里生成一对 **RSA-2048**（`private_key.pem` / `public_key.pem`），用于给客户端令牌签名。

`db_data` 与 `jwt_keys` 这两个 volume 就是这台机器的全部状态，备份见第 12 节。

---

## 7. 服务端自检

```bash
# ① 证书链 + 反代 + 到后端，一把全验（--cacert 等价于客户端会做的事）
curl --cacert certs/ca.crt -i https://182.42.59.49:9443/api/public-key
#   期望 200 + 一段 RSA PUBLIC KEY PEM

# ② 故意用一张不存在的码，验 EF 与 JSON 契约
curl --cacert certs/ca.crt -i -X POST https://182.42.59.49:9443/api/heartbeat \
  -H 'Content-Type: application/json' \
  -d '{"code":"NOPE-0000-0000-0000","machineId":"probe"}'
#   期望 404 + {"error":"激活码不存在"}
```

⚠️ 第二条是**故意给一张不存在的码**。别拿真码去试——真码 + 一个假指纹会在该证的名额里占掉一台设备。

浏览器打开 `https://182.42.59.49:9443/Admin`：因为你的浏览器不认这把自签 CA，会先看到一个证书警告，
**手动继续访问即可**（管理后台是自用；交付的客户端走的是钉扎，不看系统证书库）。输入 `.env` 里那个
口令能进、列表能打开，服务端就算就绪。

---

## 8. 生成产品密钥 + 后台建证（**发码前必做，顺序不能反**）

### 8.1 生成产品密钥（每个产品做一次）

在**打包机**上跑：

```powershell
dotnet run --project ModelProtector -c Release -- keygen --out D:\keys\bot-cs.model.key
```

文件里是一行 base64（32 字节 AES-256）。它**不进仓库、不进交付包**，只登记进服务器数据库 +
用来加密权重。

> ⚠️ **丢了它 = 已发出的密文模型全部作废**，只能重新加密发新包。立刻存进密码管理器并异地备份。

### 8.2 在后台建证，密钥栏**粘贴同一把**

`https://<IP>:9443/Admin` → 创建新许可证，四个字段：

| 字段 | 填什么 |
|---|---|
| 产品名称 | 随便（如 `bot-cs`） |
| 有效天数 | 按合同；常见用后台的 1 个月 / 3 个月 / 1 年预设 |
| 最多可激活设备数 | 按合同。**席位按设备算，不是按客户算** |
| **模型密钥（Base64）** | **粘贴 8.1 那把**（留空会让服务器**自动生成另一把**） |

> ⚠️ 这是**最容易发生、也最难从日志看出来**的一类故障：密钥留空 ⇒ 服务器另生成一把 ⇒ 客户端
> **能激活、进得去界面、模型加载失败**。`--license-check` 的最后一步正是为了抓它。

提交后页面会显示 `已生成激活码：XXXX-XXXX-XXXX-XXXX`（字母表不含 `0/O/1/I`）。**复制它**发给客户，
别让他手打——服务器不做归一化，漏一个横杠就是 404。

---

## 9. 打交付包（在打包机，bot-cs 仓库根）

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\publish-portable.ps1 `
  -ModelKeyFile D:\keys\bot-cs.model.key `
  -ServerUrl https://182.42.59.49:9443
```

脚本按顺序做，任何一步失败就 `throw` 中止（**不会留下一个"看着成功其实漏加密"的目录**）：

1. `dotnet publish -p:LicenseEnforce=true` —— 把授权闸门编译进去；顺带检查第 4 步的 CA 是否已贴，空着直接拒绝出包
2. `encrypt-dir` —— 把 `portable\BotCs\data\weights\**\*.onnx` **原地**加密成 BSMO 容器（幂等、文件名不变）
3. `verify-dir` —— **用同一把密钥真解密每个文件**，抓"打包用错密钥"
4. 写 `license.override.json`（服务器地址）→ 扫全包断言没有残留明文 `.onnx`

成品是 `portable\BotCs` 目录，**整目录压 zip** 发客户。升级就是让客户解压覆盖同一个目录
（激活状态在 `%LOCALAPPDATA%`，不会被覆盖，也不会因为发版重新占席位）。

---

## 10. 客户机验收（发出去之前，在**一台从没装过任何证书的干净 Windows** 上）

| # | 动作 | 期望 |
|---|---|---|
| 0 | 全程**不要**安装任何证书、不要动"受信任的根证书颁发机构" | 这条就是这个方案本身 |
| 1 | 命令行跑 `BotCs.exe --license-check` | 打印 `服务器信任锚 : 已内置自签 CA（只信任这一把）` |
| 2 | 双击 `BotCs.exe` | 弹「BotCs 授权」激活窗口，里面能复制**本机设备号** |
| 3 | 填第 8.2 步那张码 | 进主界面；日志出现 `LicenseGuard: 授权通过（启动授权）` |
| 4 | 再跑 `BotCs.exe --license-check` | `授权服务器 : 182.42.59.49`、**`模型解密 : 成功，N 个类别`**、退出码 0 |
| 5 | 开自动战斗打一轮 | 识别与攻击正常；日志每 10 分钟一条心跳续期 |
| 6 | 后台撤销这张证，等 ≤10 分钟 | 自动战斗停止、按住的键先松开、弹「授权已失效」，**进程不退出** |
| 7 | 十六进制看 `data\weights\*.onnx` 首 4 字节 | `42 53 4D 4F`（BSMO），不是 `08`（明文） |

第 4 步是唯一一条命令同时验"地址解析到哪一级 + 服务器答复 + 密钥与密文是否匹配"，**每次发版都必须跑**。

---

## 11. 上线收尾

| 项 | 状态 |
|---|---|
| `/api/activate` 限流 | ✅ 已写在第 5.1 节的 nginx 配置里（`limit_req_zone` + `limit_req`）。**这是已知缺项，别以为默认就有** |
| docker 日志上限 | ✅ 已写在第 5.2 节的 `logging:` 选项里。不配的话 json-file 默认无上限，跑几个月能填满小盘 |
| 健康检查 / `mem_limit` | ❌ 没配。当前负载（每客户端 6 请求/小时）不需要，机器紧张时再说 |
| 强制升级 `MinAppVersion` | ❌ 没做 |
| 客户端代码混淆 | ❌ 没做，发版前建议用 ConfuserEx 过一遍 |

---

## 12. 运维

### 12.1 哪些操作要重发客户端

| 动作 | 要做什么 | 要不要重发客户端 |
|---|---|---|
| **服务器证书快到期** | 重签 `server.crt`/`server.key` → 换文件 → `docker compose exec nginx nginx -s reload` | **不要** |
| **换服务器 IP** | 重签一张（SAN 改成新 IP）→ 换证书 → 重新打包（`-ServerUrl` 改新地址） | **要**（地址变了） |
| **换 CA** | 重签 CA + 重贴 `LicenseTrustAnchor.cs` + 重打包 | **要** |
| **续费一张许可证** | 后台**没有**改有效期的入口，手工执行：<br>`UPDATE "LicenseKeys" SET "ExpiryUtc" = '2027-09-25' WHERE "Code" = 'XXXX-XXXX-XXXX-XXXX';`<br>（表名大小写敏感，要带引号）改完客户下一次心跳自然通过 | 不要 |
| 撤销一台设备 | 后台点撤销。**不可逆**——同一台机器再拿这张码激活会直接 403；误撤只能手工把 `Activations.IsRevoked` 改回 `false`，或换新证 | 不要 |

### 12.2 必备份的三样（一起备份、异地各存一份、**并且验证过能恢复**）

| 东西 | 怎么取 | 丢了会怎样 |
|---|---|---|
| **产品密钥文件** | 你本机那个 `D:\keys\bot-cs.model.key` | 已发出的密文模型全部作废 |
| **`certs/ca.key`** | 你离线存的那份 | 换 CA = 全部客户机重发客户端 |
| **PostgreSQL（`db_data`）** | `docker compose exec -t db pg_dump -U license_app licenses > backup.sql` | 授权记录 + 密钥登记一起丢，等于服务失忆 |
| **令牌签名密钥（`jwt_keys`）** | `docker run --rm -v $(basename $PWD)_jwt_keys:/k -v $PWD:/out alpine tar czf /out/jwt_keys.tgz -C /k .` | 客户端缓存的离线令牌验签失败（能联网自愈，断网期间用不了） |

恢复顺序：新机器 `docker compose up -d db` → 灌 `backup.sql` → 还原 `jwt_keys` volume →
`docker compose up -d` → 在干净机器上跑一次 `BotCs.exe --license-check` 确认全链路回来了。

---

## 13. 故障对照（只列这条路上特有的）

| 现象 | 大概率原因 | 怎么确认 |
|---|---|---|
| 客户端 `--license-check` 报 `服务器信任锚 : 未内置` | 打包时漏贴 CA（第 4 步） | 重贴后重跑发布脚本——它会先 `throw` 拦住 |
| 客户端报"名称不匹配"类 TLS 错误 | 证书 SAN 没写对（写成域名了） | `openssl x509 -in certs/server.crt -noout -text \| grep -A1 "Subject Alt"` |
| `curl` 报证书链不受信任 | 没带 `--cacert`，或客户端里贴的是另一把 CA | 带 `--cacert certs/ca.crt` 再试 |
| 端口连不上、且是**超时** | 这个端口也被机房封了（第 1 节那件事） | 换一个回"拒绝"的端口；**证书不用改** |
| nginx 起不来 | 证书路径不对 / `certs` 目录没挂进容器 | `docker compose logs nginx` |
| `licenseserver` 反复重启 | 连不上 Postgres，或 `.env` 缺变量 | `docker compose logs licenseserver` |
| 换过 CA 之后**老客户端**全部连不上 | 设计使然：pin 的就是 CA | 重发客户端 |
| 客户"能激活、进得去界面、但模型加载失败" | 后台登记的密钥与包内密文不是同一把 | `--license-check` 看 `模型解密 : 失败`；改后台登记或用对的密钥重打包 |
| 后台明明有这张码，服务器却回 `激活码不存在` | 服务器不做归一化。客户端只去空格 + 转大写，**横杠不能少** | 让客户按后台显示的重打，或直接把那行复制给他 |
| 撤了但客户还在跑 | 还没到下一次心跳（≤10 分钟），或他在离线宽限期内 | 看后台最后心跳时间是否还在往前走 |

> 客户报障统一先让他执行 `BotCs.exe --license-check`（本机没存过码就 `BotCs.exe --license-check <激活码>`）
> 并把**整段输出**贴回来。它会依次打印：程序目录、状态目录、交付包模式、解析到的服务器、
> 本机设备号、状态文件与公钥有无、**服务器信任锚**、服务器答复、离线宽限、**模型能否真解密**。

---

## 14. 相关文件

- `DEPLOY.md`：有域名时的另一条部署路径（Let's Encrypt + 80/443）。**两条路线同一台机器只能选一条**。
- `USAGE.md`：启动形态与自检、发码/席位/撤销的真实语义、机器规格建议、通用故障对照表。
- `README.md`：BSMO 容器格式、`ModelProtector` 全部命令、诚实的安全边界说明。
- `nginx/license.conf`：反代配置（本文第 5.1 节要改的就是它）。
- bot-cs 侧：
  - `docs/licensing.md`：交付/发码/排障手册 + 端到端验收清单
  - `src/BotCs/Infrastructure/Licensing/AGENTS.md`：信任锚的三条不可碰约束与反向验证办法
  - `src/BotCs/Infrastructure/Licensing/LicenseTrustAnchor.cs`：客户端信任锚（本文第 4 步要改的就是它）
  - `tools/publish-portable.ps1`：发布脚本（含"CA 未贴就拒绝出包"的守卫）
