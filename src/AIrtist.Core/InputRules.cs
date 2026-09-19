using System.Text;

namespace AIrtist.Core;

public static class InputRules
{
    public static string Canonical(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().EnumerateRunes().Where(Rune.IsLetterOrDigit).Select(r => r.ToString()));

    public static string Validate(string description, Word word)
    {
        string normalized = description.Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.Length is < 2 or > 160 || string.IsNullOrWhiteSpace(Canonical(normalized)))
            throw new ArgumentException("묘사는 2~160자로 입력해 주세요.");
        if (normalized.Contains("<|") || normalized.Contains("|>"))
            throw new ArgumentException("특수 토큰은 입력할 수 없습니다.");
        if (ContainsAlias(normalized, word))
            throw new ArgumentException("제시어와 금지 별칭을 쓰지 않고 묘사해 주세요.");
        return normalized;
    }

    public static bool ContainsAlias(string description, Word word)
    {
        string normalized = Canonical(description);
        return word.Aliases.Any(alias => normalized.Contains(Canonical(alias), StringComparison.Ordinal));
    }
}
