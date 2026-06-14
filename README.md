# DesensitizeProxy — 本地 PII 脱敏代理

一个 .NET 10 隐私脱敏代理，拦截用户消息 → 识别并脱敏 PII（手机号、身份证、姓名、地址、密码等） → 转发到云端 LLM。

```
用户消息 → Regex 脱敏 (确定性) → 规则引擎分级 → LLM 语义脱敏 (尽力) → 云端 LLM
```

**设计原则**：纵深防御、失败安全、Regex 不可回退。默认 StrictMode=false，LLM 脱敏失败时静默降级为 Regex-only 转发，不阻断业务。

---

## 目录

- [架构概览](#架构概览)
- [安装指南](#安装指南)
  - [macOS](#macos)
  - [Linux](#linux)
  - [Windows](#windows)
  - [Docker](#docker)
- [配置说明](#配置说明)
- [使用示例](#使用示例)
- [健康检查 & 可观测性](#健康检查--可观测性)
- [编译 & 发布](#编译--发布)
- [常用场景配置](#常用场景配置)
- [项目结构](#项目结构)

---

## 架构概览

```
┌─────────────────────────────────────────────────┐
│          DesensitizeProxyMiddleware              │
│                                                 │
│  请求 → 读取 body → 抽取 TextPart[]              │
│     │                                           │
│     ▼                                           │
│  Layer 1: Regex 脱敏（始终运行）                  │
│    Phase 1: 硬编码  (SSH Key, AWS Key…)  不可关闭 │
│    Phase 2: 可配    (手机号/身份证/邮箱)  默认开启 │
│    Phase 3: 上下文  (密码是/取件码…)  关键词门控  │
│     │                                           │
│     ▼                                           │
│  规则引擎分级 (基于原始文本)                      │
│    Trigger → 必须追加 LLM                        │
│    Hint    → 结合 Regex 命中/StrictMode 判定     │
│    None    → 跳过 LLM                            │
│     │                                           │
│     ▼                                           │
│  Layer 2: LLM 两步法脱敏（尽力）                  │
│    Step 1: 本地小模型提取 PII JSON                │
│    Step 2: 代码按 value 长度降序替换              │
│    Step 3: Regex 兜底（不可回退）                 │
│     │                                           │
│     ▼                                           │
│  Schema 清洗 → Multi-auth → YARP 转发            │
└─────────────────────────────────────────────────┘
```

**三层协同**：Regex 成本最低始终运行，LLM 成本最高仅在规则引擎判定需要时才调用。LLM 结果写回前必须再跑 Regex 兜底，确保不会还原已脱敏内容。

---

## 安装指南

### 前置依赖

所有平台都需要：

| 依赖 | 说明 |
|------|------|
| .NET 10 SDK | 编译和运行 |
| Ollama (可选) | 本地 LLM 语义脱敏。不需要语义脱敏可跳过，退化为纯 Regex 模式 |

> **提示**：如果不跑本地 LLM，将配置中 `LocalModel.Enabled` 设为 `false` 即可，代理仅使用 Regex 脱敏（手机号/身份证/邮箱/SSH Key 等）。

---

### macOS

#### 1. 安装 .NET 10 SDK

```bash
brew install dotnet-sdk

# 验证
dotnet --version   # 应显示 10.x.x
```

#### 2. 安装 Ollama（可选，用于语义脱敏）

```bash
brew install ollama

# 启动服务
ollama serve

# 新开终端，拉取推荐模型
ollama pull openbmb/minicpm4.1
```

#### 3. 克隆 & 构建

```bash
git clone <repo-url> && cd locrouter

dotnet restore
dotnet build -c Release
```

#### 4. 配置上游 API Key

编辑 `src/DesensitizeProxy.AspNetCore/appsettings.json`，或设置环境变量：

```bash
export OPENAI_API_KEY="sk-你的key"
```

#### 5. 运行

```bash
# 开发模式
dotnet run --project src/DesensitizeProxy.AspNetCore

# 或者先发布再运行
dotnet publish src/DesensitizeProxy.AspNetCore \
  -c Release -r osx-arm64 --self-contained false \
  -o artifacts/publish/osx-arm64

./artifacts/publish/osx-arm64/DesensitizeProxy.AspNetCore
```

#### 6. 设为 launchd 服务（开机自启）

```bash
# 复制发布产物
sudo mkdir -p /usr/local/share/desensitize-proxy
sudo cp -r artifacts/publish/osx-arm64/* /usr/local/share/desensitize-proxy/

# 安装 plist
sudo cp deploy/launchd/com.clawxrouter.privacy-proxy.plist /Library/LaunchDaemons/
sudo launchctl load /Library/LaunchDaemons/com.clawxrouter.privacy-proxy.plist

# 验证
sudo launchctl list | grep clawxrouter
```

数据目录：`~/Library/Application Support/ClawXRouter/PrivacyProxy/`
日志目录：未配置 `Runtime.LogDirectory` 时为 `DesensitizeProxy.AspNetCore.dll` 所在目录；显式配置后使用配置目录。

---

### Linux

#### 1. 安装 .NET 10 SDK

```bash
# Ubuntu / Debian
wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y dotnet-sdk-10.0

# RHEL / Fedora
sudo dnf install dotnet-sdk-10.0

# 验证
dotnet --version
```

#### 2. 安装 Ollama（可选）

```bash
curl -fsSL https://ollama.com/install.sh | sh

# 拉取模型
ollama pull openbmb/minicpm4.1
```

#### 3. 克隆 & 构建

```bash
git clone <repo-url> && cd locrouter

dotnet restore
dotnet build -c Release
```

#### 4. 配置环境变量

```bash
# 创建环境变量文件
sudo mkdir -p /etc
sudo tee /etc/desensitize-proxy.env << 'EOF'
OPENAI_API_KEY=sk-你的key
EOF
```

#### 5. 发布 & 部署

```bash
# 发布
dotnet publish src/DesensitizeProxy.AspNetCore \
  -c Release -r linux-x64 --self-contained false \
  -o artifacts/publish/linux-x64

# 部署到 /opt
sudo mkdir -p /opt/desensitize-proxy
sudo cp -r artifacts/publish/linux-x64/* /opt/desensitize-proxy/
```

#### 6. 设为 systemd 服务（开机自启）

```bash
# 安装 service 文件
sudo cp deploy/systemd/desensitize-proxy.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable desensitize-proxy
sudo systemctl start desensitize-proxy

# 验证
sudo systemctl status desensitize-proxy
```

数据目录：`~/.local/share/clawxrouter/privacy-proxy/`
日志目录：未配置 `Runtime.LogDirectory` 时为 `DesensitizeProxy.AspNetCore.dll` 所在目录；显式配置后使用配置目录。

---

### Windows

#### 1. 安装 .NET 10 SDK

从 [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0) 下载安装程序，或使用 winget：

```powershell
winget install Microsoft.DotNet.SDK.10

# 验证
dotnet --version
```

#### 2. 安装 Ollama（可选）

从 [ollama.com](https://ollama.com/download/windows) 下载安装程序。

```powershell
ollama pull openbmb/minicpm4.1
```

#### 3. 克隆 & 构建

```powershell
git clone <repo-url>; cd locrouter

dotnet restore
dotnet build -c Release
```

#### 4. 设置环境变量

```powershell
# 当前会话
$env:OPENAI_API_KEY = "sk-你的key"

# 永久设置（需管理员）
[System.Environment]::SetEnvironmentVariable("OPENAI_API_KEY", "sk-你的key", "User")
```

#### 5. 运行

```powershell
# 开发模式
dotnet run --project src/DesensitizeProxy.AspNetCore
```

#### 6. 发布 & 安装为 Windows Service

```powershell
# 发布
dotnet publish src/DesensitizeProxy.AspNetCore `
  -c Release -r win-x64 --self-contained false `
  -o artifacts/publish/win-x64

# 安装为 Windows Service（需管理员）
$InstallDir = "$env:ProgramFiles\DesensitizeProxy"
Copy-Item -Recurse artifacts/publish/win-x64 $InstallDir
& deploy/windows/install-service.ps1 -InstallDir $InstallDir

# 验证
Get-Service DesensitizePrivacyProxy
```

数据目录：`%LocalAppData%\ClawXRouter\PrivacyProxy\`
日志目录：未配置 `Runtime.LogDirectory` 时为 `DesensitizeProxy.AspNetCore.dll` 所在目录；显式配置后使用配置目录。

---

### Docker

#### 1. 构建镜像

```bash
cd locrouter
docker build -t desensitize-proxy .
```

#### 2. 运行容器

```bash
# 创建本地数据目录
mkdir -p deploy/data deploy/logs deploy/prompts

# 运行
docker run -d \
  --name desensitize-proxy \
  -p 127.0.0.1:8403:8403 \
  -e OPENAI_API_KEY="sk-你的key" \
  -e PrivacyProxy__Proxy__BindAddress=0.0.0.0 \
  -v $(pwd)/deploy/data:/data \
  -v $(pwd)/deploy/logs:/logs \
  desensitize-proxy
```

#### 3. 使用 Docker Compose

```bash
# 先设环境变量
export OPENAI_API_KEY="sk-你的key"

# 启动
docker compose -f deploy/docker-compose.yml up -d

# 查看日志
docker compose -f deploy/docker-compose.yml logs -f
```

#### Docker 中使用宿主机 Ollama

如果 Ollama 跑在宿主机上，容器内需要这样访问：

```yaml
# docker-compose.yml
environment:
  PrivacyProxy__LocalModel__Endpoint: "http://host.docker.internal:11434"
```

或者在 Linux 下使用 `--network host`：

```bash
docker run --network host \
  -e PrivacyProxy__LocalModel__Endpoint=http://localhost:11434 \
  ...
```

---

## 配置说明

完整配置位于 `src/DesensitizeProxy.AspNetCore/appsettings.json`，节点路径为 `PrivacyProxy`。所有配置支持热更新（通过 `IOptionsMonitor`）和环境变量覆盖（`PrivacyProxy__Section__Key` 格式）。

### 顶层开关

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `Enabled` | bool | `true` | 总开关，`false` 时直通不脱敏 |
| `StrictMode` | bool | `false` | 严格模式：LLM 脱敏失败时阻断请求 (502) 而非降级转发。即使 `LocalModel.Enabled=false`，StrictMode 下命中 Trigger/Hint 也会阻断 |
| `PromptPath` | string? | `null` | 自定义脱敏 Prompt 路径；`null` 使用内置默认 |
| `MaxBodySizeBytes` | long | `16777216` | 请求体大小上限 (16MiB) |
| `MaxTextPartLengthForLlm` | int | `8000` | 送入 LLM 脱敏的单段文本字符数上限 |

### LocalModel — 本地脱敏 LLM

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `Enabled` | bool | `false` | 是否启用 LLM 语义脱敏。`false` = 纯 Regex（默认，推荐先确保 Regex 链路正常后再开启） |
| `Type` | string | `"openai-compatible"` | 协议类型：`"openai-compatible"` / `"ollama-native"` / `"custom"` |
| `Provider` | string | `"ollama"` | 模型提供商标识 |
| `Model` | string | `"openbmb/minicpm4.1"` | 模型名称 |
| `Endpoint` | string | `"http://localhost:11434"` | LLM 服务地址 |
| `ApiKey` | string? | `null` | API Key（Ollama 通常无需） |
| `TimeoutMs` | int | `5000` | 单条脱敏超时 (ms) |
| `MaxConcurrency` | int | `4` | 并发脱敏上限 |

### Keywords — 规则引擎关键词

| 配置项 | 类型 | 说明 |
|--------|------|------|
| `TriggerKeywords` | string[] | 强信号（短语级），命中后必定追加 LLM 脱敏 |
| `HintKeywords` | string[] | 弱信号（单词级），需结合 Regex 命中判定 |

### Redaction — Regex 脱敏开关

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `ChinesePhone` | bool | `true` | 中国手机号 |
| `ChineseId` | bool | `true` | 中国身份证号 |
| `Email` | bool | `true` | 邮箱地址 |
| `InternalIp` | bool | `false` | 内网 IP |
| `CreditCard` | bool | `false` | 信用卡号 |
| `ChineseAddress` | bool | `false` | 中文地址 |
| `EnvVar` | bool | `false` | 环境变量值泄漏检测 |

Phase 1（硬编码）**始终运行**，不可关闭：SSH 私钥块、AWS Key、DB 连接串、快递单号、门禁密码。

### SystemMessageRedaction — System Prompt 脱敏

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `Phase1` | bool | `true` | 硬编码 Regex 对 system 运行 |
| `Phase2` | bool | `true` | 可配 Regex 对 system 运行 |
| `Phase3` | bool | `false` | 上下文关键词 Regex 对 system 运行 |
| `RuleEngine` | bool | `false` | 规则引擎判定对 system 运行 |
| `Llm` | bool | `false` | LLM 语义脱敏对 system 运行 |

### Observability — 观测与脱敏审计日志

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `MetricsEnabled` | bool | `true` | 是否启用指标采集 |
| `HealthCheckEnabled` | bool | `true` | 是否启用健康检查端点 |
| `HealthCheckPath` | string | `"/health"` | 健康检查路径 |
| `RedactionLoggingEnabled` | bool | `true` | 是否记录每次脱敏命中。打开后写入 `Runtime.LogDirectory/log/redactions-yyyy-MM-dd.log`，每行一条 JSON |
| `RedactionLogIncludeValues` | bool | `true` | 是否把原始敏感值写入日志。默认开启后审计文件包含原值，需要按敏感日志保管 |

脱敏审计日志固定写入 `Runtime.LogDirectory/log/redactions-yyyy-MM-dd.log`。如果未配置 `Runtime.LogDirectory`，则写入 `DesensitizeProxy.AspNetCore.dll` 所在目录下的 `log/redactions-yyyy-MM-dd.log`。文件名日期和 `timestamp` 均使用北京时间 (`+08:00`)。文件格式为 JSON Lines，字段包括 `timestamp`、`source`、`path`、`isSystem`、`phase`、`label`、`count`、`originalValue`、`redactedValue`。

### Proxy — 上游转发目标

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `Port` | int | `8403` | 代理监听端口 |
| `BindAddress` | string | `"127.0.0.1"` | 绑定地址 |
| `DefaultTarget` | string? | `null` | 默认上游目标 key。请求协议没有匹配到 target 时使用 |
| `Targets` | dict | `{}` | 上游目标字典。推荐一个协议一个 target，例如 `openai` / `anthropic` / `gemini` |
| `Targets[].BaseUrl` | string | (必填) | 上游 API 地址 |
| `Targets[].ApiKey` | string | (必填) | API Key，支持 `${ENV_VAR}` 占位符 |
| `Targets[].Provider` | string? | — | 协议类型：`"openai"` / `"openai-compatible"` / `"anthropic"` / `"google"` / `"gemini"` / `"vertex"` |

上游选择规则：先根据客户端请求协议/路径匹配 target；没有匹配到协议 target 时使用 `DefaultTarget`。`model` 字段只作为转发给上游的模型名，不用于选择 target。

### 环境变量覆盖

ASP.NET Core Options 模式支持通过环境变量覆盖任意配置项，使用 `__`（双下划线）分隔层级：

```bash
export PrivacyProxy__Enabled=true
export PrivacyProxy__LocalModel__Endpoint=http://localhost:11435
export PrivacyProxy__Proxy__Port=8403
export PrivacyProxy__Proxy__DefaultTarget=openai
export PrivacyProxy__Proxy__Targets__openai__ApiKey=sk-xxx
export PrivacyProxy__Proxy__Targets__gemini__ApiKey=your-gemini-key
export PrivacyProxy__Observability__RedactionLoggingEnabled=true
# 谨慎使用：true 会把原始敏感值写入 log/redactions-yyyy-MM-dd.log
export PrivacyProxy__Observability__RedactionLogIncludeValues=true
```

---

## 使用示例

### 基本使用

代理启动后监听 `http://127.0.0.1:8403`，客户端只需把请求指向代理即可：

```bash
# 通过代理调用 OpenAI
curl http://127.0.0.1:8403/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-4o",
    "messages": [
      {"role": "user", "content": "你好，我叫张三，手机号13912345678，地址是北京市朝阳区xx路xx号"}
    ]
  }'
```

代理会将请求中的 PII 替换后转发到上游：

```
实际转发内容:
"你好，我叫[REDACTED:NAME]，手机号[REDACTED:PHONE]，地址是[REDACTED:ADDRESS]"
```

### 按协议配置上游

一个协议配置一个 target 即可。代理先根据请求协议选择 target；匹配不到时回退到 `DefaultTarget`。客户端请求体里的 `model` 会原样传给上游，不会被配置覆盖，也不会用来选择 target。

```json
{
  "Proxy": {
    "DefaultTarget": "openai",
    "Targets": {
      "openai": {
        "BaseUrl": "https://api.openai.com/v1",
        "ApiKey": "${OPENAI_API_KEY}",
        "Provider": "openai"
      },
      "anthropic": {
        "BaseUrl": "https://api.anthropic.com/v1",
        "ApiKey": "${ANTHROPIC_API_KEY}",
        "Provider": "anthropic"
      },
      "gemini": {
        "BaseUrl": "https://generativelanguage.googleapis.com",
        "ApiKey": "${GEMINI_API_KEY}",
        "Provider": "gemini"
      },
      "openai-compatible-gateway": {
        "BaseUrl": "https://your-gateway.example.com/v1",
        "ApiKey": "${GATEWAY_API_KEY}",
        "Provider": "openai-compatible"
      }
    }
  }
}
```

- OpenAI-compatible 请求（例如 `/v1/responses`、`/v1/chat/completions`）→ 匹配 `Provider: "openai"` 或 `"openai-compatible"`
- Anthropic 原生请求（例如 `/v1/messages`）→ 匹配 `Provider: "anthropic"`
- Gemini / Vertex 原生请求（例如 `generateContent` 或 body 含 `contents` / `systemInstruction`）→ 匹配 `Provider: "gemini"` / `"google"` / `"vertex"`
- 协议没有匹配到任何 target → 使用 `DefaultTarget`

### Gemini 原生路径

Gemini 只有在客户端使用 Gemini 原生路径或 Gemini 原生 body 时才会走 `gemini` target。下面的请求会匹配 `Provider: "gemini"`：

```bash
curl http://127.0.0.1:8403/v1beta/models/gemini-2.5-flash:generateContent \
  -H "Content-Type: application/json" \
  -d '{
    "contents": [{"parts": [{"text": "我的手机号是13912345678"}]}]
  }'
```

如果客户端仍使用 OpenAI-compatible 请求格式，只是把 `model` 写成 Gemini 模型名，代理会按 OpenAI-compatible 协议选择 `openai` / `openai-compatible` target，不会走 `gemini` target。

### 模型列表和无请求体接口

`GET /v1/models`、`GET /v1beta/models` 这类没有 JSON body 的控制类接口不会进入脱敏流程，代理会按协议选择 target 后直接转发，并自动补上配置中的上游鉴权头。

```bash
curl http://127.0.0.1:8403/v1/models
```

这个请求会匹配 OpenAI-compatible 协议，转发到 `Provider: "openai"` 或 `"openai-compatible"` 的 target。

---

## 健康检查 & 可观测性

### 健康检查

```bash
curl http://127.0.0.1:8403/health
```

响应示例：

```json
// 正常
{
  "status": "healthy",
  "regex": "ok",
  "llm": "connected",
  "llm_model": "openbmb/minicpm4.1",
  "cache_entries": 42
}

// LLM 不可用
{
  "status": "degraded",
  "regex": "ok",
  "llm": "timeout",
  "llm_endpoint": "http://localhost:11434"
}
```

### Metrics

代理提供以下 Prometheus 风格指标：

| 指标名 | 类型 | 说明 |
|--------|------|------|
| `desensitize_requests_total` | Counter | 总请求数 |
| `regex_hits_total` | Counter | Regex 命中次数 (label: `phase`) |
| `llm_desensitize_calls_total` | Counter | LLM 脱敏调用次数 |
| `llm_desensitize_duration_seconds` | Histogram | LLM 脱敏耗时分布 |
| `llm_failures_total` | Counter | LLM 脱敏失败次数 (label: `reason`) |
| `llm_cache_hits_total` | Counter | 缓存命中次数 |
| `strict_mode_blocks_total` | Counter | StrictMode 阻断次数 |

---

## 编译 & 发布

### 编译

```bash
# 还原 + 编译
dotnet restore
dotnet build -c Release

# 运行测试
dotnet test
```

### 单平台发布

```bash
# macOS ARM (M 系列)
dotnet publish src/DesensitizeProxy.AspNetCore \
  -c Release -r osx-arm64 --self-contained false \
  -o artifacts/publish/osx-arm64

# macOS x64
dotnet publish src/DesensitizeProxy.AspNetCore \
  -c Release -r osx-x64 --self-contained false \
  -o artifacts/publish/osx-x64

# Linux x64
dotnet publish src/DesensitizeProxy.AspNetCore \
  -c Release -r linux-x64 --self-contained false \
  -o artifacts/publish/linux-x64

# Windows x64
dotnet publish src/DesensitizeProxy.AspNetCore \
  -c Release -r win-x64 --self-contained false \
  -o artifacts/publish/win-x64
```

### 全平台批量发布

```bash
bash scripts/publish-all.sh
```

产物输出在 `artifacts/publish/` 下按 RID 分目录：
`osx-arm64` | `osx-x64` | `linux-x64` | `linux-arm64` | `win-x64` | `win-arm64`

---

## 常用场景配置

### 仅 Regex 脱敏（不跑本地 LLM）

```json
{
  "LocalModel": {
    "Enabled": false
  }
}
```

### 高安全要求（脱敏失败即阻断）

```json
{
  "StrictMode": true
}
```

### 使用不同的 Ollama 模型

```json
{
  "LocalModel": {
    "Model": "qwen2.5:0.5b",
    "Endpoint": "http://localhost:11434"
  }
}
```

### 自定义脱敏 Prompt

```json
{
  "PromptPath": "/path/to/your/pii-extraction.md"
}
```

Prompt 优先级：配置绝对路径 > `{ContentRoot}/prompts/pii-extraction.md` > 内置默认。

### 增加额外触发关键词

```json
{
  "Keywords": {
    "TriggerKeywords": [
      "自定义敏感词", "工号", "车牌号",
      "...以及其他默认 Trigger 关键词..."
    ]
  }
}
```

---

## 项目结构

```
locrouter/
├── DesensitizeProxy.slnx               # 解决方案文件
├── Dockerfile                          # Docker 镜像构建
├── README.md
├── deploy/
│   ├── docker-compose.yml              # Docker Compose 编排
│   ├── launchd/
│   │   └── com.clawxrouter.privacy-proxy.plist   # macOS 服务
│   ├── systemd/
│   │   └── desensitize-proxy.service              # Linux systemd
│   └── windows/
│       └── install-service.ps1                    # Windows Service
├── docs/
│   └── dotnet-privacy-router-design-final.md      # 详细设计文档
├── scripts/
│   └── publish-all.sh                  # 全平台批量发布
├── src/
│   ├── DesensitizeProxy.Core/          # 核心逻辑
│   │   ├── Abstractions/               # 接口定义
│   │   ├── Engine/                     # Regex 引擎 & 规则引擎
│   │   ├── Llm/                        # LLM 脱敏 & 缓存 & Prompt
│   │   ├── Models/                     # 配置 & 结果模型
│   │   ├── Redaction/                  # PII 脱敏 & Schema 清洗
│   │   ├── Runtime/                    # 跨平台目录解析
│   │   └── Extensions/                 # DI 注册
│   └── DesensitizeProxy.AspNetCore/    # ASP.NET Core 宿主
│       ├── Auth/                       # Multi-auth 鉴权
│       ├── Health/                     # 健康检查端点
│       ├── Middleware/                  # 主中间件 & 文本抽取
│       ├── Yarp/                       # YARP 转发 & Provider 转换
│       ├── appsettings.json            # 默认配置
│       └── Program.cs                  # 入口
└── tests/
    └── DesensitizeProxy.Core.Tests/    # 单元测试 & 集成测试
```

---

## 失败降级矩阵

| 场景 | Regex 命中 | LLM 可用 | StrictMode=false | StrictMode=true |
|------|-----------|---------|------------------|-----------------|
| 常规请求 | ✅ / ❌ | ✅ | 正常脱敏转发 | 正常脱敏转发 |
| LLM 挂了 | ✅ | ❌ | Regex-only 转发 + 告警 ✅ | 命中 Trigger/Hint → 502 ❌ |
| LLM 挂了 + 无 Regex | ❌ | ❌ | 转发 + 高优告警 | 命中 Trigger/Hint → 502 ❌ |
| LLM 超时 | ✅ / ❌ | 部分 | Regex-only 转发 | 命中 Trigger/Hint → 502 |
| 本地模型禁用 | ✅ / ❌ | 主动关 | Regex-only 转发 | 命中 Trigger/Hint → 502 |
| 文本超长 | ✅ | — | Regex-only 转发 + 告警 | 命中 Trigger/Hint → 413 |

默认 **StrictMode=false**，推荐生产环境使用。
