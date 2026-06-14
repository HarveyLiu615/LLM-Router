namespace DesensitizeProxy.Core.Llm;

public static class PiiTypeMapper
{
    public static string ToRedactionLabelName(string type)
    {
        var label = ToRedactionLabel(type);
        return label[10..^1];
    }

    public static string ToRedactionLabel(string type)
    {
        var normalized = type.Trim().ToUpperInvariant();
        return normalized switch
        {
            "NAME" or "SENDER_NAME" or "RECIPIENT_NAME" => "[REDACTED:NAME]",
            "PHONE" or "MOBILE" or "LANDLINE" => "[REDACTED:PHONE]",
            "ADDRESS" => "[REDACTED:ADDRESS]",
            "PASSWORD" or "API_KEY" or "TOKEN" or "SECRET" => "[REDACTED:SECRET]",
            "ID" or "ID_CARD" or "ID_NUMBER" => "[REDACTED:ID]",
            "CARD" or "BANK_CARD" or "CARD_NUMBER" => "[REDACTED:CARD]",
            "ACCESS_CODE" or "DELIVERY" or "COURIER_NUMBER" => "[REDACTED:DELIVERY]",
            "EMAIL" => "[REDACTED:EMAIL]",
            "IP" => "[REDACTED:IP]",
            "LICENSE_PLATE" or "PLATE" => "[REDACTED:LICENSE]",
            "SALARY" or "AMOUNT" => "[REDACTED:AMOUNT]",
            _ => $"[REDACTED:{SanitizeType(normalized)}]"
        };
    }

    private static string SanitizeType(string type)
    {
        var chars = type.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray();
        return chars.Length == 0 ? "UNKNOWN" : new string(chars);
    }
}
