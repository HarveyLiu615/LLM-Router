using System.Text.RegularExpressions;

namespace DesensitizeProxy.Core.Engine;

internal static partial class GeneratedRegexes
{
    [GeneratedRegex("-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----[\\s\\S]*?-----END (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    public static partial Regex PrivateKey();

    [GeneratedRegex("AKIA[0-9A-Z]{16}")]
    public static partial Regex AwsKey();

    [GeneratedRegex("(?:mysql|postgres|postgresql|mongodb|redis|amqp)://[^\\s\"']+", RegexOptions.IgnoreCase)]
    public static partial Regex DbConnection();

    [GeneratedRegex("(?:快递单号|运单号|取件码)[：:\\s]*[A-Za-z0-9]{6,20}")]
    public static partial Regex DeliveryCode();

    [GeneratedRegex("(?:门禁码|门禁密码|开门密码|门锁密码)[：:\\s]*[A-Za-z0-9#*]{3,12}")]
    public static partial Regex AccessCode();

    [GeneratedRegex("(?<!\\d)1[3-9]\\d{9}(?!\\d)")]
    public static partial Regex ChinesePhone();

    [GeneratedRegex("(?<!\\d)\\d{17}[\\dXx](?!\\d)")]
    public static partial Regex ChineseId();

    [GeneratedRegex("[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}")]
    public static partial Regex Email();

    [GeneratedRegex("(?<!\\d)(?:10|172\\.(?:1[6-9]|2\\d|3[01])|192\\.168)(?:\\.\\d{1,3}){2}(?!\\d)")]
    public static partial Regex InternalIp();

    [GeneratedRegex("(?<!\\d)(?:\\d[ -]*?){13,19}(?!\\d)")]
    public static partial Regex CreditCardCandidate();

    [GeneratedRegex("""(?i)(?:[A-Z_][A-Z0-9_]*_(?:KEY|TOKEN|SECRET|PASSWORD)|(?:KEY|TOKEN|SECRET|PASSWORD))[\s=:"]+[A-Za-z0-9_./+=-]{6,}""")]
    public static partial Regex EnvVarSecret();

    [GeneratedRegex("(?:北京市|上海市|天津市|重庆市|[\\u4e00-\\u9fa5]{2,}省|[\\u4e00-\\u9fa5]{2,}市|[\\u4e00-\\u9fa5]{2,}区|[\\u4e00-\\u9fa5]{2,}县)[\\u4e00-\\u9fa5A-Za-z0-9号弄栋单元室路街道巷-]{4,}")]
    public static partial Regex ChineseAddress();
}
