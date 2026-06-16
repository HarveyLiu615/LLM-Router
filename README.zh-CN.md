# DesensitizeProxy

[English](README.md)

> 本地优先的 PII 脱敏代理。在请求发送到云端 LLM 之前，先用 Regex 和可选本地小模型识别并替换手机号、身份证、邮箱、地址、密钥等敏感信息。

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![YARP](https://img.shields.io/badge/YARP-2.3-blue)](https://microsoft.github.io/reverse-proxy/)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

DesensitizeProxy 是一个基于 ASP.NET Core 和 YARP 的隐私保护反向代理。你的客户端仍然可以使用 OpenAI-compatible、DeepSeek、Anthropic、Gemini 或 Vertex 风格接口；代理会在请求转发到上游 LLM 前，先改写请求体中的文本内容，并按目标提供商自动补齐鉴权头。

```text
客户端 -> Regex 脱敏 -> 规则引擎 -> 可选本地 LLM 脱敏 -> Provider 转换 -> 上游 LLM
```

默认策略偏向可用性：Regex 始终先运行；本地 LLM 不可用时，非 `StrictMode` 会降级为 Regex-only 转发。高安全场景可以开启 `StrictMode`，让命中敏感信号但无法完成语义脱敏的请求直接失败。

## 功能特性

- **本地优先脱敏**：Regex 规则先行，覆盖手机号、身份证、邮箱、SSH 私钥、AWS Key、数据库连接串、快递单号、门禁码等。
- **可选语义脱敏**：通过 Ollama 或 OpenAI-compatible 本地模型提取 PII JSON，再由代码执行替换。
- **失败安全边界**：LLM 脱敏结果写回前会再次运行 Regex，避免恢复已脱敏内容。
- **多协议上游**：支持 OpenAI-compatible、DeepSeek、Anthropic、Gemini、Vertex 请求转发与鉴权头转换。
- **规则引擎分级**：根据 Trigger / Hint / None 判断是否需要调用本地 LLM，降低无意义模型调用。
- **跨平台部署**：支持 Docker、Docker Compose、systemd、launchd、Windows Service 和多 RID 发布。
- **可观测性**：提供 `/health`、Prometheus 风格指标、JSON Lines 脱敏审计日志。

## 目录

- [快速开始](#快速开始)
- [工作原理](#工作原理)
- [配置说明](#配置说明)
- [使用示例](#使用示例)
- [上游路由](#上游路由)
- [健康检查与可观测性](#健康检查与可观测性)
- [部署](#部署)
- [开发](#开发)
- [项目结构](#项目结构)
- [安全说明](#安全说明)
- [许可证](#许可证)

## 快速开始

### 前置依赖

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Ollama 或其他 OpenAI-compatible 本地模型端点，可选但推荐用于语义脱敏
- 至少一个上游 LLM API Key

### 本地运行

```bash
git clone <repo-url>
cd locrouter

dotnet restore
dotnet build -c Release
```

通过环境变量配置上游目标：

```bash
export PrivacyProxy__Proxy__DefaultTarget=openai
export PrivacyProxy__Proxy__Targets__openai__BaseUrl=https://api.openai.com/v1
export PrivacyProxy__Proxy__Targets__openai__ApiKey="$OPENAI_API_KEY"
export PrivacyProxy__Proxy__Targets__openai__Provider=openai
```

如果本地测试时不需要语义脱敏，可以关闭本地模型：

```bash
export PrivacyProxy__LocalModel__Enabled=false
```

启动代理：

```bash
dotnet run --project src/DesensitizeProxy.AspNetCore
```

默认监听地址为 `http://127.0.0.1:8403`。

### 使用 Docker Compose

```bash
export OPENAI_API_KEY="sk-your-key"
docker compose -f deploy/docker-compose.yml up -d
```

Docker Compose 会把代理发布到 `127.0.0.1:8403`，并挂载本地 `deploy/data`、`deploy/logs` 和 `deploy/prompts` 目录。

## 工作原理

```text
收到请求
  |
  |-- 从 chat / responses / provider-native JSON 中抽取文本片段
  |-- Phase 1 Regex: 内置高置信度密钥规则，始终启用
  |-- Phase 2 Regex: 可配置常见 PII 规则，默认启用
  |-- Phase 3 Regex: 上下文关键词规则，会跳过 code fence
  |-- 规则引擎: 基于原文判断 Trigger / Hint / None
  |-- 需要且已启用时调用本地 LLM 做语义脱敏
  |-- 对 LLM 输出再次执行 Regex 兜底
  |-- Schema 清洗和 Provider 转换
  v
通过 YARP 转发到上游提供商
```

代理不会存储会话，也不会在本地生成回答。它只负责请求体脱敏、Provider-specific 转换、鉴权头注入和请求转发。

## 配置说明

所有配置都位于 `src/DesensitizeProxy.AspNetCore/appsettings.json` 的 `PrivacyProxy` 节点下。ASP.NET Core 环境变量覆盖使用双下划线分隔层级，例如 `PrivacyProxy__Proxy__Port=8403`。

### 最小上游配置

```json
{
  "PrivacyProxy": {
    "Proxy": {
      "Port": 8403,
      "BindAddress": "127.0.0.1",
      "DefaultTarget": "openai",
      "Targets": {
        "openai": {
          "BaseUrl": "https://api.openai.com/v1",
          "ApiKey": "${OPENAI_API_KEY}",
          "Provider": "openai"
        }
      }
    }
  }
}
```

### 常用配置项

| 配置项 | 默认值 | 说明 |
|---|---:|---|
| `Enabled` | `true` | 总开关。关闭后请求直通，不做脱敏。 |
| `StrictMode` | `false` | 当请求需要语义脱敏但无法完成时，直接阻断请求。 |
| `PromptPath` | `null` | 自定义 PII 提取 Prompt 路径。 |
| `MaxBodySizeBytes` | `16777216` | 请求体大小上限，默认 16 MiB。 |
| `MaxTextPartLengthForLlm` | `8000` | 单段文本送入本地模型的最大长度。 |
| `LocalModel.Enabled` | `true` | 是否启用本地模型语义脱敏。 |
| `LocalModel.Endpoint` | `http://localhost:11434` | 本地模型端点。 |
| `LocalModel.Model` | `openbmb/minicpm4.1` | 发送给本地模型端点的模型名。 |
| `Proxy.Port` | `8403` | 本地代理端口。 |
| `Proxy.BindAddress` | `127.0.0.1` | 本地绑定地址。 |
| `Observability.HealthCheckPath` | `/health` | 健康检查路径。 |

### Regex 脱敏开关

```json
{
  "PrivacyProxy": {
    "Redaction": {
      "ChinesePhone": true,
      "ChineseId": true,
      "Email": true,
      "InternalIp": false,
      "CreditCard": false,
      "ChineseAddress": false,
      "EnvVar": false
    }
  }
}
```

Phase 1 内置规则不可关闭且始终运行。它覆盖私钥块、云访问密钥、数据库连接串、快递单号、门禁码等低误报敏感信息。

### 本地模型

Ollama 示例：

```bash
ollama serve
ollama pull openbmb/minicpm4.1

export PrivacyProxy__LocalModel__Enabled=true
export PrivacyProxy__LocalModel__Type=openai-compatible
export PrivacyProxy__LocalModel__Provider=ollama
export PrivacyProxy__LocalModel__Endpoint=http://localhost:11434
export PrivacyProxy__LocalModel__Model=openbmb/minicpm4.1
```

仅使用 Regex 脱敏：

```bash
export PrivacyProxy__LocalModel__Enabled=false
```

开启严格模式：

```bash
export PrivacyProxy__StrictMode=true
```

## 使用示例

把你的 LLM 客户端地址指向代理，而不是直接请求上游提供商。

```bash
curl http://127.0.0.1:8403/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-4o-mini",
    "messages": [
      {
        "role": "user",
        "content": "我叫张三，手机号 13912345678，邮箱 zhangsan@example.com。"
      }
    ]
  }'
```

上游会收到类似下面的脱敏内容：

```text
我叫[REDACTED:NAME]，手机号 [REDACTED:PHONE]，邮箱 [REDACTED:EMAIL]。
```

没有 JSON 请求体的控制类接口，例如 `GET /v1/models`，不会进入脱敏流程，会直接按协议选择上游并补齐鉴权后转发。

```bash
curl http://127.0.0.1:8403/v1/models
```

## 上游路由

代理会根据请求协议和路径选择上游 target。请求体中的 `model` 字段会原样转发给上游，但不会作为 target key 使用。

```json
{
  "PrivacyProxy": {
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
        }
      }
    }
  }
}
```

路由规则：

- OpenAI-compatible 路径，例如 `/v1/chat/completions` 和 `/v1/responses`，匹配 provider 为 `openai` 或 `openai-compatible` 的 target。
- DeepSeek-compatible endpoint 应按 OpenAI-compatible target 配置。官方 DeepSeek host 会自动启用 Responses 到 Chat Completions 的兼容转换。多模型网关可用 `ResponsesCompatibilityRules` 按模型启用，例如 `{ "ModelPattern": "deepseek-*", "Mode": "deepseek-chat-completions" }`。未命中的模型保持原生转发，所以在 Codex 或 Claude Code 里切换模型不需要修改代理配置。
- Anthropic 原生路径，例如 `/v1/messages`，匹配 `anthropic` target。
- Gemini 原生路径，例如 `/v1beta/models/gemini-2.5-flash:generateContent`，匹配 `gemini`、`google` 或 `vertex` target。
- 没有匹配到协议 target 时，使用 `DefaultTarget`。

OpenAI-compatible 多模型网关示例：

```json
{
  "BaseUrl": "https://gateway.example/v1",
  "ApiKey": "${GATEWAY_API_KEY}",
  "Provider": "openai-compatible",
  "ResponsesCompatibility": "native",
  "ResponsesCompatibilityRules": [
    { "ModelPattern": "deepseek-*", "Mode": "deepseek-chat-completions" },
    { "ModelPattern": "deepseek/*", "Mode": "deepseek-chat-completions" }
  ]
}
```

Gemini 原生请求示例：

```bash
curl http://127.0.0.1:8403/v1beta/models/gemini-2.5-flash:generateContent \
  -H "Content-Type: application/json" \
  -d '{
    "contents": [
      {"parts": [{"text": "我的手机号是 13912345678"}]}
    ]
  }'
```

## 健康检查与可观测性

### 健康检查

```bash
curl http://127.0.0.1:8403/health
```

正常响应示例：

```json
{
  "status": "healthy",
  "regex": "ok",
  "llm": "connected",
  "llm_model": "openbmb/minicpm4.1",
  "cache_entries": 42
}
```

当本地模型关闭或不可用时，健康状态可能是 degraded，但 Regex 脱敏仍然可用。

### 指标

内置指标采集器会跟踪：

| 指标 | 类型 | 说明 |
|---|---|---|
| `desensitize_requests_total` | Counter | 代理请求总数。 |
| `regex_hits_total` | Counter | Regex 命中次数，按 phase 打标签。 |
| `llm_desensitize_calls_total` | Counter | 本地模型脱敏调用次数。 |
| `llm_desensitize_duration_seconds` | Histogram | 本地模型脱敏耗时。 |
| `llm_failures_total` | Counter | 本地模型失败次数，按 reason 打标签。 |
| `llm_cache_hits_total` | Counter | 语义脱敏缓存命中次数。 |
| `strict_mode_blocks_total` | Counter | 被 StrictMode 阻断的请求数。 |

### 脱敏审计日志

当 `PrivacyProxy__Observability__RedactionLoggingEnabled=true` 时，审计记录会以 JSON Lines 写入：

```text
<Runtime.LogDirectory>/log/redactions-yyyy-MM-dd.log
```

如果审计日志不能包含原始敏感值，请设置 `PrivacyProxy__Observability__RedactionLogIncludeValues=false`。

## 部署

### 发布单个平台

```bash
dotnet publish src/DesensitizeProxy.AspNetCore \
  -c Release \
  -r linux-x64 \
  --self-contained false \
  -o artifacts/publish/linux-x64
```

支持的发布目标包括 `osx-arm64`、`osx-x64`、`linux-x64`、`linux-arm64`、`win-x64` 和 `win-arm64`。

### 发布所有平台

```bash
bash scripts/publish-all.sh
```

### Docker

```bash
docker build -t desensitize-proxy .

docker run -d \
  --name desensitize-proxy \
  -p 127.0.0.1:8403:8403 \
  -e PrivacyProxy__Proxy__BindAddress=0.0.0.0 \
  -e PrivacyProxy__Proxy__Targets__openai__BaseUrl=https://api.openai.com/v1 \
  -e PrivacyProxy__Proxy__Targets__openai__ApiKey="$OPENAI_API_KEY" \
  -e PrivacyProxy__Proxy__Targets__openai__Provider=openai \
  desensitize-proxy
```

如果 Ollama 运行在宿主机，而代理运行在 Docker 中，在 Docker Desktop 上可以把本地模型端点设为 `http://host.docker.internal:11434`。Linux 下请使用容器可访问的宿主机地址，或使用 host networking。

### 系统服务

`deploy/` 下包含服务模板：

- `deploy/systemd/desensitize-proxy.service`：Linux systemd
- `deploy/launchd/com.clawxrouter.privacy-proxy.plist`：macOS launchd
- `deploy/windows/install-service.ps1`：Windows Service 安装脚本

运行时目录默认按平台选择，也可以通过 `PrivacyProxy__Runtime__DataDirectory` 和 `PrivacyProxy__Runtime__LogDirectory` 覆盖。

## 开发

```bash
# 还原与编译
dotnet restore
dotnet build DesensitizeProxy.slnx

# 运行测试
dotnet test tests/DesensitizeProxy.Core.Tests/DesensitizeProxy.Core.Tests.csproj

# 使用临时端口启动 Web Host
PrivacyProxy__Proxy__Port=18403 \
PrivacyProxy__LocalModel__Enabled=false \
dotnet run --project src/DesensitizeProxy.AspNetCore
```

相关文档：

- [详细设计](docs/dotnet-privacy-router-design-final.md)
- [实现审计](docs/implementation-audit.md)

## 项目结构

```text
locrouter/
├── src/
│   ├── DesensitizeProxy.AspNetCore/   # ASP.NET Core 宿主、中间件、健康检查、YARP 转发
│   └── DesensitizeProxy.Core/         # 脱敏引擎、规则引擎、本地 LLM 客户端、模型
├── tests/                             # 单元测试与集成测试
├── deploy/                            # Docker Compose 与系统服务模板
├── docs/                              # 设计与实现说明
├── scripts/                           # 发布辅助脚本
├── Dockerfile
├── DesensitizeProxy.slnx
└── README.md
```

## 安全说明

- 不要提交真实上游 API Key。优先使用环境变量或本地密钥管理工具。
- 如果 `RedactionLogIncludeValues=true`，请把脱敏审计日志视为敏感日志保管。
- `StrictMode=false` 偏向可用性。如果不接受不完整脱敏文本被转发，请使用 `StrictMode=true`。
- 除非已经放在自己的访问控制之后，否则代理建议只监听 `127.0.0.1`。

## 许可证

本项目使用 [MIT License](LICENSE)。
