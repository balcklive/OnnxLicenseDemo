# BotCs 重连频道识别部署与验收

`POST /api/reconnect/recognize` 接收 BotCs 授权 JWT 和频道列表 PNG。服务端实时查验订阅和设备激活状态，通过后代调 VLM，返回实际选中的 `channel` 及其文字中心 `x`、`y`。坐标是相对于上传裁图的 0～1 比例。供应商 Key 只配置在服务端。

## 部署

沿用现有授权服务的 HTTPS、PostgreSQL 和 JWT 签名卷。已有部署直接更新应用，无数据库迁移。首次部署参见 [IP + 自签 CA 部署](DEPLOY-IP-SELFSIGNED.md)或[域名部署](DEPLOY.md)。

1. 更新包含此接口的服务端代码。如果服务器使用自行维护的 Compose 文件，把仓库 `docker-compose.yml` 中对应的四个环境变量透传合并到实际的 `licenseserver.environment` 段。
2. 在服务器未入库的 `.env` 中加入以下四项。直接运行服务进程时，将同名变量注入进程环境。

   ```dotenv
   ReconnectVlm__Endpoint=https://api.deepseek.com/chat/completions
   ReconnectVlm__Model=deepseek-flash
   ReconnectVlm__Product=<授权后台对应许可证的 ProductName 原值>
   ReconnectVlm__ApiKey=<部署秘密配置中的 DeepSeek API Key>
   ```

   `Endpoint` 要填完整 HTTPS 图片 Chat Completions 接口。产品名按大小写精确匹配。服务端需要能通过 HTTPS 访问供应商；发往 `api.deepseek.com` 的请求会显式关闭 thinking。已有的数据库、后台和证书配置继续按原部署文档维护。

3. 在现有 Compose 项目目录重建服务并检查状态。下列命令以 PostgreSQL 和反代已运行的部署为前提。

   ```bash
   docker compose up -d --build --no-deps licenseserver
   docker compose ps licenseserver
   docker compose logs --tail=50 licenseserver
   ```

## 最小化测试

无需先启动 BotCs。准备一张已知选中频道的列表裁图，例如 `channel-panel.png`，让选中标记和频道文字都能看清。PNG 最大 512 KiB、1600×1000。准备一个独立测试许可证，产品名与上述配置一致，订阅有效且有空闲设备名额。下面的命令会使用设备标识 `reconnect-vlm-smoke` 激活该测试许可证。重复运行会复用同一条测试激活。

在有 Bash、curl 和 jq 的机器上运行。把地址和 CA 路径改成实际值。公网 CA 部署将 `tls=(--cacert ./certs/ca.crt)` 改成 `tls=()`。

```bash
(
  set -euo pipefail
  base_url='https://<授权服务器地址>:9443'
  tls=(--cacert ./certs/ca.crt)

  read -r -s -p '测试激活码: ' test_code
  printf '\n'
  jwt="$(
    printf '%s' "$test_code" |
      jq -Rs '{code: ., machineId: "reconnect-vlm-smoke"}' |
      curl "${tls[@]}" --fail-with-body -sS "$base_url/api/activate" \
        -H 'Content-Type: application/json' --data-binary @- |
      jq -er '.token | strings | select(length > 0)'
  )"
  unset test_code

  printf 'Authorization: Bearer %s\n' "$jwt" |
    curl "${tls[@]}" -sS --max-time 20 -i "$base_url/api/reconnect/recognize" \
      --header @- -H 'Content-Type: image/png' --data-binary @channel-panel.png
)
```

预期是 HTTP 200，返回类似 `{"channel":26,"x":0.41,"y":0.62}`。以截图为准，频道号应和选中项一致，坐标应落在其文字上。HTTP 200 且三个字段为空，说明上游调用返回了结果，但未确认选中项。使用已知截图拿到正确的号码和坐标，才算端到端验收通过。测试激活可以在后台撤销。

| 状态 | 优先检查 |
| --- | --- |
| 404 | 在识别请求上出现时，检查反代地址和部署版本 |
| 401 | JWT 缺失、无效或已过期，重新获取测试 JWT |
| 403 | 产品名、订阅期限、许可证或设备是否已吊销 |
| 400 | 请求体是原始 PNG，检查 Content-Type、大小和尺寸 |
| 429 | 单进程每设备每分钟 4 次、每授权 12 次，等一个限流窗口再重试 |
| 502 | 供应商 Key、端点、模型、出站 HTTPS、上游响应格式或 8 秒超时 |
| 503 | 四项配置是否实际透传进运行中的服务，Endpoint 是否为 HTTPS |

接口通过后，再在新版 BotCs 的 `config.json` 中启用 `Reconnect.UseVlmChannelConfirmation` 并指定目标频道。真实重连时客户端先用本地 OCR，无法确认已选频道才提交频道列表裁图。
