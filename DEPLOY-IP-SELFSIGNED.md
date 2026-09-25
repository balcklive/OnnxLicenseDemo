# 用 IP + 自签证书部署（无域名、免备案）

> 面向"不想买域名 / 备案一时办不下来"的场景。客户端（bot-cs）**只信任你自己签的 CA**，
> 客户机上不需要安装任何证书、不需要管理员权限，也不会看到"是否信任此 CA"的确认框。
>
> 有域名且能备案的话走 `DEPLOY.md`（Let's Encrypt 那套），那份文档仍然有效，两者互不干扰。
> 本文只讲**与 DOMAIN 那套不同的地方**，相同的地方（`.env`、后台使用、发码/席位/撤销语义）不重复。

## 0. 这条路换来了什么、代价是什么

| | 说明 |
|---|---|
| **换来的** | 不买域名、不做 ICP 备案、不需要任何公共 CA 证书；客户机零安装（对比"给每台客户机装根证书"那条路——Windows 会强制弹一个写着"这是安全风险"的确认框，用户级存储还要管理员权限且换 Windows 账户就失效） |
| **代价 1** | **换 CA 必须重新发布客户端**（客户端 pin 的是 CA）。反过来**服务器证书续期、换 IP 都不用重发客户端**——这是这套做法最值钱的地方 |
| **代价 2** | 自签体系没有 CRL/OCSP，**没有吊销机制**。CA 私钥泄露只能换 CA + 重发客户端 |
| **代价 3** | 客户端做了证书钉扎，**企业网络里如果有 SSL 拦截代理会连不上**（代理会替换证书）。家用网络无此问题 |

---

## 1. 前置：先确认到底哪个端口能用

**这一步必须先做，而且必须区分"超时"和"拒绝"**——两者含义完全不同：

| 现象 | 含义 |
|---|---|
| `Connection timed out` | 包**被静默丢弃**，端口被挡了，用不了 |
| `Could not connect` / `Connection refused` | 包**到了主机**、只是没人监听 —— **端口是通的，可以用** |

自测（把 `<IP>` 和 `<端口>` 换掉）：

```bash
curl -sS -o /dev/null --max-time 6 "http://<IP>:<端口>/" ; echo " <- 看这行的报错文案"
```

**本服务器（182.42.59.49）的实测结论**：`80` / `443` / `8080` / `8443` 全部**静默超时**（机房对未备案
IP 的常见做法），而 **`9443` 回的是"拒绝" = 可用**。所以本文一律用 **9443**。

> 判据的对照组：同一台机器上 `12345` / `4443` / `3000` 这些没人监听的端口都老老实实回了"拒绝"，
> 唯独 Web 端口族被丢包——**被丢包 ≠ 没服务**，所以别看到连不上就以为"起了 nginx 就好了"。
>
> **换端口不用改证书**：证书的 SAN 里只有 IP，**端口从来不进证书**。

---

## 2. 签 CA + 服务器证书（在服务器上跑，git bash 自带 openssl）

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
```

产出的三个文件各去哪：

| 文件 | 去哪 | 是不是秘密 |
|---|---|---|
| `ca.crt` | 贴进客户端的 `LicenseTrustAnchor.cs`（第 3 步） | 否，公开信息 |
| `ca.key` | **离线保存**，签完就从服务器删掉 | **是，最高级机密** |
| `server.crt` / `server.key` | nginx 用（第 4 步） | 公钥 / 私钥 |

---

## 3. 把 CA 证书贴进客户端（漏了这步，包发出去全部连不上）

打开 bot-cs 仓库的 `src/BotCs/Infrastructure/Licensing/LicenseTrustAnchor.cs`，把 `ca.crt` 全文贴进 `CaCertificatePem`：

```csharp
    private const string CaCertificatePem = """
        -----BEGIN CERTIFICATE-----
        ...ca.crt 的全部内容（含首尾两行）...
        -----END CERTIFICATE-----
        """;
```

把 `ca.crt` 内容打出来的办法：`cat certs/ca.crt`

> `publish-portable.ps1` 在授权包模式下会**检查这里是否为空**，空着直接 `throw` 不出包——
> 与"宁可不出包，也不能出一个漏加密的包"是同一条规矩。因为空锚的包**必然连不上服务器**，
> 而且症状只会在客户机上暴露（`--license-check` 会打印 `服务器信任锚 : 未内置`）。

---

## 4. 起 nginx（监听 9443）

### 4.1 `nginx/license.conf` 整个换成：

```nginx
# 自签证书 + IP 直连形态。没有域名，所以不需要 80 端口，也不需要 ACME 校验。

