# dotnet-privacy-router-design-final.md 实现审计

审计基准：`docs/dotnet-privacy-router-design-final.md`。状态含义：

- `proven`：已有当前源码、测试或命令结果直接证明。
- `partial`：主体已实现，但证据范围不足或与设计文档有差异。
- `missing`：当前实现缺失。

## 需求逐项核对

| 设计要求 | 状态 | 当前证据 | 缺口/动作 |
|---|---:|---|---|
| 纯 PII 脱敏代理：拦截消息、替换为 `[REDACTED:*]`、转发到云端，不做本地回答/模型路由/记忆隔离 | proven | `DesensitizeProxyMiddleware` 只改写请求体后交给 YARP；`UpstreamResolver` 只按请求 model/默认 target 选择上游；无本地回答或会话存储模块 | 无 |
| 技术栈：.NET 10、ASP.NET Core Middleware、YARP、Options 热更新、日志 | proven | `*.csproj` 均为 `net10.0`；`Program.cs` 注册 middleware/YARP；`PrivacyConfig` + `IOptionsMonitor`；结构化日志位于 middleware/LLM client；`ServiceRegistrationTests` 使用 scope validation 验证服务图；实际 `dotnet run` 启动 smoke 通过 | 无 |
| 本地脱敏 LLM 支持 HTTP OpenAI-compatible/Ollama-native 客户端，并提供 `Microsoft.Extensions.AI.Abstractions` + `IChatClient` 适配器 | proven | `LocalModelClient` 为默认 DI；`IChatClientPiiExtractionClient` 和测试存在；`LocalModelClientTests` 与 `IChatClientPiiExtractionClientTests` | 设计文档需同步说明默认路径是 HTTP client，IChatClient 是可注入适配器 |
| Regex 始终先运行，覆盖所有消息含 system | proven | `RunRegex()` 对所有 `TextPart` 运行；system 调 `RedactSystemWithHits()`；`PiiRedactorTests.RedactSystem_DefaultRunsPhase1AndPhase2ButSkipsPhase3` | 无 |
| Regex Phase 1 不可关闭，覆盖私钥、AWS Key、DB 连接、快递单号、门禁码 | proven | `GeneratedRegexes` + `PiiRedactor.RedactPhase1Only()`；`PiiRedactorTests.RedactWithHits_ReportsPhase1` 与 `RedactPhase1Only_CoversBuiltInLowFalsePositiveRules` | 无 |
| Regex Phase 2 默认开启且可配置关闭，覆盖手机号、身份证、邮箱 | proven | `RedactionConfig` 默认 true；`PiiRedactor.RedactPhase2()`；pipeline 测试验证手机号/邮箱转发前脱敏 | 无 |
| Regex Phase 3 上下文关键词、中文无分词 lookbehind、跳过 code fence | proven | `PiiRedactor.RedactContextKeywords()` + `RedactChineseKeyword()`；`PiiRedactorTests.Redact_DoesNotRedactContextInsideCodeFence`、`Redact_HandlesChineseLooseAndStrictKeywordRules`、`Redact_DoesNotUseChineseWordBoundaryLookbehind` | 无 |
| Regex 不可回退：LLM 结果写回前必须再跑 Regex | proven | `RunLlmAsync()` 成功后 `finalRedacted = _piiRedactor.Redact(result.RedactedContent)`；`IntegrationTests.LlmResultThenRegexFallback_DoesNotRestoreRegexDetectedPii` | 无 |
| 规则引擎基于原始文本，区分 Trigger/Hint/None | proven | `originalTexts` 在 Regex 前保存；`RuleEngine.Check()`；`RuleEngineTests` 覆盖三档 | 无 |
| Trigger、Hint+Regex、Hint+StrictMode 收集 LLM 队列 | proven | `ShouldRunLlm()`；`HttpPipelineTests.Middleware_StrictModeBlocksWhenLocalModelDisabledAndTriggerHit` 与 `Middleware_StrictModeBlocksHintWhenLocalModelDisabled` | 无 |
| 长文本不截断；超过 `MaxTextPartLengthForLlm` 时 StrictMode + Trigger/Hint 阻断，否则 Regex-only | proven | `BuildLlmQueue()` 长度检查返回 413；`Middleware_StrictModeReturns413WhenTriggeredTextPartIsTooLongForLlm` | 无 |
| StrictMode 强契约：Trigger/Hint 下任何非 `Success` 状态阻断 | proven | `RunLlmAsync()` 检查 `result.Status != Success`；`LlmDesensitizerTests` 覆盖 Parse/Empty/Invalid 状态；`Middleware_StrictModeBlocksWhenLlmReturnsParseFailure` | 无 |
| LocalModel.Enabled=false 降级/阻断矩阵 | proven | `RunLlmAsync()`；`HttpPipelineTests.Middleware_StrictModeBlocksWhenLocalModelDisabledAndTriggerHit` | 无 |
| LLM 两步法：模型只提取 JSON，代码按 value 长度降序替换 | proven | `LlmDesensitizer.BuildResult()`；`LlmDesensitizerTests.BuildResult_ReplacesLongerValuesFirst` | 无 |
| LLM 并发、单条 TimeoutMs、只缓存 Success | proven | `DesensitizeBatchAsync()` + `SemaphoreSlim`；`ProcessOneAsync()` timeout；只在 `Status == Success` 时 `Set()`；`LlmDesensitizerTests.DesensitizeAsync_ReturnsModelFailureOnTimeout` 与 `DesensitizeAsync_CachesOnlySuccessfulResults` | 无 |
| 缓存 SHA256 内容指纹 + config fingerprint | proven | `DesensitizeCache`；`ConfigFingerprintProvider` 包含 prompt、model、mapping version、redaction、keywords | 无 |
| Prompt 加载优先级：配置绝对路径、ContentRoot prompts、内置默认；配置变更刷新 | proven | `PromptLoader.Load()`；构造函数 `OnChange` 清空 `_cachedPrompt`；`PromptAndFingerprintTests.PromptLoader_UsesConfiguredPromptAndRefreshesOnOptionsChange`；publish smoke 需验证 prompt 输出 | 无 |
| LLM JSON 解析容错：markdown、单引号、trailing comma、数组截取、补 `]` | proven | `PiiJsonParser.Normalize()`；`PiiJsonParserTests` | 无 |
| 请求 body 流式读取必须执行硬上限，不能只依赖 Content-Length | proven | `ReadRequestBodyAsync()` 逐块计数；`Middleware_Returns413WhenBodyExceedsLimit` 与 `Middleware_Returns413WhenStreamingBodyExceedsLimitWithoutContentLength` | 无 |
| 文本片段抽取：OpenAI Chat string/multimodal、OpenAI Responses、Gemini contents/systemInstruction；非文本保留 | proven | `TextPartExtractor`；`TextPartExtractorTests` 覆盖这些形态 | 无 |
| system/developer 默认 Phase1+Phase2，跳过 Phase3/规则/LLM；可配置开启规则/LLM | proven | `SystemMessageRedactionConfig` 默认；`BuildLlmQueue()` system gating；`HttpPipelineTests.Middleware_StrictModeBlocksSystemMessageWhenSystemLlmIsEnabledAndLocalModelDisabled` | 无 |
| Schema 清洗按 provider，OpenAI 保留 `additionalProperties`，Gemini/Vertex 删除 | proven | `SchemaCleaner`；`SchemaCleanerTests` | 无 |
| Multi-auth：OpenAI bearer、Anthropic x-api-key/version、Gemini/Vertex x-goog-api-key | proven | `MultiAuthHandler.ResolveAuthHeaders()`；`MultiAuthHandlerTests` 覆盖 OpenAI/Anthropic/google/gemini/vertex 与 `${VAR}` expansion | 无 |
| Provider transform：OpenAI-compatible 透传/路径规范化；Anthropic/Gemini/Vertex 原生请求转换 | proven | `ProviderTransformers`；`ProviderTransformerTests` 覆盖 Anthropic system/tools、Gemini contents/tools/path、OpenAI path normalize | 无 |
| 上游模型名来源：客户端请求体 `model` 决定实际上游模型，配置只保存 `BaseUrl` / `ApiKey` / `Provider` 等连接信息 | proven | `OpenAiCompatibleTransformer` 透传请求体；`AnthropicTransformer` 复制请求体 model；`GeminiTransformer.BuildPath()` 从请求体 `model` 生成 native path；`UpstreamResolver` 先按请求协议匹配 target provider，匹配不到再用 `Proxy.DefaultTarget`，不按 model key 选 target；`UpstreamResolverTests`、`HttpPipelineTests.Middleware_ForwardsClientModelWhenUsingDefaultUpstreamTarget` 与 `Middleware_GeminiNativePathUsesClientModelWhenConfiguredTargetIsDefault` | 无 |
| Provider-native 响应透传并加 `x-desensitize-response-mode: provider-native` | proven | `ApplyResponseHeaders()`；`IntegrationTests.NativeProviderResponse_IsMarkedAsProviderNative`；Gemini response header 测试 | 无 |
| 流式响应由 YARP 透传，请求端完整读取脱敏后重建 | proven | middleware 将脱敏/provider-transformed JSON 写回 `HttpContext.Request.Body`；`DesensitizeForwarderTransformer` 只改 URI、鉴权和响应头，避免 YARP 禁止的 outgoing `HttpContent` 替换；`Middleware_PreservesStreamFlagAndAcceptHeaderForSseForwarding`；`YarpForwarderIntegrationTests.Middleware_UsesRealYarpForwarderWithoutReplacingOutgoingContent`；实际 `/v1/responses` 代理 smoke 进入 YARP `Proxying to ...` 并收到上游响应 | 无 |
| 配置项完整：Enabled、StrictMode、PromptPath、body/text 上限、LocalModel、Keywords、Redaction、SystemMessage、Proxy、Runtime、Observability | proven | `PrivacyConfig.cs`；`appsettings.json` | 无 |
| 运行目录解析：DataDirectory 默认用户级目录；LogDirectory 未配置时默认 `DesensitizeProxy.AspNetCore.dll` 所在目录；支持相对路径和 `${VAR}` | proven | `RuntimeDirectoryResolver`；`Program.cs` 创建 Data/Log 目录；`RuntimeDirectoryResolverTests` | 无 |
| 健康检查 `/health`：regex ok、llm connected/timeout/error/disabled、cache_entries | proven | `HealthCheckEndpoint`；`HealthProbeTests` 覆盖 disabled/connected/error/timeout | 无 |
| Metrics 7 个指标，regex phase tag、llm failure reason tag、MetricsEnabled 开关 | proven | `DesensitizeMetrics`；`DesensitizeMetricsTests` 验证 phase/reason/disabled | 无 |
| 发布产物：prompt 复制到 publish 输出 | proven | AspNetCore csproj Content include；`dotnet publish ... -o artifacts/publish/osx-arm64-smoke`；`test -f artifacts/publish/osx-arm64-smoke/prompts/pii-extraction.md` | 无 |
| 跨平台部署：Docker、docker-compose、systemd、launchd、Windows Service、publish-all RIDs | proven | 文件存在：`Dockerfile`、`.dockerignore`、`deploy/*`、`scripts/publish-all.sh`；Windows Service 在 `Program.cs`；`scripts/publish-all.sh` 成功发布 6 个 RID；`plutil -lint deploy/launchd/...`；`docker compose -f deploy/docker-compose.yml config`；`docker build -t locrouter-privacy-proxy:smoke .` 成功；PowerShell 容器 AST 解析 `deploy/windows/install-service.ps1` 成功；`win-x64`/`win-arm64` 发布产物含 `.exe` | 未在真实 Windows SCM 上执行安装 smoke；该项属于当前 macOS 环境不可执行的端到端平台验证，不影响脚本语法、产物和 `UseWindowsService()` 证据 |
| 附录 A 文件清单 | proven | `dotnet-privacy-router-design-final.md` 已更新到当前 Core/AspNetCore/Tests 实际结构 | 无 |

