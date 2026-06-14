# .NET 10 本地脱敏代理方案

> 一个纯粹的 PII 脱敏代理——拦截用户消息，识别并脱敏隐私信息，转发到云端 LLM。不做本地推理、不做模型路由、不做记忆隔离。

## 目录

- [1. 设计目标与原则](#1-设计目标与原则)
- [2. 架构概览](#2-架构概览)
- [3. 核心抽象](#3-核心抽象)
- [4. Regex 脱敏层——第一道防线](#4-regex-脱敏层第一道防线)
- [5. 规则引擎——分级初筛](#5-规则引擎分级初筛)
- [6. LLM 两步法脱敏——语义增强](#6-llm-两步法脱敏语义增强)
- [7. 代理中间件](#7-代理中间件)
- [8. 配置设计](#8-配置设计)
- [9. 可观测性](#9-可观测性)
- [附录 A：文件清单](#附录-a文件清单)
- [附录 B：部署模式](#附录-b部署模式)
  - [B.1 跨平台运行方式](#b1-跨平台运行方式)
  - [B.2 默认目录策略](#b2-默认目录策略)
  - [B.3 发布产物](#b3-发布产物)

---

## 1. 设计目标与原则

### 1.1 核心定位

```
用户消息 → .NET 脱敏代理 → 云端 LLM (Claude / GPT / Gemini / DeepSeek)
                │
         ┌──────┼──────┐
         ▼      ▼      ▼
      Regex层  规则引擎  LLM脱敏
      (始终)  (分级)  (尽力)
```

**不做什么**：
- ❌ 不做本地 LLM 推理（不替代云端模型回答问题）
- ❌ 不做模型路由切换（不根据敏感级别换 provider）
- ❌ 不做双轨记忆隔离（不管理会话文件存储）

**只做什么**：
- ✅ 拦截用户消息，识别其中的 PII
- ✅ 用 `[REDACTED:PHONE]` 等标签替换
- ✅ 转发脱敏后的请求到云端

### 1.2 设计原则

| 原则 | 说明 |
|---|---|
| **纵深防御** | Regex（始终运行）→ 规则引擎（分级判定）→ LLM 两步法（语义增强）。Regex 成本最低放最前，LLM 成本最高放最后。 |
| **失败安全** | 默认 LLM 脱敏失败 → 降级到 Regex-only + 告警，不阻断业务。`StrictMode=true` 时，只要请求命中 Trigger/Hint 且 LLM 结果状态不是 `Success`，就返回 502/413。 |
| **开箱安全** | 默认配置覆盖最常见的中文 PII 场景，手机号/身份证/邮箱 Regex 默认开启。 |
| **Regex 不可回退** | Regex 是确定性安全基线。LLM 只能追加语义脱敏，不能覆盖并降低 Regex 已经完成的脱敏结果。 |
| **先 Regex 再检测** | Regex 是纯 CPU、永不失败。对所有消息（含 system）始终运行。规则引擎只在 Regex 之后判断是否追加 LLM 脱敏。 |

### 1.3 失败降级矩阵

| 场景 | Regex 命中 | LLM 可用 | 动作 |
|---|---|---|---|
| 常规请求 | ✅ / ❌ | ✅ | Regex → 规则引擎判定 → 有 Trigger/Hint？→ LLM 增强 → 再跑 Regex 兜底 → 转发 |
| LLM 挂了 | ✅ | ❌ | 命中 Trigger/Hint 且 `StrictMode=true`? 502 ❌ : Regex-only 转发 + 告警 ✅ |
| LLM 挂了 + 无 Regex 命中 | ❌ | ❌ | 命中 Trigger/Hint 且 `StrictMode=true`? 502 ❌ : 转发 + 高优告警 ⚠️ |
| LLM 超时 (>TimeoutMs) | ✅ / ❌ | 部分 | `StrictMode=true` 且命中 Trigger/Hint? 502 : Regex-only 转发 + metric 打点 |
| 本地模型禁用 | ✅ / ❌ | 主动禁用 | `StrictMode=true` 且命中 Trigger/Hint? 502 : Regex-only 转发 + metric 打点 |

### 1.4 技术栈

| 层 | 选型 |
|---|---|
| 运行时 | .NET 10 |
| Web 框架 | ASP.NET Core Minimal API / Middleware |
| 本地脱敏 LLM | 默认 HTTP OpenAI-compatible / Ollama-native client；同时提供 `Microsoft.Extensions.AI.Abstractions` + `IChatClient` 适配器 |
| 反向代理 | YARP `IHttpForwarder` |
| 配置 | `IOptionsMonitor<T>` Options 模式 + 热更新 |
| 跨平台 | macOS / Linux / Windows |
| Regex（静态） | `[GeneratedRegex]` Source Generator（编译期，AOT 友好） |
| Regex（动态） | `RegexOptions.Compiled` + `ConcurrentDictionary` LRU 缓存 |
| 缓存 | `MemoryCache`（SHA256 内容指纹） |
| 日志 | `ILogger<T>` 结构化日志 |

### 1.5 跨平台约束

| 项 | 约束 |
|---|---|
| 路径处理 | 统一使用 .NET `Path` / `Path.Combine` / `Path.GetFullPath`，不写死 `/` 或 `\\`。|
| 配置位置 | 支持绝对路径、`ContentRoot` 相对路径，以及环境变量占位符；内部统一解析为当前平台可用的绝对路径。|
| 运行目录 | 不依赖当前工作目录；默认基于 `ContentRootPath`、应用基目录或用户数据目录定位 prompt / cache / 日志。|
| 服务托管 | macOS 用 `launchd`，Linux 用 `systemd`，Windows 用 Windows Service；同一代码库和配置模型跨平台，发布产物按 RID 区分。|
| 端口监听 | 监听逻辑仅依赖 Kestrel/YARP，不依赖特定平台 socket API。|
| 文件权限 | 读取 prompt / 写入 cache / 日志时遵循当前系统权限模型，不假设 POSIX 权限或 Windows ACL。|

---

## 2. 架构概览

```
┌──────────────────────────────────────────────────────┐
│                 DesensitizeProxyMiddleware            │
│                                                      │
│  请求 → 读取 body → 抽取 TextPart[] 并保存原文副本    │
│         │                                             │
│         ▼                                             │
│  ┌─────────────────────────────────────────┐         │
│  │ Layer 1: Regex 脱敏（始终运行）           │         │
│  │  - 所有消息（含 system）                  │         │
│  │  - Phase 1: 硬编码（始终）               │         │
│  │  - Phase 2: 可配模式（默认开）            │         │
│  │  - Phase 3: 上下文关键词（始终）          │         │
│  │  - system msg: 默认 Phase 1 + Phase 2    │         │
│  └──────────────┬──────────────────────────┘         │
│                 │                                     │
│         ┌───────▼────────┐                            │
│         │ 规则引擎判定    │                            │
│         │ (基于原始文本)   │                           │
│         │ TRIGGER/HINT    │                            │
│         │   /NONE         │                            │
│         └───┬────────┬───┘                            │
│             │        │                                │
│       TRIGGER 或     NONE                             │
│       HINT+Regex命中/StrictMode                       │
│             │        │                                │
│             ▼        ▼                               │
│  ┌──────────────────┐  ┌──────────┐                  │
│  │ Layer 2: LLM脱敏  │  │ 直接转发  │                  │
│  │ 基于原始文本        │  └──────────┘                  │
│  │ 并发 + 缓存 + 超时  │                                │
│  │ 失败 → 降级       │                                │
│  └────────┬─────────┘                                │
│           ▼                                           │
│       Schema 清洗                                      │
│           │                                           │
│           ▼                                           │
│       Multi-auth + YARP 转发                           │
└──────────────────────────────────────────────────────┘
```

**三层协同要点**：
- 请求体进来后**先抽取文本片段并保存原文副本**（`originalTexts`），后续规则引擎和 LLM 脱敏都基于副本
- Regex 脱敏直接原位修改 `TextPart.Text`，这是最终转发的基础
- LLM 脱敏成功后，必须先对 `result.RedactedContent` 再执行 Regex 兜底，再原位写回对应 `TextPart`。实现不允许让 LLM 结果还原 Regex 已经脱敏的内容

---

## 3. 核心抽象

### 3.1 规则命中等级

```csharp
public enum RuleHitLevel
{
    None,      // 无命中，跳过 LLM 脱敏
    Hint,      // 弱信号：可能含 PII（如 "身份证" 出现在技术讨论中）
    Trigger    // 强信号：确认需要 LLM 语义脱敏（如 "身份证号是 430102..."）
}

public record DetectionResult(
    RuleHitLevel HitLevel,
    string? Reason,           // 命中的规则描述
    double Confidence         // 1.0 = 强规则, <1.0 = Hint
);
```

### 3.2 脱敏结果

```csharp
public enum DesensitizeStatus
{
    Success,          // 成功解析并完成替换；允许 PII 数组为空仅限 DetectionResult=None
    ModelFailure,     // 模型不可用 / HTTP 错误 / 超时
    ParseFailure,     // 返回内容不是可接受 JSON 数组
    EmptyResult,      // Trigger/Hint 语境下解析为空数组
    AllItemsInvalid   // Trigger/Hint 语境下所有 value 都无法在原文定位
}

public record DesensitizeResult(
    string RedactedContent,       // 脱敏后内容；失败时 = 原始内容
    bool WasModelUsed,            // LLM 是否参与
    DesensitizeStatus Status,
    string? FailureReason
)
{
    public bool Failed => Status != DesensitizeStatus.Success;
}
```

`DesensitizeStatus` 是 StrictMode 的强契约：中间件不能只判断 HTTP/超时失败。只要规则引擎已经给出 Trigger/Hint，解析失败、空数组、或全部 value 无法在原文定位，都必须以非 Success 状态返回，StrictMode 下阻断。

### 3.3 核心接口

```csharp
// Regex 脱敏器：同步、确定性、永不失败
public interface IPiiRedactor
{
    string Redact(string content);
    string RedactPhase1Only(string content);
    string RedactSystem(string content, SystemMessageRedactionConfig config);
    bool HasAnyHit(string content);   // 快速判断是否有命中（不执行替换）
}

// 规则引擎：同步、纯 CPU、分级命中
public interface IRuleEngine
{
    DetectionResult Check(string content);
}

// LLM 脱敏器：异步、语义理解、尽力而为
public interface ILlmDesensitizer
{
    Task<DesensitizeResult> DesensitizeAsync(
        string content, DetectionResult detection, CancellationToken ct);

    // 批量并发脱敏（内部控制并发上限）
    Task<IReadOnlyList<DesensitizeResult>> DesensitizeBatchAsync(
        IReadOnlyList<(string Content, DetectionResult Detection)> items,
        CancellationToken ct);
}

// 脱敏结果缓存（基于 SHA256 内容指纹）
public interface IDesensitizeCache
{
    string? Get(string originalContent, string configFingerprint);
    void Set(string originalContent, string redactedContent, string configFingerprint);
}

// Schema 清洗（去除云厂商不兼容的 JSON Schema 关键词）
public interface ISchemaCleaner
{
    JsonNode Clean(JsonNode tools, string provider);
}
```

---

## 4. Regex 脱敏层——第一道防线

> 纯 CPU、永不失败、<1ms 延迟。所有规则在代理启动时即就绪，不依赖外部服务。对 system message 默认运行 **Phase 1 + Phase 2**，捕获硬编码凭证、手机号、身份证、邮箱等低误报 PII；Phase 3 和 LLM 默认跳过，防止破坏指令语义。

### 4.1 三层 Regex

| 层 | 触发条件 | 实现方式 | 对 system msg |
|---|---|---|---|
| **Phase 1** | 始终运行，不可关闭 | `[GeneratedRegex]` Source Generator | ✅ 运行 |
| **Phase 2** | 始终运行（可配置关闭） | `[GeneratedRegex]` Source Generator | ✅ 默认运行（可配置关闭） |
| **Phase 3** | 始终运行 | `RegexOptions.Compiled` + `ConcurrentDictionary` LRU 缓存 | ❌ 跳过（保护语义） |

### 4.2 Phase 1: 硬编码（误报极低，编进代码）

| 规则 | 正则 | 替换为 |
|---|---|---|
| SSH 私钥块 | `-----BEGIN (?:RSA \|EC \|DSA \|OPENSSH )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA \|EC \|DSA \|OPENSSH )?PRIVATE KEY-----` | `[REDACTED:PRIVATE_KEY]` |
| AWS Key | `AKIA[0-9A-Z]{16}` | `[REDACTED:AWS_KEY]` |
| DB 连接串 | `(?:mysql\|postgres\|postgresql\|mongodb\|redis\|amqp)://[^\s"']+` | `[REDACTED:DB_CONNECTION]` |
| 快递单号 | `(?:快递单号\|运单号\|取件码)[：:\s]*[A-Za-z0-9]{6,20}` | `[REDACTED:DELIVERY]` |
| 门禁密码 | `(?:门禁码\|门禁密码\|开门密码\|门锁密码)[：:\s]*[A-Za-z0-9#*]{3,12}` | `[REDACTED:ACCESS_CODE]` |

> **表格转义说明**：上表中的 `\|` 是 Markdown 表格转义，防止竖线被解析成列分隔符。实现 .NET Regex 时应使用普通 alternation `|`，例如 `(?:mysql|postgres|postgresql|mongodb|redis|amqp)`，不要复制成 `\|`。

> **SSH 正则特别说明**：`(?:RSA |EC |...)` 使用非捕获组而非字符类。字符类 `[RSA|EC]` 中 `|` 是字面字符而非"或"，这在很多实现中是常见错误。

### 4.3 Phase 2: 默认开启（可配置关闭）

| 规则 | 正则 | 替换为 |
|---|---|---|
| 中国手机号 | `(?<!\d)1[3-9]\d{9}(?!\d)` | `[REDACTED:PHONE]` |
| 中国身份证号 | `(?<!\d)\d{17}[\dXx](?!\d)` | `[REDACTED:ID]` |
| 邮箱 | `[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}` | `[REDACTED:EMAIL]` |
| API 密钥 | 见 Phase 3 上下文规则（keyword-gated，避免误报） | `[REDACTED:API_KEY]` |

> API Key 检测采用 Phase 3 的 keyword-gated 模式（`api_key: sk-xxx`），而非裸正则 `\b(?:sk|key)-...\b`。后者会误匹配 `key-generation-algorithm`、`sk-learn-pipeline` 等大量技术文档中的正常文本。

### 4.4 Phase 3: 上下文关键词

两档连接模式：

```
STRICT（模糊词，需动词或分隔符确认）:
  关键词 + is/are/was/were + 值
  关键词 + = / : + 值
  适用: "credit card", "SSN", "secret"

LOOSE（凭证词，下一词大概率是值）:
  关键词 + 空格/等号/冒号/动词 + 值
  适用: "password", "api_key", "token"
```

英文关键词规则（与 ClawXRouter 一致）：

```csharp
// STRICT: 需要动词或分隔符
{ pattern: /(?:credit\s*card|card\s*(?:number|no\.?))\s+(?:is|are|was|were)(?:\s+(?:in|at|on|of|for))*\s*["']?([^\s"']{2,})["']?/gi, label: "CARD" }
{ pattern: /(?:ssn|social\s*security(?:\s*(?:number|no\.?))?)\s+(?:is|are|was|were)\s*["']?([^\s"']{2,})["']?/gi, label: "SSN" }

// LOOSE: 直接取下一词
{ pattern: /(?:password|passwd|pwd|passcode)[\s:=]+["']?([^\s"']{2,})["']?/gi, label: "PASSWORD" }
{ pattern: /(?:api[_\s]?key|access[_\s]?key|secret[_\s]?key)[\s:=]+["']?([^\s"']{2,})["']?/gi, label: "API_KEY" }
{ pattern: /(?:(?:auth[_\s]?)?token|bearer)[\s:=]+["']?([^\s"']{2,})["']?/gi, label: "TOKEN" }
```

**中文关键词规则**（特殊处理——中文无空格分词，不做词边界断言）：

```csharp
private static readonly (string Keyword, string Label, bool Strict)[] ChineseRules =
[
    ("密码",       "PASSWORD",      false),  // LOOSE: "密码是xxx" "密码：xxx"
    ("私钥",       "PRIVATE_KEY",   true),   // STRICT: 避免代码中 "私钥" 误匹配
    ("取件码",     "DELIVERY",      false),
    ("验证码",     "VERIFICATION",  false),
    ("银行卡号",   "CARD",          true),
    ("身份证号",   "ID",            true),
];

// 匹配逻辑:
// 1. IndexOf(keyword) 找到位置
// 2. 向后扫描：
//    LOOSE: keyword 后第一个非空连续片段作为值
//    STRICT: keyword 后需出现 "是" / "为" / ":" / "=" 才取值
// 3. 仅在非 code fence 区域（无 ``` 包围）中运行
```

> 关键设计决策：中文关键词**不使用** `(?<![\u4e00-\u9fa5])` lookbehind。中文没有空格分词，"的密码是" 中 "的" 是汉字，lookbehind 会否决所有真实命中。

### 4.5 正则编译策略

| 正则类型 | 使用方式 | 原因 |
|---|---|---|
| Phase 1（硬编码） | `[GeneratedRegex]` | 编译期确定，AOT 友好 |
| Phase 2（可配模式） | `[GeneratedRegex]` | 模式固定，仅开关可配 |
| Phase 3（上下文关键词） | `new Regex(..., Compiled)` + LRU 缓存（上限 500） | 关键词由用户配置，正则需运行时拼接 |

---

## 5. 规则引擎——分级初筛

> Regex 之后运行。基于**原始内容**（Regex 处理前的副本）做判定。目的是减少不必要的 LLM 调用：不是每条消息都需要小模型做语义脱敏。

### 5.1 三级命中

```
TRIGGER → 必须追加 LLM 脱敏（强信号，高置信度）
  - "我的身份证号是 430102199001011234"
  - "密码是 hunter2，别告诉别人"
  - "地址：北京市朝阳区xxx路xxx号"

HINT → 可能含 PII，但不确定（弱信号，需联合 Regex 命中判定）
  - "帮我解释一下身份证号校验算法"
  - "how to validate email address in regex"
  - "salary calculation formula in Excel"

NONE → 不追加 LLM 脱敏
  - "帮我写个 Python 脚本"
  - "介绍一下微服务架构"
```

### 5.2 判定逻辑

```csharp
public DetectionResult Check(string originalContent)
{
    // ① Trigger 关键词（强 PII 上下文，包含连接词的短语）
    //    "身份证号", "密码是", "密码为", "密码：", "手机号", "住址",
    //    "门禁码", "取件码", "password is", "password:",
    //    "api_key", "access_key", "secret_key", "private key"
    //    → 命中即返回 Trigger

    // ② Regex Phase 2/3 命中检查（基于 originalContent）
    //    手机号正则命中？→ Trigger
    //    身份证号正则命中？→ Trigger
    //    上下文关键词（"密码是 xxx"）命中？→ Trigger

    // ③ Hint 关键词（弱 PII 上下文，不含连接词）
    //    "身份证", "salary", "工资", "病历", "地址", "password",
    //    "SSN", "credit card", "medical", "passport"
    //    → 命中即返回 Hint

    // ④ 都没命中 → None
}
```

### 5.3 关键词配置

```csharp
public class KeywordConfig
{
    // 强信号：命中后必定追加 LLM 脱敏（短语级，减少假阳性）
    public List<string> TriggerKeywords { get; set; } = [
        "身份证号", "手机号", "电话号码", "住址", "门牌号", "家庭地址",
        "密码是", "密码为", "密码：", "password is", "password:",
        "取件码", "门禁码", "开门密码", "快递单号", "运单号",
        "api_key", "access_key", "secret_key", "private key",
    ];

    // 弱信号：需结合 Regex 命中结果联合判定（单词级）
    public List<string> HintKeywords { get; set; } = [
        "身份证", "SSN", "social security",
        "salary", "工资单", "工资条", "payslip", "税表", "tax return",
        "银行卡", "bank account", "credit card",
        "病历", "medical record", "体检报告", "处方",
        "地址", "address", "passport", "护照",
        "password", "密码", "secret", "私钥", "token",
    ];
}
```

### 5.4 命中后动作

```
Trigger             → 收集到 LLM 脱敏队列
Hint + Regex 命中    → 收集到 LLM 脱敏队列
Hint + Regex 无命中  → StrictMode=true 时收集到 LLM 脱敏队列；否则不追加（节省 LLM 调用）
None                 → 不追加
```

---

## 6. LLM 两步法脱敏——语义增强

> 本地小模型（MiniCPM / Qwen / Ollama）做语义理解。**并发调用 + 结果缓存 + TimeoutMs 超时 + 失败降级**。

### 6.1 为什么是两步法

| 方式 | 优势 | 劣势 |
|---|---|---|
| 让模型改写全文 | 简单 | 可能改写语义，破坏 prompt 意图 |
| **两步法（本方案）** | 精确、无幻觉 | 多一次 LLM 调用 |

模型只负责「找」（提取 PII 为 JSON 数组），代码负责「换」（精确字符串替换）。各取所长。

### 6.2 流程

```
Step 1: 本地 LLM 提取 PII → JSON 数组

  Input:  "张三住在北京市朝阳区xx路xx号，电话13912345678，门禁码1234#"
  Prompt: pii-extraction.md (可加载用户自定义)
  LLM 输出: [{"type":"NAME","value":"张三"},
             {"type":"ADDRESS","value":"北京市朝阳区xx路xx号"},
             {"type":"PHONE","value":"13912345678"},
             {"type":"ACCESS_CODE","value":"1234#"}]

                  ↓

Step 2: 程序化替换（按 value 长度降序，防止部分替换）

  排序后:
    1. "北京市朝阳区xx路xx号" → [REDACTED:ADDRESS]
    2. "13912345678"         → [REDACTED:PHONE]
    3. "张三"                → [REDACTED:NAME]
    4. "1234#"               → [REDACTED:ACCESS_CODE]

Step 3: Regex 兜底（必须）

  对 Step 2 结果再次运行 Regex 脱敏，确保 LLM 漏提取时不会还原 Regex 已命中的手机号、身份证、邮箱、凭证等内容。

  最终: "[REDACTED:NAME]住在[REDACTED:ADDRESS]，电话[REDACTED:PHONE]，门禁码[REDACTED:ACCESS_CODE]"
```

**安全不变量**：最终写回请求体的内容必须满足 `final = RegexRedact(LlmRedact(original))`。如果 LLM 解析为空或漏提取，Regex 兜底仍然保留确定性脱敏结果。

### 6.3 PII 类型 → 标签映射

| LLM 输出 type | 替换为 |
|---|---|
| `NAME`, `SENDER_NAME`, `RECIPIENT_NAME` | `[REDACTED:NAME]` |
| `PHONE`, `MOBILE`, `LANDLINE` | `[REDACTED:PHONE]` |
| `ADDRESS` | `[REDACTED:ADDRESS]` |
| `PASSWORD`, `API_KEY`, `TOKEN`, `SECRET` | `[REDACTED:SECRET]` |
| `ID`, `ID_CARD`, `ID_NUMBER` | `[REDACTED:ID]` |
| `CARD`, `BANK_CARD`, `CARD_NUMBER` | `[REDACTED:CARD]` |
| `ACCESS_CODE`, `DELIVERY`, `COURIER_NUMBER` | `[REDACTED:DELIVERY]` |
| `EMAIL` | `[REDACTED:EMAIL]` |
| `IP` | `[REDACTED:IP]` |
| `LICENSE_PLATE`, `PLATE` | `[REDACTED:LICENSE]` |
| `SALARY`, `AMOUNT` | `[REDACTED:AMOUNT]` |
| 其他 | `[REDACTED:<TYPE>]` |

### 6.4 批量并发脱敏

```csharp
public async Task<IReadOnlyList<DesensitizeResult>> DesensitizeBatchAsync(
    IReadOnlyList<(string Content, DetectionResult Detection)> items,
    CancellationToken ct)
{
    var semaphore = new SemaphoreSlim(_config.MaxConcurrency); // 默认 4
    var tasks = new List<Task<DesensitizeResult>>(items.Count);

    foreach (var item in items)
    {
        tasks.Add(ProcessOneAsync(item.Content, item.Detection, semaphore, ct));
    }

    return await Task.WhenAll(tasks);
}

private async Task<DesensitizeResult> ProcessOneAsync(
    string content, DetectionResult detection, SemaphoreSlim semaphore, CancellationToken ct)
{
    // 1. 先查缓存（SHA256 内容指纹 + 配置指纹）
    var configFingerprint = _fingerprintProvider.Current;
    var cached = _cache.Get(content, configFingerprint);
    if (cached != null)
    {
        _metrics.CacheHitsTotal.Inc();
        return new DesensitizeResult(cached, WasModelUsed: false,
            DesensitizeStatus.Success, FailureReason: null);
    }

    await semaphore.WaitAsync(ct);
    try
    {
        // 2. 单条 LLM 超时
        using var cts = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(_config.TimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

        return await DesensitizeSingleAsync(content, detection, linked.Token);
    }
    catch (OperationCanceledException)
    {
        _logger.LogWarning("LLM desensitize timeout for {Length} chars", content.Length);
        _metrics.TimeoutTotal.Inc();
        return new DesensitizeResult(content, WasModelUsed: false,
            DesensitizeStatus.ModelFailure, FailureReason: "timeout");
    }
    finally
    {
        semaphore.Release();
    }
}
```

### 6.5 脱敏失败不阻断

这是最重要的安全底线设计：

```
LLM 失败 + StrictMode=false → Regex-only 转发 + 告警 ✅
LLM 失败 + StrictMode=true + Trigger/Hint → 502 ❌
LLM 失败 + StrictMode=true + None → Regex-only 转发 + 告警 ✅
LocalModel.Enabled=false + StrictMode=false → Regex-only 转发 + metric 打点 ✅
LocalModel.Enabled=false + StrictMode=true + Trigger/Hint → 502 ❌
TextPart 超过 MaxTextPartLengthForLlm + StrictMode=false → Regex-only 转发 + 告警 ✅
TextPart 超过 MaxTextPartLengthForLlm + StrictMode=true + Trigger/Hint → 413 或 502 ❌

默认 StrictMode=false。
```

长文本不做静默截断。截断会破坏 PII 边界，可能让地址、凭证或姓名只剩半段而无法脱敏。超过 `MaxTextPartLengthForLlm` 的文本片段只能走完整 Regex-only 降级，或在 StrictMode 下阻断。

### 6.6 缓存策略

```csharp
public class DesensitizeCache : IDesensitizeCache
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
        SizeLimit = 500,
        ExpirationScanFrequency = TimeSpan.FromMinutes(5)
    });

    public string? Get(string originalContent, string configFingerprint)
    {
        var key = ComputeSha256(configFingerprint + "\n" + originalContent);
        return _cache.TryGetValue(key, out string? cached) ? cached : null;
    }

    public void Set(string originalContent, string redactedContent, string configFingerprint)
    {
        var key = ComputeSha256(configFingerprint + "\n" + originalContent);
        _cache.Set(key, redactedContent, new MemoryCacheEntryOptions
        {
            Size = 1,
            SlidingExpiration = TimeSpan.FromMinutes(30)
        });
    }

    private static string ComputeSha256(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
```

只缓存 `DesensitizeStatus.Success` 的结果。`ModelFailure`、`ParseFailure`、`EmptyResult`、`AllItemsInvalid` 都不能写入缓存，避免把一次降级或漏提取结果稳定复用。Trigger/Hint 语境下，如果模型返回空数组但 Regex-only 后仍可转发，也只能走本次请求的降级路径，不能缓存。

`configFingerprint` 至少包含 Prompt hash、模型名、PII type mapping 版本、Regex/Redaction 配置版本和关键词配置版本。`IOptionsMonitor<T>.OnChange` 触发时也可以直接清空缓存，防止热更新后继续复用旧脱敏结果。

### 6.7 LLM JSON 解析鲁棒性

本地小模型输出格式不稳定，需容错处理：

```csharp
private List<PiiItem> ParsePiiJson(string raw)
{
    // ① 去掉 markdown 代码块：```json ... ```
    // ② 修复 Python 单引号：{'type':'NAME'} → {"type":"NAME"}
    // ③ 去掉 trailing comma：{"type":"NAME"},] → {"type":"NAME"}]
    // ④ 找到第一个 [ ... 最后一个 ]，截取 JSON 数组
    // ⑤ 缺 ] 闭合？补 ]
    // ⑥ JSON 解析
    // ⑦ 过滤出 {type, value} 结构的项
    // 全部失败 → 返回空数组，由调用方按命中等级决定是否视为失败
}
```

解析结果判定规则：

| 场景 | DetectionResult | StrictMode=false | StrictMode=true |
|---|---|---|---|
| JSON 解析失败 / 非数组 | Trigger / Hint | `ParseFailure`，Regex-only 转发 + 告警 | `ParseFailure`，502 |
| JSON 解析为空数组 | Trigger / Hint | `EmptyResult`，Regex-only 转发 + 告警 | `EmptyResult`，502 |
| JSON 解析为空数组 | None | `Success`，Regex-only 转发 + 告警 | `Success`，Regex-only 转发 + 告警 |
| JSON 有结果但 value 不在原文中 | Trigger / Hint | 忽略无效项，剩余结果继续处理；若全部无效则 `AllItemsInvalid` | 同左，全部无效时 502 |

语义 PII（姓名、地址、病历描述、备注等）不一定能被 Regex 覆盖。因此只要规则引擎已经给出 Trigger / Hint，LLM 解析失败、空数组或全部无效都不能在 StrictMode 下视为成功。

### 6.8 Prompt 管理

加载优先级（从高到低）：

1. `appsettings.json` 中 `PrivacyProxy.PromptPath` 配置的绝对路径
2. `{ContentRoot}/prompts/pii-extraction.md`
3. 嵌入式资源（内置默认 prompt，含中英文 PII 提取示例）

文件不存在或加载失败时使用嵌入式默认 prompt，覆盖 NAME / PHONE / ADDRESS / ACCESS_CODE / DELIVERY / ID / CARD / LICENSE_PLATE / EMAIL / PASSWORD / PAYMENT / BIRTHDAY / NOTE 等类型。配置热更新时通过 `IOptionsMonitor<T>.OnChange` 回调重新加载 Prompt。

---

## 7. 代理中间件

### 7.1 完整流程

```
┌─────────────────────────────────────────────────────────┐
│               DesensitizeProxyMiddleware                 │
│                                                         │
│  1. 读取请求 body → 解析文本片段 TextPart[]              │
│     → 保存 originalTextParts[] 副本（供后续规则/LLM 用） │
│                                                         │
│  2. Regex 脱敏（基于文本片段原位修改）                   │
│     - system msg: 默认 Phase 1 + Phase 2                 │
│     - 其他 msg: Phase 1 + 2 + 3                         │
│                                                         │
│  3. For each non-system text part:                      │
│     a. 规则引擎判定（基于对应的 originalText）            │
│        ├─ Trigger → 收集 (part, originalText) 到队列     │
│        ├─ Hint + Regex命中 → 收集到队列                  │
│        ├─ Hint + StrictMode → 收集到队列                 │
│        └─ None / Hint且非StrictMode → 跳过              │
│                                                         │
│  4. 并发 LLM 脱敏（MaxConcurrency，TimeoutMs 单条超时） │
│     ├─ 先查缓存                                         │
│     ├─ 成功 → LLM 结果再跑 Regex 兜底后写回文本片段     │
│     ├─ 非 Success + StrictMode + Trigger/Hint → 502      │
│     └─ 失败 + 其他情况 → 告警 + 继续（Regex 版本生效）   │
│                                                         │
│  5. Schema 清洗（去 patternProperties 等）               │
│                                                         │
│  6. Multi-auth + YARP Forwarder transform 转发           │
└─────────────────────────────────────────────────────────┘
```

### 7.2 中间件实现骨架

```csharp
public class DesensitizeProxyMiddleware : IMiddleware
{
    private readonly IRuleEngine _ruleEngine;
    private readonly ILlmDesensitizer _llmDesensitizer;
    private readonly IPiiRedactor _piiRedactor;
    private readonly ISchemaCleaner _schemaCleaner;
    private readonly IHttpForwarder _forwarder;
    private readonly IOptionsMonitor<PrivacyConfig> _options;
    private readonly ILogger _logger;
    private readonly DesensitizeMetrics _metrics;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var config = _options.CurrentValue;
        if (!config.Enabled) { await next(context); return; }

        // 1. 读取 & 解析请求体
        if (context.Request.ContentLength > config.MaxBodySizeBytes)
        {
            context.Response.StatusCode = 413;
            await context.Response.WriteAsync(
                """{"error":{"message":"request body too large"}}""");
            return;
        }

        var body = await ReadRequestBody(context);
        if (body.Length > config.MaxBodySizeBytes)
        {
            context.Response.StatusCode = 413;
            await context.Response.WriteAsync(
                """{"error":{"message":"request body too large"}}""");
            return;
        }

        var parsed = JsonNode.Parse(body);
        var textParts = ExtractTextParts(parsed); // 统一 OpenAI + Gemini 文本片段

        // 保存原始文本副本（Regex 会原地修改 textPart.Text）
        var originalTexts = new Dictionary<TextPartRef, string>();
        foreach (var part in textParts)
        {
            if (!string.IsNullOrWhiteSpace(part.Text))
                originalTexts[part.Ref] = part.Text;
        }

        // 2. Regex 脱敏
        foreach (var part in textParts)
        {
            if (string.IsNullOrWhiteSpace(part.Text)) continue;
            var original = part.Text;
            var redacted = part.IsSystem
                ? _piiRedactor.RedactSystem(original, config.SystemMessageRedaction)
                : _piiRedactor.Redact(original);
            if (redacted != original)
            {
                part.SetText(redacted);
                _metrics.RegexHitTotal.Inc();
            }
        }

        // 3. 规则引擎分级 + 收集 LLM 队列（基于原始内容）
        var llmQueue = new List<(TextPart part, string originalText, DetectionResult detection)>();
        foreach (var part in textParts)
        {
            if (part.IsSystem || string.IsNullOrWhiteSpace(part.Text)) continue;
            if (!originalTexts.TryGetValue(part.Ref, out var original)) continue;

            var detection = _ruleEngine.Check(original);
            if (original.Length > config.MaxTextPartLengthForLlm)
            {
                _logger.LogWarning(
                    "Text part too long for LLM desensitization: {Length} chars",
                    original.Length);
                if (config.StrictMode && detection.HitLevel != RuleHitLevel.None)
                {
                    context.Response.StatusCode = 413;
                    await context.Response.WriteAsync(
                        """{"error":{"message":"PII semantic desensitization skipped because text part is too large"}}""");
                    return;
                }
                continue; // Regex 版本已在 part.Text 中生效
            }

            if (detection.HitLevel == RuleHitLevel.Trigger)
            {
                llmQueue.Add((part, original, detection));
            }
            else if (detection.HitLevel == RuleHitLevel.Hint
                     && (_piiRedactor.HasAnyHit(original) || config.StrictMode))
            {
                llmQueue.Add((part, original, detection));
            }
        }

        // 4. 并发 LLM 脱敏
        if (llmQueue.Count > 0)
        {
            if (!config.LocalModel.Enabled)
            {
                if (config.StrictMode)
                {
                    context.Response.StatusCode = 502;
                    await context.Response.WriteAsync(
                        """{"error":{"message":"PII semantic desensitization disabled in strict mode"}}""");
                    return;
                }

                _logger.LogWarning("Local model disabled, using regex-only desensitization");
            }
            else
            {
                var items = llmQueue
                    .Select(x => (Content: x.originalText, Detection: x.detection))
                    .ToList();
                var results = await _llmDesensitizer.DesensitizeBatchAsync(
                    items, context.RequestAborted);

                for (int i = 0; i < llmQueue.Count; i++)
                {
                    var (part, original, detection) = llmQueue[i];
                    var result = results[i];

                    if (result.Status != DesensitizeStatus.Success)
                    {
                        _metrics.LlmFailureTotal.Inc();
                        if (config.StrictMode && detection.HitLevel != RuleHitLevel.None)
                        {
                            context.Response.StatusCode = 502;
                            await context.Response.WriteAsync(
                                """{"error":{"message":"PII desensitization failed in strict mode"}}""");
                            return;
                        }
                        _logger.LogWarning(
                            "LLM desensitize did not produce a successful result. "
                            + "Status: {Status}; falling back to regex-only. "
                            + "Content length: {Length}", result.Status, original.Length);
                        continue; // Regex 版本已在 part.Text 中生效
                    }

                    var finalRedacted = _piiRedactor.Redact(result.RedactedContent);
                    part.SetText(finalRedacted);
                    if (result.WasModelUsed) _metrics.LlmDesensitizeTotal.Inc();
                }
            }
        }

        // 5. Schema 清洗
        if (parsed?["tools"] is JsonArray tools)
        {
            var target = ResolveTarget(parsed, config);
            parsed["tools"] = _schemaCleaner.Clean(tools, target.Provider ?? "openai");
        }

        // 6. Multi-auth + YARP Forwarder transform 转发
        // 不手工构造 HttpRequestMessage 传给 SendAsync；改写 body/header 必须放进
        // HttpTransformer，让 YARP 管理请求复制、响应流和取消。
        var target = ResolveTarget(parsed, config);
        var transformer = new DesensitizeForwarderTransformer(parsed, target);
        await _forwarder.SendAsync(
            context,
            target.BaseUrl,
            _httpClient,
            ForwarderRequestConfig.Empty,
            transformer);
    }
}
```

`ReadRequestBody` 必须在流式读取过程中执行 `MaxBodySizeBytes` 硬上限，不能只依赖 `Content-Length`。缺失或伪造 `Content-Length` 的请求也必须在超过上限时返回 413。

### 7.3 System Message 处理

System prompt 的处理分两层：

| 层 | system message | 原因 |
|---|---|---|
| Phase 1 硬编码 Regex | ✅ 运行 | 防止开发者硬编码 SSH Key / AWS Key 等凭证 |
| Phase 2 Regex | ✅ 默认运行 | 手机号、身份证、邮箱误报较低；system/developer 消息也可能包含用户资料或业务上下文 |
| Phase 3 Regex | ❌ 默认跳过 | 避免将 "password" "地址" 等字段名替换为 `[REDACTED:XX]` 破坏指令语义 |
| 规则引擎 | ❌ 默认跳过 | 避免把开发者指令送入语义脱敏；如业务会把用户资料拼入 system，可配置开启 |
| LLM 脱敏 | ❌ 默认跳过 | 同上；开启后必须与普通消息遵守同一 StrictMode 结果契约 |

默认策略不是假设 system message 永远安全，而是在低误报 Regex 与指令语义之间取保守平衡。业务如果会把用户姓名、地址、病历、订单备注等拼入 system/developer 消息，应显式开启 system 规则引擎和 LLM 脱敏，或避免把用户数据放入 system。 

### 7.4 文本片段抽取

代理只处理文本内容，非文本 part 原样保留。解析请求时统一抽象为 `TextPart`，其中 `Ref` 保存原始 JSON 路径，确保脱敏后能原位写回：

```csharp
public sealed record TextPartRef(string Path);

public sealed class TextPart
{
    public required TextPartRef Ref { get; init; }
    public required string Text { get; set; }
    public required bool IsSystem { get; init; }
    public required Action<string> SetText { get; init; }
}
```

抽取规则：

| 协议 | 文本来源 | 非文本内容 |
|---|---|---|
| OpenAI Chat | `messages[].content` 为 string 时整体作为一个 `TextPart` | 原样保留 |
| OpenAI Chat 多模态 | `messages[].content[].type == "text"` 的 `text` 字段 | image/audio/file 等 part 原样保留 |
| Gemini | `contents[].parts[].text` | inlineData/functionCall/functionResponse 等 part 原样保留 |

后续规则引擎和 LLM 队列都基于 `TextPartRef` 回写，不能只用数组下标作为 key，避免多模态 part 位置变化时覆盖错内容。

### 7.5 Schema 清洗

去除云厂商不兼容的 JSON Schema 关键词（对标 ClawXRouter）。清洗必须按 provider 执行，避免把 OpenAI 可接受且有语义的 schema 约束无差别删除。

```csharp
public class SchemaCleaner : ISchemaCleaner
{
    private static readonly HashSet<string> CommonUnsupportedKeywords = new()
    {
        "patternProperties", "$schema", "$id",
        "$ref", "$defs", "definitions", "examples",
        "minLength", "maxLength", "minimum", "maximum",
        "multipleOf", "pattern", "format",
        "minItems", "maxItems", "uniqueItems",
        "minProperties", "maxProperties",
    };

    public JsonNode Clean(JsonNode tools, string provider)
    {
        // 递归清理：
        //   OpenAI 格式: tools[].function.parameters
        //   Gemini 格式: tools[].functionDeclarations[].parameters
        // additionalProperties 仅在目标 provider 明确不兼容时删除；默认保留，避免破坏 object map 语义。
        var unsupported = ResolveUnsupportedKeywords(provider);
        return StripKeywords(tools.DeepClone(), unsupported);
    }
}
```

### 7.6 Multi-Auth 与 Provider 边界

完整交付需要同时支持 **OpenAI-compatible upstream** 和已声明的 Anthropic、Gemini、Vertex 原生 API。Multi-Auth 只解决鉴权头，不等于 provider 协议转换；原生 API 必须经过 provider-specific transform 后才能直连。

```csharp
private static Dictionary<string, string> ResolveAuthHeaders(
    UpstreamTarget target)
{
    return target.Provider?.ToLower() switch
    {
        "openai-compatible" or "openai" or null => new()
        {
            ["Authorization"] = $"Bearer {target.ApiKey}",
        },
        "anthropic" => new()
        {
            ["x-api-key"] = target.ApiKey,
            ["anthropic-version"] = "2023-06-01",
        },
        "google" or "gemini" or "vertex" => new()
        {
            ["x-goog-api-key"] = target.ApiKey,
        },
        _ => throw new NotSupportedException($"Provider '{target.Provider}' is not supported")
    };
}
```

Provider transform 最小职责：

| Provider | Request transform | Response transform |
|---|---|---|
| OpenAI-compatible | 默认透传 OpenAI Chat/Responses 兼容格式 | 默认透传 |
| Anthropic 原生 | 转换 path、`messages/system/tools` 结构、stream 参数和 tool schema | 透传 Anthropic 原生响应给 Anthropic 客户端，并用 `x-desensitize-response-mode: provider-native` 标记；不伪装成 OpenAI 响应 |
| Gemini / Vertex 原生 | 转换 `contents[].parts[]`、`systemInstruction`、`tools.functionDeclarations`、query/header auth；原生 path 中的 model 来自客户端请求体 `model` 字段 | 透传 Gemini/Vertex 原生响应给对应客户端，并用 `x-desensitize-response-mode: provider-native` 标记；不伪装成 OpenAI 响应 |

客户端请求体里的 `model` 是最终上游调用使用的模型名，代理不能用配置文件写死模型覆盖它。`Proxy.Targets` 只保存上游连接信息（`BaseUrl` / `ApiKey` / `Provider`），建议一个协议一个 target，例如 `openai`、`anthropic`、`gemini`。目标选择先按请求协议/路径匹配 provider，匹配不到时使用 `Proxy.DefaultTarget`，不按 model 名选择 target。代理不根据敏感级别或运行时策略自动切换 provider。

```csharp
public interface IProviderTransformer
{
    JsonNode TransformRequest(JsonNode openAiLikeRequest, UpstreamTarget target);
    HttpResponseMessage TransformResponse(HttpResponseMessage upstreamResponse, UpstreamTarget target);
}
```

流式响应在 OpenAI-compatible upstream 下由 YARP 透传；原生 provider 也按 provider-native 模式透传给对应客户端。若未来要求“原生 provider 响应转换成 OpenAI 响应”，必须单独实现并验证 SSE event 名称、data payload、结束标记和错误事件，不能只改请求头。

### 7.7 流式转发

**这是一个请求改写代理，不是透明 TCP 代理**。流式体现在两个方面：
- **请求端**：代理接收完整请求体 → 脱敏 → 重建 → 转发到上游
- **响应端**：YARP 原生支持 SSE streaming 透传

首 token 延迟预期（优化后）：
- Regex 脱敏层：典型短消息 <1ms；长 prompt / 大段代码 / 日志按请求体大小线性增长
- LLM 脱敏层：0~TimeoutMs（缓存命中 0s，并发 + TimeoutMs 单条上限）
- 总增量：<1ms ~ TimeoutMs；建议配置 `MaxBodySizeBytes` / `MaxTextPartLengthForLlm` 防止大请求拖垮代理

---

## 8. 配置设计

### 8.1 Options 类

```csharp
public class PrivacyConfig
{
    public const string SectionName = "PrivacyProxy";

    public bool Enabled { get; set; } = true;
    public bool StrictMode { get; set; } = false;
    public string? PromptPath { get; set; }
    public long MaxBodySizeBytes { get; set; } = 16 * 1024 * 1024;
    public int MaxTextPartLengthForLlm { get; set; } = 8_000;

    public LocalModelConfig LocalModel { get; set; } = new();
    public KeywordConfig Keywords { get; set; } = new();
    public RedactionConfig Redaction { get; set; } = new();
    public SystemMessageRedactionConfig SystemMessageRedaction { get; set; } = new();
    public ProxyConfig Proxy { get; set; } = new();
    public RuntimeConfig Runtime { get; set; } = new();
    public ObservabilityConfig Observability { get; set; } = new();
}

public class LocalModelConfig
{
    public bool Enabled { get; set; } = true;
    public string Type { get; set; } = "openai-compatible";  // "ollama-native" / "custom"
    public string Provider { get; set; } = "ollama";
    public string Model { get; set; } = "openbmb/minicpm4.1";
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string? ApiKey { get; set; }
    public int TimeoutMs { get; set; } = 5_000;
    public int MaxConcurrency { get; set; } = 4;
}

public class KeywordConfig
{
    public List<string> TriggerKeywords { get; set; } = [ /* 见 5.3 */ ];
    public List<string> HintKeywords { get; set; } = [ /* 见 5.3 */ ];
}

public class RedactionConfig
{
    // Phase 2 默认开启
    public bool ChinesePhone   { get; set; } = true;
    public bool ChineseId      { get; set; } = true;
    public bool Email          { get; set; } = true;

    // Phase 2 默认关闭
    public bool InternalIp     { get; set; } = false;
    public bool CreditCard     { get; set; } = false;
    public bool ChineseAddress { get; set; } = false;
    public bool EnvVar         { get; set; } = false;
}

public class SystemMessageRedactionConfig
{
    public bool Phase1 { get; set; } = true;
    public bool Phase2 { get; set; } = true;
    public bool Phase3 { get; set; } = false;
    public bool RuleEngine { get; set; } = false;
    public bool Llm { get; set; } = false;
}

public class ProxyConfig
{
    public int Port { get; set; } = 8403;
    public string BindAddress { get; set; } = "127.0.0.1";
    public string? DefaultTarget { get; set; }
    public Dictionary<string, UpstreamTarget> Targets { get; set; } = [];
}

public class UpstreamTarget
{
    public required string BaseUrl { get; set; }
    public required string ApiKey { get; set; }
    public string? Provider { get; set; }  // "openai" / "openai-compatible" / "anthropic" / "google" / "gemini" / "vertex"
}

public class RuntimeConfig
{
    public string? DataDirectory { get; set; }
    public string? LogDirectory { get; set; }
    public bool RunAsService { get; set; } = false;
}

public class ObservabilityConfig
{
    public bool MetricsEnabled { get; set; } = true;
    public bool HealthCheckEnabled { get; set; } = true;
    public string HealthCheckPath { get; set; } = "/health";
}
```

### 8.2 appsettings.json

```json
{
  "PrivacyProxy": {
    "Enabled": true,
    "StrictMode": false,
    "PromptPath": null,
    "MaxBodySizeBytes": 16777216,
    "MaxTextPartLengthForLlm": 8000,
    "LocalModel": {
      "Enabled": true,
      "Type": "openai-compatible",
      "Provider": "ollama",
      "Model": "openbmb/minicpm4.1",
      "Endpoint": "http://localhost:11434",
      "TimeoutMs": 5000,
      "MaxConcurrency": 4
    },
    "Keywords": {
      "TriggerKeywords": [
        "身份证号", "手机号", "住址", "密码是", "取件码", "门禁码"
      ],
      "HintKeywords": [
        "身份证", "salary", "工资单", "password", "密码"
      ]
    },
    "Redaction": {
      "ChinesePhone": true,
      "ChineseId": true,
      "Email": true,
      "CreditCard": false,
      "ChineseAddress": false
    },
    "SystemMessageRedaction": {
      "Phase1": true,
      "Phase2": true,
      "Phase3": false,
      "RuleEngine": false,
      "Llm": false
    },
    "Proxy": {
      "Port": 8403,
      "BindAddress": "127.0.0.1",
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
        "openai-compatible-gateway": {
          "BaseUrl": "https://your-openai-compatible-gateway.example.com/v1",
          "ApiKey": "${GATEWAY_API_KEY}",
          "Provider": "openai-compatible"
        }
      }
    },
    "Runtime": {
      "DataDirectory": null,
      "LogDirectory": null,
      "RunAsService": false
    },
    "Observability": {
      "MetricsEnabled": true,
      "HealthCheckEnabled": true,
      "HealthCheckPath": "/health"
    }
  }
}
```

### 8.3 热更新

```csharp
// Program.cs
builder.Services.Configure<PrivacyConfig>(
    builder.Configuration.GetSection("PrivacyProxy"));

// 中间件注入 IOptionsMonitor<PrivacyConfig>，OnChange 自动生效
// PromptLoader 额外注册 OnChange 回调以刷新 Prompt 缓存
```

### 8.4 DI 注册

```csharp
// Program.cs — 一行注册
builder.Services.AddDesensitizeProxy(builder.Configuration);

// 扩展方法自动注册:
//   IRuleEngine → RuleEngine (Singleton)
//   ILlmDesensitizer → LlmDesensitizer (Scoped)
//   IPiiRedactor → PiiRedactor (Singleton)
//   ISchemaCleaner → SchemaCleaner (Singleton)
//   IDesensitizeCache → DesensitizeCache (Singleton)
//   LocalModelClient → 默认 HTTP OpenAI-compatible / Ollama-native client (Singleton)
//   IChatClientPiiExtractionClient → 可替换 IChatClient 适配器
//   PromptLoader → PromptLoader (Singleton)
//   DesensitizeMetrics → DesensitizeMetrics (Singleton)
//   DesensitizeProxyMiddleware → DesensitizeProxyMiddleware (Scoped)
//   YARP Forwarder → builder.Services.AddHttpForwarder()
```

---

## 9. 可观测性

### 9.1 健康检查

```
GET /health

→ 200: {
    "status": "healthy",
    "regex": "ok",
    "llm": "connected",
    "llm_model": "openbmb/minicpm4.1",
    "cache_entries": 42
  }

→ 503: {
    "status": "degraded",
    "regex": "ok",
    "llm": "timeout",
    "llm_endpoint": "http://localhost:11434"
  }
```

### 9.2 Metrics

| 指标名 | 类型 | 说明 |
|---|---|---|
| `desensitize_requests_total` | Counter | 总请求数 |
| `regex_hits_total` | Counter | Regex 命中次数（label: `phase=phase1\|phase2\|phase3`） |
| `llm_desensitize_calls_total` | Counter | LLM 脱敏调用次数 |
| `llm_desensitize_duration_seconds` | Histogram | LLM 脱敏耗时分布 |
| `llm_failures_total` | Counter | LLM 脱敏失败次数（label: `reason=timeout\|error`） |
| `llm_cache_hits_total` | Counter | 脱敏缓存命中次数 |
| `strict_mode_blocks_total` | Counter | StrictMode 阻断次数 |

### 9.3 结构化日志

```csharp
// 正常流程
_logger.LogInformation(
    "Request desensitized: {Messages} msgs, {RegexHits} regex hits, "
    + "{LlmCalls} LLM calls ({CacheHits} cached), {TotalMs}ms total",
    messageCount, regexHits, llmCalls, cacheHits, elapsedMs);

// 降级告警
_logger.LogWarning(
    "LLM desensitize degraded: {Failures}/{Total} tasks failed. "
    + "Falling back to regex-only for {AffectedMessages} messages",
    failures, total, affectedMessageCount);

// LLM 超时
_logger.LogWarning(
    "LLM desensitize timeout: content length {Length} chars, "
    + "model {Model} at {Endpoint}",
    contentLength, model, endpoint);
```

---

## 附录 A：文件清单

```
src/
├── DesensitizeProxy.Core/
│   ├── Abstractions/
│   │   ├── IRuleEngine.cs
│   │   ├── ILlmDesensitizer.cs
│   │   ├── ILocalPiiExtractionClient.cs
│   │   ├── ILocalModelHealthProbe.cs
│   │   ├── IPiiRedactor.cs
│   │   ├── ISchemaCleaner.cs
│   │   └── IDesensitizeCache.cs
│   ├── Models/
│   │   ├── DetectionResult.cs           # RuleHitLevel 枚举 + DetectionResult
│   │   ├── DesensitizeResult.cs
│   │   ├── DesensitizeMetrics.cs        # 7 个 Counter/Histogram
│   │   └── PrivacyConfig.cs             # 完整 Options 类族
│   ├── Engine/
│   │   ├── RuleEngine.cs                # 三级命中判定
│   │   ├── GeneratedRegexes.cs          # [GeneratedRegex] Phase 1 + 2
│   │   └── DynamicRegexes.cs            # Phase 3 动态正则 + LRU 缓存
│   ├── Llm/
│   │   ├── LocalModelClient.cs          # OpenAI / Ollama HTTP 客户端
│   │   ├── IChatClientPiiExtractionClient.cs # Microsoft.Extensions.AI IChatClient 适配器
│   │   ├── LocalModelHealthProbe.cs     # 本地模型健康探针
│   │   ├── ConfigFingerprintProvider.cs # Prompt/配置指纹
│   │   ├── LlmDesensitizer.cs           # 两步法 + 并发 + 缓存 + 降级
│   │   ├── DesensitizeCache.cs          # SHA256 + MemoryCache
│   │   ├── PromptLoader.cs              # 三级回退加载
│   │   ├── PiiJsonParser.cs             # LLM JSON 容错解析
│   │   └── PiiTypeMapper.cs             # type → [REDACTED:XX]
│   ├── Redaction/
│   │   ├── PiiRedactor.cs               # Phase 1/2/3 编排 + 中文关键词
│   │   └── SchemaCleaner.cs             # JSON Schema 关键词清洗
│   ├── Runtime/
│   │   └── RuntimeDirectoryResolver.cs  # 跨平台运行目录解析
│   └── Extensions/
│       └── ServiceCollectionExtensions.cs  # AddDesensitizeProxyCore()
│
├── DesensitizeProxy.AspNetCore/
│   ├── Middleware/
│   │   ├── DesensitizeProxyMiddleware.cs   # 唯一中间件入口
│   │   ├── TextPartExtractor.cs            # OpenAI/Responses/Gemini 文本抽取
│   │   └── TextPart.cs
│   ├── Auth/
│   │   └── MultiAuthHandler.cs             # OpenAI-compatible / Anthropic / Google 鉴权
│   ├── Health/
│   │   └── HealthCheckEndpoint.cs          # /health 端点
│   ├── Yarp/
│   │   ├── UpstreamResolver.cs             # model/默认上游选择
│   │   ├── DesensitizeForwarderTransformer.cs
│   │   ├── ProviderTransformers.cs         # OpenAI/Anthropic/Gemini/Vertex 转换
│   │   └── ProxyTransformer.cs
│   └── Extensions/
│       └── ServiceCollectionExtensions.cs  # AddDesensitizeProxy()
│
└── tests/
    └── DesensitizeProxy.Core.Tests/
        ├── PiiRedactorTests.cs            # 含中文关键词专项测试
        ├── RuleEngineTests.cs             # 含三级命中专项测试
        ├── LlmDesensitizerTests.cs        # 含降级行为测试
        ├── HttpPipelineTests.cs           # 中间件/YARP 管道测试
        ├── ProviderTransformerTests.cs     # provider-native 请求/响应边界
        ├── TextPartExtractorTests.cs       # 文本抽取协议覆盖
        ├── LocalModelClientTests.cs
        ├── IChatClientPiiExtractionClientTests.cs
        ├── HealthProbeTests.cs
        ├── DesensitizeMetricsTests.cs
        ├── PromptAndFingerprintTests.cs
        ├── RuntimeDirectoryResolverTests.cs
        ├── MultiAuthHandlerTests.cs
        ├── SchemaCleanerTests.cs
        ├── PiiJsonParserTests.cs
        └── IntegrationTests.cs
```

## 附录 B：部署模式

```
方案 A: 独立微服务
  ┌──────┐     ┌─────────────────┐     ┌──────────┐
  │ Client│ ──▶│ DesensitizeProxy │ ──▶│ Cloud LLM │
  └──────┘     │  (localhost:8403)│     └──────────┘
               └─────────────────┘

方案 B: Sidecar 模式
  ┌──────────────────────────────────┐
  │          应用 Pod                 │
  │  ┌────────┐  ┌─────────────────┐ │
  │  │ App     │─▶│ DesensitizeProxy│ │────▶ Cloud
  │  └────────┘  └─────────────────┘ │
  └──────────────────────────────────┘

方案 C: 嵌入式中间件
  builder.Services.AddDesensitizeProxy();
  // 直接插入现有 ASP.NET Core 管道
```

### B.1 跨平台运行方式

| 平台 | 推荐方式 | 说明 |
|---|---|---|
| macOS | `dotnet publish -r osx-arm64/osx-x64` + `launchd` | 作为用户级或系统级 LaunchAgent/Daemon 运行，配置路径通过 `appsettings.json` 或环境变量传入。|
| Linux | `dotnet publish -r linux-x64/linux-arm64` + `systemd` | 适合服务器和 sidecar；用 `EnvironmentFile` 注入 API Key，工作目录固定为发布目录。|
| Windows | `dotnet publish -r win-x64/win-arm64` + Windows Service | 使用 `UseWindowsService()` 集成服务生命周期，路径解析必须兼容盘符和反斜杠。|
| Docker | 多架构镜像 | 覆盖 Linux 容器场景；挂载配置、prompt、证书目录，不把 API Key bake 进镜像。|

### B.2 默认目录策略

未显式配置 `Runtime.DataDirectory` 时，按平台选择用户级数据目录；未显式配置 `Runtime.LogDirectory` 时，日志写入应用基目录，也就是 `DesensitizeProxy.AspNetCore.dll` 所在目录，便于本地审查：

| 平台 | 默认数据目录 | 默认日志目录 |
|---|---|---|
| macOS | `~/Library/Application Support/ClawXRouter/PrivacyProxy` | `DesensitizeProxy.AspNetCore.dll` 所在目录 |
| Linux | `${XDG_DATA_HOME:-~/.local/share}/clawxrouter/privacy-proxy` | `DesensitizeProxy.AspNetCore.dll` 所在目录 |
| Windows | `%LOCALAPPDATA%\\ClawXRouter\\PrivacyProxy` | `DesensitizeProxy.AspNetCore.dll` 所在目录 |

Prompt 加载、缓存和日志写入都必须先经过目录解析层，不允许业务代码直接拼接平台特定路径。

### B.3 发布产物

最小发布目标：

```bash
dotnet publish src/DesensitizeProxy.AspNetCore \
  -c Release \
  -r <runtime-identifier> \
  --self-contained false
```

需要无运行时依赖时可发布 self-contained 版本，但要分别产出 `osx-arm64`、`osx-x64`、`linux-x64`、`linux-arm64`、`win-x64`、`win-arm64`。是否启用 single-file / trimming 需要单独验证 YARP、Options 绑定、`System.Text.Json`、`Microsoft.Extensions.AI` 和本地模型客户端，不能默认开启。