server {
    listen 9443 ssl;
    server_name 182.42.59.49;

    ssl_certificate     /etc/nginx/certs/server.crt;
    ssl_certificate_key /etc/nginx/certs/server.key;

    location / {
        proxy_pass http://licenseserver:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

### 4.2 `docker-compose.yml` 的 nginx 段改成：

```yaml
  nginx:
    image: nginx:alpine
    restart: unless-stopped
    depends_on: [licenseserver]
    ports:
      - "9443:9443"                      # 原来是 "80:80" / "443:443"
    volumes:
      - ./nginx/license.conf:/etc/nginx/conf.d/default.conf:ro
      - ./certs:/etc/nginx/certs:ro      # 新增：第 2 步签出来的证书
    networks: [internal]
```

（`certbot_www` / `certbot_certs` 两个 volume 在这条路上用不到了，留着无害，删掉更清爽。`db` 与
`licenseserver` 两段**一个字都不用动**。）

### 4.3 删掉 `docker-compose.override.yml`

那个文件把 `licenseserver` 的 8080 **明文**映射到公网 18080，是当初本机联调用的。
上线形态下管理后台走 `https://182.42.59.49:9443/Admin` 访问，这个明文口子必须关掉。

---

## 5. 起服务 + 自检

```bash
docker compose up -d
docker compose logs -f licenseserver     # 看到 Now listening: http://[::]:8080 即成功
docker compose logs nginx                # 有证书报错会在这里
```

两条自检（把 `certs/ca.crt` 带上，等价于客户端会做的事）：

```bash
# ① 证书链 + 反代 + 到后端，一把全验
curl --cacert certs/ca.crt -i https://182.42.59.49:9443/api/public-key
#   期望 200 + 一段 PUBLIC KEY PEM（内容与 <jwt_keys>/public_key.pem 一致）

# ② 故意用一张不存在的码，验 EF 与 JSON 契约
curl --cacert certs/ca.crt -i -X POST https://182.42.59.49:9443/api/heartbeat \
  -H 'Content-Type: application/json' \
  -d '{"code":"NOPE-0000-0000-0000","machineId":"probe"}'
#   期望 404 + {"error":"激活码不存在"}
```

⚠️ 第二条是**故意给一张不存在的码**。别拿真码去试——真码 + 一个假指纹会在该证的名额里占掉一台设备。

浏览器打开 `https://182.42.59.49:9443/Admin`：因为你的浏览器不认这把自签 CA，会先看到一个证书警告，
手动继续访问即可（管理后台是自用，不影响交付的客户端——客户端走的是钉扎，不看系统证书库）。

---

## 6. 打包与验收

客户端仓库根执行（地址带端口，脚本的 `^https://` 校验能过）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\publish-portable.ps1 `
  -ModelKeyFile D:\keys\bot-cs.model.key `
  -ServerUrl https://182.42.59.49:9443
```

验收与 `docs/licensing.md` 第 4 节完全一致，**新增一条前置检查**：

| # | 动作 | 期望 |
|---|---|---|
| 0 | 在**一台从没装过任何证书的干净机器**上跑 `BotCs.exe --license-check` | `服务器信任锚 : 已内置自签 CA（只信任这一把）`；且**全程没有被要求装任何证书**。这条就是这个方案本身 |
| 1 | 双击 `BotCs.exe` | 弹「BotCs 授权」激活窗口 |
| 2 | 填后台建的码 | 进主界面，日志出现 `LicenseGuard: 授权通过（启动授权）` |
| 3 | `--license-check` | `授权服务器 : 182.42.59.49`、`模型解密 : 成功，N 个类别`，退出码 0 |
| 4 | 后台撤销这张证，等 ≤10 分钟 | 自动战斗停止、按键松开、弹「授权已失效」，**进程不退出** |

---

## 7. 运维

| 动作 | 要做什么 | 要不要重发客户端 |
|---|---|---|
| **服务器证书快到期** | 重签 `server.crt`/`server.key` → 换文件 → `docker compose exec nginx nginx -s reload` | **不要** |
| **换服务器 IP** | 重新签一张（SAN 改成新 IP）→ 换证书 → 重新打包（`-ServerUrl` 改新地址） | **要**（因为地址变了） |
| **换 CA** | 重签 CA + 重发客户端 | **要** |
| 续费一张许可证 | 后台没有入口，手工 `UPDATE "LicenseKeys" SET "ExpiryUtc" = ... WHERE "Code" = '...'` | 不要 |

**必须备份的三样**（丢了都是不可恢复的）：产品密钥文件、`db_data` volume、`jwt_keys` volume。
**另外建议把 `certs/ca.key` 也一起放进离线备份**——它是这套信任体系的总开关。

---

## 8. 故障对照（只列这条路上特有的）

| 现象 | 大概率原因 | 怎么确认 |
|---|---|---|
| 客户端 `--license-check` 报 `服务器信任锚 : 未内置` | 打包时漏贴 CA | 回到第 3 步；重跑发布脚本（会直接 throw 拦住） |
| 客户端报"名称不匹配"类 TLS 错误 | 证书 SAN 没写对，或写了域名但不是 IP | `openssl x509 -in server.crt -noout -text \| grep -A1 "Subject Alt"` |
| `curl` 报证书链不受信任 | 没带 `--cacert`（或者客户端里贴的是另一把 CA） | 带 `--cacert certs/ca.crt` 再试 |
| 端口连不上、且是**超时** | 该端口也被机房封了（第 1 节那件事） | 换一个回"拒绝"的端口；证书不用改 |
| nginx 起不来 | 证书路径不对 / `certs` 目录没挂进容器 | `docker compose logs nginx` |
| 换过 CA 之后**老客户端**全部连不上 | 这是设计使然：pin 的是 CA | 重发客户端 |

---

## 9. 相关文件

- `DEPLOY.md`：有域名时的部署路径（Let's Encrypt + 80/443）。
- `USAGE.md`：启动形态、发码/席位/撤销语义、机器规格、通用故障对照表。
- `bot-cs/docs/licensing.md`：客户端侧的交付/发码/验收手册。
- `bot-cs/src/BotCs/Infrastructure/Licensing/LicenseTrustAnchor.cs`：客户端信任锚（本文第 3 步要改的就是它）。
- `bot-cs/src/BotCs/Infrastructure/Licensing/AGENTS.md`：信任锚的三条不可碰约束与反向验证办法。
