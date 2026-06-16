# DesensitizeProxy

[Chinese documentation](README.zh-CN.md)

> A local-first PII redaction proxy. Before requests are sent to a cloud LLM, it detects and replaces sensitive content such as phone numbers, national IDs, email addresses, physical addresses, and secret keys with Regex rules and an optional local model.

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![YARP](https://img.shields.io/badge/YARP-2.3-blue)](https://microsoft.github.io/reverse-proxy/)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

DesensitizeProxy is a privacy-preserving reverse proxy built with ASP.NET Core and YARP. Clients can keep using OpenAI-compatible, DeepSeek, Anthropic, Gemini, or Vertex-style APIs. The proxy rewrites textual request bodies before forwarding them and injects the correct authentication headers for each upstream provider.

```text
Client -> Regex redaction -> Rule engine -> Optional local LLM redaction -> Provider transform -> Upstream LLM
```

The default policy favors availability: Regex redaction always runs first; if the local LLM is unavailable, non-StrictMode requests fall back to Regex-only forwarding. For high-security deployments, enable `StrictMode` so requests with sensitive signals fail when semantic redaction cannot complete.

## Features

- **Local-first redaction**: Regex rules run first and cover phone numbers, national IDs, email addresses, SSH private keys, AWS keys, database connection strings, delivery tracking numbers, access codes, and more.
- **Optional semantic redaction**: Use Ollama or another OpenAI-compatible local model to extract PII as JSON, then let application code perform deterministic replacement.
- **Fail-safe boundary**: Regex redaction runs again before LLM-redacted text is written back, preventing already-redacted content from being restored.
- **Multi-provider forwarding**: Supports OpenAI-compatible, DeepSeek, Anthropic, Gemini, and Vertex request forwarding with provider-specific authentication transforms.
- **Rule-engine triage**: Uses Trigger / Hint / None decisions to call the local LLM only when it is useful.
- **Cross-platform deployment**: Supports Docker, Docker Compose, systemd, launchd, Windows Service, and multi-RID publishing.
- **Observability**: Provides `/health`, Prometheus-style metrics, and JSON Lines redaction audit logs.

## Table of Contents

- [Quick Start](#quick-start)
- [How It Works](#how-it-works)
- [Configuration](#configuration)
- [Usage](#usage)
- [Provider Routing](#provider-routing)
- [Health and Observability](#health-and-observability)
- [Deployment](#deployment)
- [Development](#development)
- [Project Structure](#project-structure)
- [Security Notes](#security-notes)
- [License](#license)

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Ollama or another OpenAI-compatible local model endpoint, optional but recommended for semantic redaction
- At least one upstream LLM API key

### Run locally

```bash
git clone <repo-url>
cd locrouter

dotnet restore
dotnet build -c Release
```

Configure the upstream target through environment variables:

```bash
export PrivacyProxy__Proxy__DefaultTarget=openai
export PrivacyProxy__Proxy__Targets__openai__BaseUrl=https://api.openai.com/v1
export PrivacyProxy__Proxy__Targets__openai__ApiKey="$OPENAI_API_KEY"
export PrivacyProxy__Proxy__Targets__openai__Provider=openai
```

If you do not want semantic redaction during local testing, disable the local model:

```bash
export PrivacyProxy__LocalModel__Enabled=false
```

Start the proxy:

```bash
dotnet run --project src/DesensitizeProxy.AspNetCore
```

The proxy listens on `http://127.0.0.1:8403` by default.

### Run with Docker Compose

```bash
export OPENAI_API_KEY="sk-your-key"
docker compose -f deploy/docker-compose.yml up -d
```

Docker Compose publishes the proxy at `127.0.0.1:8403` and mounts local `deploy/data`, `deploy/logs`, and `deploy/prompts` directories.

## How It Works

```text
Incoming request
  |
  |-- Extract text parts from chat / responses / provider-native JSON
  |-- Phase 1 Regex: built-in high-confidence secrets, always enabled
  |-- Phase 2 Regex: configurable common PII, enabled by default
  |-- Phase 3 Regex: context-keyword rules, skips code fences
  |-- Rule engine: Trigger / Hint / None on original text
  |-- Local LLM redaction when required and enabled
  |-- Regex fallback on LLM output
  |-- Schema cleanup and provider transform
  v
Forward to upstream provider through YARP
```

The proxy never stores conversations or generates answers locally. Its job is limited to request-body redaction, provider-specific transformation, authentication header injection, and forwarding.

## Configuration

All options live under the `PrivacyProxy` section in `src/DesensitizeProxy.AspNetCore/appsettings.json`. ASP.NET Core environment variable overrides use double underscores, for example `PrivacyProxy__Proxy__Port=8403`.

### Minimal upstream target

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

### Common options

| Option | Default | Description |
|---|---:|---|
| `Enabled` | `true` | Global switch. When disabled, requests pass through without redaction. |
| `StrictMode` | `false` | Blocks requests when semantic redaction is required but cannot complete. |
| `PromptPath` | `null` | Optional custom prompt path for PII extraction. |
| `MaxBodySizeBytes` | `16777216` | Maximum request body size, 16 MiB by default. |
| `MaxTextPartLengthForLlm` | `8000` | Maximum single text part length sent to the local model. |
| `LocalModel.Enabled` | `true` | Enables semantic redaction through the configured local model. |
| `LocalModel.Endpoint` | `http://localhost:11434` | Local model endpoint. |
| `LocalModel.Model` | `openbmb/minicpm4.1` | Model name sent to the local model endpoint. |
| `Proxy.Port` | `8403` | Local proxy port. |
| `Proxy.BindAddress` | `127.0.0.1` | Local bind address. |
| `Observability.HealthCheckPath` | `/health` | Health check endpoint. |

### Redaction switches

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

Phase 1 built-in rules are not configurable and always run. They cover low-false-positive secrets such as private key blocks, cloud access keys, database connection strings, delivery tracking numbers, and access codes.

### Local model

Ollama example:

```bash
ollama serve
ollama pull openbmb/minicpm4.1

export PrivacyProxy__LocalModel__Enabled=true
export PrivacyProxy__LocalModel__Type=openai-compatible
export PrivacyProxy__LocalModel__Provider=ollama
export PrivacyProxy__LocalModel__Endpoint=http://localhost:11434
export PrivacyProxy__LocalModel__Model=openbmb/minicpm4.1
```

Regex-only mode:

```bash
export PrivacyProxy__LocalModel__Enabled=false
```

Strict mode:

```bash
export PrivacyProxy__StrictMode=true
```

## Usage

Point your LLM client at the proxy instead of the upstream provider.

```bash
curl http://127.0.0.1:8403/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-4o-mini",
    "messages": [
      {
        "role": "user",
        "content": "My name is Alice Zhang. My phone number is 13912345678 and my email is alice@example.com."
      }
    ]
  }'
```

The upstream receives redacted content similar to:

```text
My name is [REDACTED:NAME]. My phone number is [REDACTED:PHONE] and my email is [REDACTED:EMAIL].
```

Control-plane endpoints without JSON request bodies, such as `GET /v1/models`, bypass redaction and are forwarded with the configured upstream authentication.

```bash
curl http://127.0.0.1:8403/v1/models
```

## Provider Routing

The proxy selects an upstream target from the request protocol and path. The `model` field is forwarded to the upstream provider, but it is not used as a target key.

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
        "deepseek": {
          "BaseUrl": "https://api.deepseek.com/v1",
          "ApiKey": "${DEEPSEEK_API_KEY}",
          "Provider": "deepseek"
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

Routing rules:

- OpenAI-compatible paths such as `/v1/chat/completions` and `/v1/responses` match `openai`, `openai-compatible`, or `deepseek` targets.
- DeepSeek targets use Bearer authentication and forward Chat Completions requests as-is. For `/v1/responses`, the request body is converted to `/chat/completions` shape before forwarding, including `thinking.type`, `reasoning_effort`, and tool-call `reasoning_content` carry-over required by DeepSeek thinking mode; responses remain upstream-native.
- Anthropic native paths such as `/v1/messages` match `anthropic` targets.
- Gemini native paths such as `/v1beta/models/gemini-2.5-flash:generateContent` match `gemini`, `google`, or `vertex` targets.
- When no protocol-specific target matches, `DefaultTarget` is used.

Gemini native example:

```bash
curl http://127.0.0.1:8403/v1beta/models/gemini-2.5-flash:generateContent \
  -H "Content-Type: application/json" \
  -d '{
    "contents": [
      {"parts": [{"text": "My phone number is 13912345678."}]}
    ]
  }'
```

## Health and Observability

### Health check

```bash
curl http://127.0.0.1:8403/health
```

Healthy response example:

```json
{
  "status": "healthy",
  "regex": "ok",
  "llm": "connected",
  "llm_model": "openbmb/minicpm4.1",
  "cache_entries": 42
}
```

When the local model is disabled or unavailable, health can become degraded while Regex redaction remains available.

### Metrics

The built-in metrics collector tracks:

| Metric | Type | Description |
|---|---|---|
| `desensitize_requests_total` | Counter | Total proxied requests. |
| `regex_hits_total` | Counter | Regex hits, tagged by phase. |
| `llm_desensitize_calls_total` | Counter | Local model redaction calls. |
| `llm_desensitize_duration_seconds` | Histogram | Local model redaction latency. |
| `llm_failures_total` | Counter | Local model failures, tagged by reason. |
| `llm_cache_hits_total` | Counter | Semantic redaction cache hits. |
| `strict_mode_blocks_total` | Counter | Requests blocked by StrictMode. |

### Redaction audit log

When `PrivacyProxy__Observability__RedactionLoggingEnabled=true`, audit entries are written as JSON Lines to:

```text
<Runtime.LogDirectory>/log/redactions-yyyy-MM-dd.log
```

Set `PrivacyProxy__Observability__RedactionLogIncludeValues=false` if audit logs must not contain original sensitive values.

## Deployment

### Publish one platform

```bash
dotnet publish src/DesensitizeProxy.AspNetCore \
  -c Release \
  -r linux-x64 \
  --self-contained false \
  -o artifacts/publish/linux-x64
```

Supported publish targets include `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `win-x64`, and `win-arm64`.

### Publish all platforms

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

If Ollama runs on the host and the proxy runs in Docker, set the local model endpoint to `http://host.docker.internal:11434` on Docker Desktop. On Linux, use a reachable host address or host networking.

### Services

Service templates are included under `deploy/`:

- `deploy/systemd/desensitize-proxy.service` for Linux systemd
- `deploy/launchd/com.clawxrouter.privacy-proxy.plist` for macOS launchd
- `deploy/windows/install-service.ps1` for Windows Service installation

Runtime directories are platform-specific by default and can be overridden with `PrivacyProxy__Runtime__DataDirectory` and `PrivacyProxy__Runtime__LogDirectory`.

## Development

```bash
# Restore and build
dotnet restore
dotnet build DesensitizeProxy.slnx

# Run tests
dotnet test tests/DesensitizeProxy.Core.Tests/DesensitizeProxy.Core.Tests.csproj

# Run the web host on a temporary port
PrivacyProxy__Proxy__Port=18403 \
PrivacyProxy__LocalModel__Enabled=false \
dotnet run --project src/DesensitizeProxy.AspNetCore
```

Useful docs:

- [Detailed design](docs/dotnet-privacy-router-design-final.md)
- [Implementation audit](docs/implementation-audit.md)

## Project Structure

```text
locrouter/
├── src/
│   ├── DesensitizeProxy.AspNetCore/   # ASP.NET Core host, middleware, health, YARP forwarding
│   └── DesensitizeProxy.Core/         # Redaction engine, rule engine, local LLM client, models
├── tests/                             # Unit and integration tests
├── deploy/                            # Docker Compose and service templates
├── docs/                              # Design and implementation notes
├── scripts/                           # Release helpers
├── Dockerfile
├── DesensitizeProxy.slnx
└── README.md
```

## Security Notes

- Do not commit real upstream API keys. Prefer environment variables or a local secrets manager.
- Treat redaction audit logs as sensitive if `RedactionLogIncludeValues=true`.
- `StrictMode=false` favors availability. Use `StrictMode=true` when forwarding imperfectly redacted text is unacceptable.
- Run the proxy on `127.0.0.1` unless it is intentionally exposed behind your own access controls.

## License

This project is licensed under the [MIT License](LICENSE).
