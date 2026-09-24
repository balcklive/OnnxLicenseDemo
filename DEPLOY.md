# 部署到云服务器指南

## 前提条件

- 一台云服务器（Ubuntu 22.04 或类似发行版），开放 80/443 端口
- 一个域名，A 记录解析到这台服务器的公网 IP（比如 `license.yourcompany.com`）
- 服务器上安装好 Docker 和 docker compose 插件：

```bash
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER   # 之后重新登录一次生效
```

## 第一步：上传代码 & 配置

把 `OnnxLicenseDemo` 整个目录上传到服务器（`scp` 或 `git clone` 都行），然后：

```bash
cd OnnxLicenseDemo
cp .env.example .env
nano .env   # 填好下面两个值
```

`.env` 需要两个值：

```
DB_PASSWORD=一个随便设的数据库密码，别用弱密码
ADMIN_PASSWORD_SHA256=你管理员密码的SHA256哈希
```

生成密码哈希（把 `你的密码` 换成实际密码）：

```bash
echo -n "你的密码" | sha256sum
```

把 `nginx/license.conf` 里的 `your-domain.com` 全部替换成你的真实域名。

## 第二步：先申请 HTTPS 证书（鸡生蛋问题的解法）

Nginx 配置里引用的证书这时候还不存在，所以要先用"只监听80端口"的临时配置把证书拿到手，再切到正式配置：

```bash
# 1. 临时只跑 nginx 的 80 端口部分（先注释掉 license.conf 里 443 的 server 块，或者用下面的一次性命令代替）
sudo docker run --rm -p 80:80 \
  -v $(pwd)/certbot-www:/var/www/certbot \
  -v $(pwd)/certbot-certs:/etc/letsencrypt \
  certbot/certbot certonly --standalone \
  -d your-domain.com --agree-tos -m your-email@example.com --non-interactive

# 2. 把上面命令里的两个目录换成 docker-compose.yml 里对应的 volume 名字，
#    或者直接把证书结果目录挂载进 nginx 的 volumes（docker-compose.yml 已经预留了 certbot_certs volume）
```

> 如果你更熟悉别的证书申请方式（比如宝塔面板、Caddy 自动证书等），完全可以替换掉这一步，
> 核心只是要在 `/etc/letsencrypt/live/your-domain.com/` 下拿到 `fullchain.pem` 和 `privkey.pem`。

证书续期（Let's Encrypt 证书3个月过期一次），加一条 crontab：

```bash
0 3 * * * docker compose run --rm certbot renew && docker compose exec nginx nginx -s reload
```

## 第三步：启动服务

```bash
docker compose up -d --build
docker compose logs -f licenseserver   # 看看有没有报错
```

首次启动会自动：
- 在 Postgres 里建好表（`LicenseKeys` / `Activations`）
- 在 `/app/keys`（对应 `jwt_keys` 这个持久化 volume）生成一对 RSA 密钥，用于给客户端令牌签名

## 第四步：登录管理后台

浏览器打开 `https://your-domain.com/Admin`，会先跳到登录页，输入你在 `.env` 里设置的那个密码。

进去之后可以：
- **创建新许可证**：填产品名、有效天数、最多激活几台设备，生成一个激活码（形如 `AB3D-EFGH-1234-5678`）发给客户
- **查看每张许可证的激活情况**：哪些机器激活了、最后一次心跳时间（如果一台设备很久没心跳了，说明这个客户可能很久没用了或者换了机器）
- **一键撤销**：撤销整张许可证（客户所有设备立即失效），或者只撤销某一台设备（比如客户说要换新电脑，你撤销旧设备就能腾出激活名额）

## 第五步：出一个可以外发的交付包

### 5.1 每个产品做一次：生成产品密钥

```powershell
dotnet run --project ModelProtector -c Release -- keygen --out D:\keys\bot-cs.model.key
```

文件里是一行 base64，就是**这个产品全部权重共用的那把 AES-256 密钥**。它**不进仓库、不进交付包**，
只登记在授权服务器的数据库里（下一步），随令牌下发给客户端。丢了它 = 已发出去的密文模型全部作废。

### 5.2 后台登记同一把密钥

在 `https://<你的域名>/Admin` 创建许可证时，“模型密钥”那一栏**粘贴 5.1 那把**（留空会让服务器自动生成
另一把，客户端就解不开模型：表现是“能激活、进得去界面、模型加载失败”）。产品名称、有效天数、
最多可激活设备数按客户合同填。

### 5.3 打包（真实客户端是 bot-cs）

在 `D:\07-games\bot-cs` 仓库根执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\publish-portable.ps1 `
  -ModelKeyFile D:\keys\bot-cs.model.key `
  -ServerUrl https://<你的授权域名>
```

脚本按顺序做四件事，任何一步失败就中止（不会留下一个“看着成功其实漏加密”的目录）：

1. `dotnet publish -p:LicenseEnforce=true` —— 把授权闸门编译进去。**不给** `-ModelKeyFile` 就是内部明文包，
   构建时会打印 `Licensing: DISABLED`，这种包**不得外发**。
2. `encrypt-dir` —— 把 `portable\BotCs\data\weights\**\*.onnx` 原地加密成 BSMO 容器，文件名不变。
3. `verify-dir` —— 用同一把密钥真解密每个文件，抓“打包用错密钥”。
4. 写 `portable\BotCs\license.override.json`（服务器地址），再断言全包没有残留明文 `.onnx`。

服务器地址**刻意不进 `config.json`**（那是入库文件，GUI 保存的又是整份配置）。发出去之前，在干净机器上
跑一次 `BotCs.exe --license-check`，确认它打印“模型解密：成功，N 个类别”。完整验收清单见
`bot-cs/docs/licensing.md` 第 4 节。

### 5.4 演示客户端（只想验证协议时用）

`ClientApp` 是这套协议的最小演示：改 `Program.cs` 里的 `ServerBaseUrl` 成你的域名，`dotnet publish` 出 exe，
和它旁边的 `model.onnx.enc`（用 5.1 那把密钥加密）一起发客户即可，公钥由客户端自动向服务器取并缓存。

## 日常运维要点

- **一定要备份** `jwt_keys` 这个 volume 和数据库。`jwt_keys` 丢了不会导致客户完全无法使用（因为心跳时服务器看的是数据库记录，不看旧token），但所有客户端本地缓存的离线令牌会验签失败，断网期间会用不了，得等联网重新心跳。
- 客户端默认令牌有效期是5天（在 `JwtService.cs` 里的 `TimeSpan.FromDays(5)`，可以自己改），也就是最多容忍客户端断网5天。
- 交付出去的 bot-cs 每 **10 分钟**向 `/api/heartbeat` 续一次令牌（`LicenseGuard` 的心跳线程）。所以后台点“撤销”之后，
  客户最迟 10 分钟内会停掉自动功能；**已经在内存里的那份权重停不掉**（换模型或重启才失效），这条要对客户说清楚。
- **产品密钥（5.1 那把）要和数据库、`jwt_keys` 一起备份**。数据库丢了 = 授权记录和模型密钥一起丢，全部密文作废。
- 客户报障先让他跑 `BotCs.exe --license-check` 把整段输出贴回来；日志里只会出现密钥指纹与激活码尾号，不会泄露整张码。
- 想要更强的安全性，给这台服务器也配一下防火墙规则、定期更新系统补丁、数据库密码定期轮换，这些是通用的服务器安全常识，这里就不展开了。
