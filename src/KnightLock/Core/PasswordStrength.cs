using System;
using System.Collections.Generic;
using System.Linq;

namespace KnightLock.Core;

public sealed record PasswordAssessment(int Score, string Label, string Hint, double EntropyBits, bool MeetsMinimum)
{
    public static readonly string[] Labels = { "Very weak", "Weak", "Fair", "Strong", "Excellent" };
}

/// <summary>
/// Offline password strength estimator (no network calls, nothing leaves the machine).
/// Scores 0-4 from character-class entropy with penalties for the patterns that
/// real-world password cracking eats for breakfast.
/// </summary>
public static class PasswordStrength
{
    public const int MinimumLength = 8;

    private static readonly string[] CommonPatterns =
    {
        "password", "qwerty", "123456", "letmein", "welcome", "admin", "iloveyou",
        "monkey", "dragon", "abc123", "passw0rd", "vault", "knight", "lock"
    };

    public static PasswordAssessment Evaluate(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return new PasswordAssessment(0, PasswordAssessment.Labels[0], "Enter a master password.", 0, false);

        int length = password.Length;
        bool lower = password.Any(char.IsLower);
        bool upper = password.Any(char.IsUpper);
        bool digit = password.Any(char.IsDigit);
        bool symbol = password.Any(c => !char.IsLetterOrDigit(c));
        bool nonAscii = password.Any(c => c > 127);

        int classes = (lower ? 1 : 0) + (upper ? 1 : 0) + (digit ? 1 : 0) + (symbol ? 1 : 0) + (nonAscii ? 1 : 0);
        double alphabet = 0;
        if (lower) alphabet += 26;
        if (upper) alphabet += 26;
        if (digit) alphabet += 10;
        if (symbol) alphabet += 33;
        if (nonAscii) alphabet += 100;
        if (alphabet <= 0) alphabet = 26;

        double entropy = length * Math.Log2(alphabet);

        // Penalties
        string lowered = password.ToLowerInvariant();
        if (CommonPatterns.Any(p => lowered.Contains(p, StringComparison.Ordinal)))
            entropy -= 30;
        if (IsRepeatedOrSequential(lowered))
            entropy -= 20;
        int distinct = password.Distinct().Count();
        if (distinct <= Math.Max(2, length / 3))
            entropy -= 15;
        if (length < MinimumLength)
            entropy -= 20;

        // Character-diversity ceiling: "aaaaaaaaaaaaaaaa" is not a 75-bit secret.
        int distinctCount = password.Distinct().Count();
        if (distinctCount <= 2) entropy = Math.Min(entropy, 15);
        else if (distinctCount <= 4) entropy = Math.Min(entropy, 40);
        else if (distinctCount <= 6) entropy = Math.Min(entropy, 60);

        entropy = Math.Max(0, entropy);

        int score = entropy switch
        {
            >= 90 => 4,
            >= 70 => 3,
            >= 50 => 2,
            >= 30 => 1,
            _ => 0
        };

        // A password that cannot be typed comfortably should never rate as "Excellent".
        if (length < 12 && score == 4) score = 3;

        bool meets = length >= MinimumLength && (classes >= 2 || length >= 14) && score >= 2;

        string hint = score switch
        {
            4 => "Excellent — this will resist offline brute force.",
            3 => "Strong. 4+ random words or 14+ mixed characters is the sweet spot.",
            2 => "Fair. Add length or another character class.",
            1 => "Weak. Use at least 12 characters mixing cases, digits and symbols.",
            _ => "Too weak. Use a long, unique passphrase you don't use anywhere else."
        };

        if (!meets && string.IsNullOrEmpty(password) == false)
            hint = length < MinimumLength
                ? $"At least {MinimumLength} characters are required."
                : hint;

        return new PasswordAssessment(score, PasswordAssessment.Labels[Math.Clamp(score, 0, 4)],
            hint, Math.Round(entropy, 1), meets);
    }

    private static bool IsRepeatedOrSequential(string s)
    {
        if (s.Length < 3) return false;

        bool allSame = s.All(c => c == s[0]);
        if (allSame) return true;

        int ascending = 0;
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] - s[i - 1] == 1) ascending++;
            else ascending = 0;
            if (ascending >= 3) return true;
        }
        return false;
    }
}
