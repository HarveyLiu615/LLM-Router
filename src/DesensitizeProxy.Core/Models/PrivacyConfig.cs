namespace DesensitizeProxy.Core.Models;

public sealed class PrivacyConfig
{
    public const string SectionName = "PrivacyProxy";
    public const long DefaultMaxBodySizeBytes = 16 * 1024 * 1024;

    public bool Enabled { get; set; } = true;
    public bool StrictMode { get; set; } = false;
    public string? PromptPath { get; set; }
    public long MaxBodySizeBytes { get; set; } = DefaultMaxBodySizeBytes;
    public int MaxTextPartLengthForLlm { get; set; } = 8_000;

    public LocalModelConfig LocalModel { get; set; } = new();
    public KeywordConfig Keywords { get; set; } = new();
    public RedactionConfig Redaction { get; set; } = new();
    public SystemMessageRedactionConfig SystemMessageRedaction { get; set; } = new();
    public ProxyConfig Proxy { get; set; } = new();
    public RuntimeConfig Runtime { get; set; } = new();
    public ObservabilityConfig Observability { get; set; } = new();
}

public sealed class LocalModelConfig
{
    public bool Enabled { get; set; } = true;
    public string Type { get; set; } = "openai-compatible";
    public string Provider { get; set; } = "ollama";
    public string Model { get; set; } = "openbmb/minicpm4.1";
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string? ApiKey { get; set; }
    public int TimeoutMs { get; set; } = 5_000;
    public int MaxConcurrency { get; set; } = 4;
}

public sealed class KeywordConfig
{
    public List<string> TriggerKeywords { get; set; } =
    [
        "身份证号", "手机号", "电话号码", "住址", "门牌号", "家庭地址",
        "密码是", "密码为", "密码：", "password is", "password:",
        "取件码", "门禁码", "开门密码", "快递单号", "运单号",
        "api_key", "access_key", "secret_key", "private key"
    ];

    public List<string> HintKeywords { get; set; } =
    [
        "身份证", "SSN", "social security",
        "salary", "工资单", "工资条", "payslip", "税表", "tax return",
        "银行卡", "bank account", "credit card",
        "病历", "medical record", "体检报告", "处方",
        "地址", "address", "passport", "护照",
        "password", "密码", "secret", "私钥", "token"
    ];
}

public sealed class RedactionConfig
{
    public bool ChinesePhone { get; set; } = true;
    public bool ChineseId { get; set; } = true;
    public bool Email { get; set; } = true;

    public bool InternalIp { get; set; } = false;
    public bool CreditCard { get; set; } = false;
    public bool ChineseAddress { get; set; } = false;
    public bool EnvVar { get; set; } = false;
}

public sealed class SystemMessageRedactionConfig
{
    public bool Phase1 { get; set; } = true;
    public bool Phase2 { get; set; } = true;
    public bool Phase3 { get; set; } = false;
    public bool RuleEngine { get; set; } = false;
    public bool Llm { get; set; } = false;
}

public sealed class ProxyConfig
{
    public int Port { get; set; } = 8403;
    public string BindAddress { get; set; } = "127.0.0.1";
    public string? DefaultTarget { get; set; }
    public Dictionary<string, UpstreamTarget> Targets { get; set; } = [];
}

public sealed class UpstreamTarget
{
    public required string BaseUrl { get; set; }
    public required string ApiKey { get; set; }
    public string? Provider { get; set; }
    public string? ResponsesCompatibility { get; set; }
    public List<ResponsesCompatibilityRule> ResponsesCompatibilityRules { get; set; } = [];
}

public sealed class ResponsesCompatibilityRule
{
    public string? ModelPattern { get; set; }
    public string? Mode { get; set; }
}

public sealed class RuntimeConfig
{
    public string? DataDirectory { get; set; }
    public string? LogDirectory { get; set; }
    public bool RunAsService { get; set; } = false;
}

public sealed class ObservabilityConfig
{
    public bool MetricsEnabled { get; set; } = true;
    public bool HealthCheckEnabled { get; set; } = true;
    public string HealthCheckPath { get; set; } = "/health";
    public bool RedactionLoggingEnabled { get; set; } = true;
    public bool RedactionLogIncludeValues { get; set; } = true;
}
