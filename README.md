# ONNX 模型加密 + 订阅制许可证（联网激活 + Web管理后台版）

## 项目结构

```
OnnxLicenseDemo/
├── LicenseServer/      # 授权服务器：API + Web管理后台，部署到你的云服务器
├── ModelProtector/     # 本地小工具：BSMO 容器的加解密（打包机上跑，不进交付包）
├── ClientApp/          # 最终交付客户的程序（演示核心逻辑）
├── nginx/              # 反向代理 + HTTPS 配置示例
├── docker-compose.yml  # 一键编排 LicenseServer + PostgreSQL + Nginx
├── DEPLOY.md           # 完整的云服务器部署步骤（证书、compose、后台登录）
├── DEPLOY-IP-SELFSIGNED.md  # 无域名路线：IP + 自签证书 + 非标端口（免备案，客户机零安装，
│                        #   自包含全流程：空服务器 → .env → 签证书 → 后台建证 → 出包）
├── USAGE.md            # 用法手册：启动/自检/发码席位撤销/机器规格/故障对照表
└── README.md           # 本文件：结构、容器格式、命令速查、安全边界
```

## 整体架构

```
  客户电脑上的 ClientApp
        │  1. 首次运行：输入激活码 -> POST /api/activate
        │  2. 之后定期心跳：POST /api/heartbeat 换新令牌（bot-cs 客户端是每 10 分钟）
        ▼
  你的云服务器: LicenseServer (Docker容器)
        │  - 验证激活码是否有效/是否吊销/是否过期
        │  - 记录这台机器的激活时间、最后心跳时间
        │  - 签发一个短期有效的令牌(RS256签名)，里面带着模型解密密钥
        ▼
  PostgreSQL 数据库（记录所有License和Activation）

  你自己在浏览器打开 https://your-domain.com/Admin
        - 看到所有许可证、每张证下激活了哪些设备、最后在线时间
        - 一键吊销整张证 / 单台设备
```

## 从这里开始

1. **服务已经在跑了、只是要用/要排障** → 看 **`USAGE.md`**：启动形态与自检、发码/席位/撤销的真实语义、
   机器规格建议、备份清单、故障对照表（含几条只有读代码才能发现的坑）。
2. **从零部署到云服务器** → 看 **`DEPLOY.md`**（申请证书、`.env`、compose 起服务、登录后台、打包给客户）。
   **没有域名、或者不想走备案** → 看 **`DEPLOY-IP-SELFSIGNED.md`**：用 IP + 自签证书，客户端只信任
   内置的那把 CA，客户机上不需要安装任何证书。注意**端口不一定是 443**——机房常把未备案 IP 的
   80/443/8080/8443 静默封掉，那份文档第 1 节有"怎么分辨端口是被封了还是没人监听"的判据。
3. `ModelProtector` 现在写 **BSMO 容器**（2026-09-24 起）：交付包走「原地加密整个目录」，文件名不变，
   客户端按文件头区分密文/明文。`ClientApp` 仍兼容旧版 28 字节裸头的 `.onnx.enc`，新交付包别再那么发。
   命令与格式见下面「ModelProtector 命令（BSMO v1 容器）」，接 bot-cs 见「接入 bot-cs」。
4. 许可证不再由本地 CLI 工具签发，改成登录 Web 后台点几下按钮生成。

## ModelProtector 命令（BSMO v1 容器）

容器格式（**读侧在 bot-cs 的 `ModelVault`/`ModelContainer`，改格式两边必须同步**）：

```
[0,4)   magic  ASCII "BSMO"
[4]     版本   = 1
[5,8)   保留   = 0
[8,20)  nonce  12 字节
[20,36) tag    16 字节（AES-256-GCM）
[36,)   密文   整个原始 .onnx
```

```powershell
dotnet run --project ModelProtector -c Release -- <命令> [参数]

keygen      [--out <path>]                     # 生成一把产品密钥（base64 一行）
encrypt     --in <f> --out <f> [--key <b64>|--keyfile <p>]
decrypt     --in <f> --out <f> --key <b64>|--keyfile <p>
encrypt-dir --dir <root> [--pattern *.onnx] (--key|--keyfile) [--dry-run]
verify-dir  --dir <root> [--pattern *.onnx] [--key|--keyfile]
inspect     --in <f>
```

- `encrypt-dir` 是**原地**加密、幂等（已经是 BSMO 容器的文件跳过）、**必须显式给密钥**，
  并且匹配到 0 个文件就算失败——**宁可不出包，也不能静默出一个明文包**。
