using System.Text;

namespace AIrtist.Core;

public static class InputRules
{
    public static string Canonical(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().EnumerateRunes().Where(Rune.IsLetterOrDigit).Select(r => r.ToString()));

    public static string Validate(string description, Word word)
    {
        string normalized = ValidateDescription(description);
        RejectAlias(normalized, word);
        return normalized;
    }

    public static string ValidateTranslation(string translation, Word word)
    {
        string normalized = ValidateTranslation(translation);
        RejectAlias(normalized, word);
        return normalized;
    }

    public static string ValidateDescription(string description)
        => ValidateText(description, 160, "묘사는 2~160자로 입력해 주세요.");

    public static string ValidateTranslation(string translation)
    {
        string normalized = ValidateText(translation, 320, "확정할 영어 묘사는 2~320자로 입력해 주세요.");
        if (System.Text.RegularExpressions.Regex.IsMatch(normalized, @"[\p{IsHangulSyllables}\p{IsHangulJamo}\p{IsHangulCompatibilityJamo}]"))
            throw new ArgumentException("번역 확인란에는 영어로 묘사해 주세요. 한국어 원문은 다시 작성할 수 있어요.");
        return normalized;
    }

    private static string ValidateText(string text, int limit, string lengthMessage)
    {
        string normalized = text.Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.Length < 2 || normalized.Length > limit || string.IsNullOrWhiteSpace(Canonical(normalized)))
            throw new ArgumentException(lengthMessage);
        if (normalized.Contains("<|") || normalized.Contains("|>"))
            throw new ArgumentException("특수 토큰은 입력할 수 없습니다.");
        return normalized;
    }

    private static void RejectAlias(string text, Word word)
    {
        if (ContainsAlias(text, word)) throw new ArgumentException("제시어와 금지 별칭을 쓰지 않고 묘사해 주세요.");
    }

    public static bool ContainsAlias(string description, Word word)
    {
        string normalized = Canonical(description);
        return word.Aliases.Any(alias => normalized.Contains(Canonical(alias), StringComparison.Ordinal));
    }
}