## 当前命令证据

```bash
dotnet test tests/DesensitizeProxy.Core.Tests/DesensitizeProxy.Core.Tests.csproj
```

结果：69 passed, 0 failed。

```bash
dotnet build DesensitizeProxy.slnx
dotnet test tests/DesensitizeProxy.Core.Tests/DesensitizeProxy.Core.Tests.csproj --no-build
ASPNETCORE_ENVIRONMENT=Development PrivacyProxy__Proxy__Port=18403 PrivacyProxy__LocalModel__Enabled=false dotnet run --project src/DesensitizeProxy.AspNetCore/DesensitizeProxy.AspNetCore.csproj
curl -i http://127.0.0.1:18403/health
dotnet publish src/DesensitizeProxy.AspNetCore/DesensitizeProxy.AspNetCore.csproj -c Release -r osx-arm64 --self-contained false -o artifacts/publish/osx-arm64-smoke
test -f artifacts/publish/osx-arm64-smoke/prompts/pii-extraction.md
bash -n scripts/publish-all.sh
plutil -lint deploy/launchd/com.clawxrouter.privacy-proxy.plist
docker compose -f deploy/docker-compose.yml config
scripts/publish-all.sh
for rid in osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64 win-arm64; do test -f "artifacts/publish/$rid/prompts/pii-extraction.md" || exit 1; done
docker run --rm -v "$PWD:/workspace:ro" mcr.microsoft.com/powershell:latest pwsh -NoLogo -NoProfile -Command '$errors=$null; [System.Management.Automation.Language.Parser]::ParseFile("/workspace/deploy/windows/install-service.ps1", [ref]$null, [ref]$errors) > $null; if ($errors.Count) { $errors | ForEach-Object { $_.ToString() }; exit 1 }'
test -f artifacts/publish/win-x64/DesensitizeProxy.AspNetCore.exe && test -f artifacts/publish/win-arm64/DesensitizeProxy.AspNetCore.exe
```

结果：build 0 warnings/0 errors；test 81 passed；实际启动监听 `http://127.0.0.1:18403`，`/health` 返回 503 degraded 且 `llm=disabled`，证明启动链路和 DI 构造通过；`/v1/responses` 真实 YARP smoke 不再触发 `RequestCreation`，请求到达本地上游；publish smoke 成功；prompt 文件存在；publish 脚本语法通过；launchd plist OK；docker compose config 可解析；6 个 RID (`osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`) 全部发布成功且 prompt 文件存在；PowerShell parser 无语法错误；Windows RID 产物包含 `DesensitizeProxy.AspNetCore.exe`。

```bash
docker build -t locrouter-privacy-proxy:smoke .
```

结果：第一次因 SDK 180MB 主层下载过慢人工取消；补 `.dockerignore` 后重跑成功，Docker context 降到 4.58KB，镜像 `locrouter-privacy-proxy:smoke` 构建完成。

## 剩余平台说明

当前 macOS 环境无法调用 Windows Service Control Manager，因此未执行真实 `sc.exe create/start` 安装 smoke。已完成的证据覆盖脚本语法、Windows RID `.exe` 产物、`UseWindowsService()` 集成和发布链路。