- `verify-dir` 带密钥时会**真解密**每个文件，这是唯一能抓住“打包用错密钥”的检查。
- 退出码：0 成功 / 1 校验失败 / 2 用法错误。ONNX 是 protobuf，首字节恒 `0x08`，撞不上 `BSMO`，
  所以密文与明文可以沿用同一个文件名（客户端按文件头判定）。
- `keys/`、`*.key`、`.env` 已被 `.gitignore` 排除：**产品密钥与令牌签名密钥都不入库、不进交付包**。

## 接入 bot-cs（真实客户端）

`ClientApp` 只是演示协议用的最小客户端。真正交付给客户的是隔壁仓库 `D:/07-games/bot-cs`（.NET 10 WPF）：

- 读侧与授权闸门：`bot-cs/src/BotCs/Infrastructure/Licensing/`（规矩与踩坑见该目录 `AGENTS.md`）。
- 打包：`bot-cs` 仓库根执行 `powershell -ExecutionPolicy Bypass -File .\tools\publish-portable.ps1 -ModelKeyFile <产品密钥文件> -ServerUrl https://<你的域名>`，
  脚本会 publish（带 `-p:LicenseEnforce=true`）→ `encrypt-dir` → `verify-dir` → 写 `license.override.json` → 断言全包无明文 `.onnx`。
- 发版验收、发码/席位/撤销、客户排障（`BotCs.exe --license-check`）：见 `bot-cs/docs/licensing.md`。
- 服务器地址**不进** `config.json`（那是入库文件），随包走 `license.override.json`，临时覆盖用环境变量 `BOTCS_LICENSE_URL`。
- 客户端只认 `RS256`、必须校验 `machine` claim，并每 10 分钟向 `/api/heartbeat` 续期（吊销在下一次心跳生效）。
- 令牌除 `machine/license/product/modelKey/exp` 外还带 **`subExp`**（订阅到期，unix 秒 = `LicenseKey.ExpiryUtc`）——
  客户端顶栏的「授权 剩余 N 天」只认它。⚠ 与 `exp` **不是一回事**：`exp` 只是 5 天离线宽限期、每次心跳重置，
  拿它当剩余天数会让客户天天看到"还剩 5 天"。客户端读到没有该 claim 的老令牌时显示「授权：天数未知」占位，
  不报错也不告警（服务端升级当天全体存量客户都是这个状态，最多 10 分钟随下一次心跳补上）。
- 三个 csproj 都设了 `RollForward=LatestMajor`：只有 .NET 10 运行时的机器上，即便目标框架是 net8/9 也能直接跑。

## 诚实的安全边界说明

请一定认识到这几点，不要对"防破解"抱有不切实际的预期：

1. **模型迟早要在客户端内存里以明文形式存在**（OnnxRuntime 需要读懂它才能推理），只要攻击者有能力对你的进程做内存dump，理论上总能把权重导出来。本方案的目标是"挡住随手复制授权/伪造激活状态的普通用户，并且让你能远程监控和吊销"，不是"挡住有专业逆向能力的攻击者"。
2. 如果你的模型价值极高、绝对不能落地到客户端，**唯一从根本上解决问题的办法是把推理放在你自己的服务器上**，客户端软件只负责发送输入、接收输出。
3. 建议在正式发布前，用 [ConfuserEx](https://github.com/mkaring/ConfuserEx)（开源、成熟、免费）对 ClientApp 做一次混淆，增加反编译读懂校验逻辑的难度。
4. 机器指纹在虚拟机/云主机上可能不稳定，生产环境建议叠加"首次运行生成一个随机GUID写入注册表"作为兜底。
5. 管理后台目前是单一管理员密码登录，够小团队内部用；如果多人协作管理，建议后续加多账号 + 操作日志。
6. **一个产品一把模型密钥**（2026-09-24 定的口径）：全部权重共用它，密钥只存在服务器数据库里、随令牌下发。
   好处是“许可证失效”与“拿不到模型”变成同一件事，客户端不需要第二套放行判断；代价是**按图卖/按图到期做不到**，
   要按图分密钥得换成 `mapId → key` 的 JSON 多带一个 claim（容器和打包流水线都不用改）。
7. **吊销停不掉已经在内存里的那份权重**：心跳判失效后客户端会清掉密钥、停掉自动功能，但已建好的 `InferenceSession`
   仍会照常推理，直到换模型或重启。要“吊销即失能”得让心跳密钥持续参与推理链路（定期换密钥重建会话），成本高、收益有限。
