using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using RentalServer.Core.Rates;

namespace RentalServer;

public partial class RateEditorWindow : Window
{
    private const int MaxSecondsPerCoin = 24 * 60 * 60;

    public RateSheet? EditedRateSheet { get; private set; }

    public RateEditorWindow(RateSheet currentSheet)
    {
        InitializeComponent();
        Populate(currentSheet);
    }

    private void Populate(RateSheet sheet)
    {
        SetTextBox(RatePcD1, sheet.AddTimeRates, "pc", 1);
        SetTextBox(RatePcD5, sheet.AddTimeRates, "pc", 5);
        SetTextBox(RatePcD10, sheet.AddTimeRates, "pc", 10);
        SetTextBox(RatePcD20, sheet.AddTimeRates, "pc", 20);
        SetTextBox(RatePhnD1, sheet.AddTimeRates, "phn", 1);
        SetTextBox(RatePhnD5, sheet.AddTimeRates, "phn", 5);
        SetTextBox(RatePhnD10, sheet.AddTimeRates, "phn", 10);
        SetTextBox(RatePhnD20, sheet.AddTimeRates, "phn", 20);
        SetTextBox(RateTabD1, sheet.AddTimeRates, "tab", 1);
        SetTextBox(RateTabD5, sheet.AddTimeRates, "tab", 5);
        SetTextBox(RateTabD10, sheet.AddTimeRates, "tab", 10);
        SetTextBox(RateTabD20, sheet.AddTimeRates, "tab", 20);

        SetRuleTextBoxes("pc", sheet.InsertCoinRules, RulePcStarterMax, RulePcStarterSeconds, RulePcNormalizedFrom, RulePcPerPesoSeconds);
        SetRuleTextBoxes("phn", sheet.InsertCoinRules, RulePhnStarterMax, RulePhnStarterSeconds, RulePhnNormalizedFrom, RulePhnPerPesoSeconds);
        SetRuleTextBoxes("tab", sheet.InsertCoinRules, RuleTabStarterMax, RuleTabStarterSeconds, RuleTabNormalizedFrom, RuleTabPerPesoSeconds);
    }

    private static void SetTextBox(
        TextBox textBox,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> rates,
        string device,
        int denom)
    {
        textBox.Text = rates.TryGetValue(device, out var perDenom) &&
                       perDenom.TryGetValue(denom, out var seconds)
            ? FormatDuration(seconds)
            : string.Empty;
    }

    private static void SetRuleTextBoxes(
        string device,
        IReadOnlyDictionary<string, InsertCoinRateRule> rules,
        TextBox starterMax,
        TextBox starterSeconds,
        TextBox normalizedFrom,
        TextBox perPesoSeconds)
    {
        if (!rules.TryGetValue(device, out var rule))
        {
            return;
        }

        starterMax.Text = rule.StarterMaxPesos.ToString(CultureInfo.InvariantCulture);
        starterSeconds.Text = FormatDuration(rule.StarterSeconds);
        normalizedFrom.Text = rule.NormalizedFromPesos.ToString(CultureInfo.InvariantCulture);
        perPesoSeconds.Text = FormatDuration(rule.PerPesoSeconds);
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var addTimeRates = new Dictionary<string, IReadOnlyDictionary<int, int>>(StringComparer.Ordinal)
            {
                ["pc"] = ParseAddTimeRates("PC", RatePcD1, RatePcD5, RatePcD10, RatePcD20),
                ["phn"] = ParseAddTimeRates("Phone", RatePhnD1, RatePhnD5, RatePhnD10, RatePhnD20),
                ["tab"] = ParseAddTimeRates("Tablet", RateTabD1, RateTabD5, RateTabD10, RateTabD20)
            };

            var insertCoinRules = new Dictionary<string, InsertCoinRateRule>(StringComparer.Ordinal)
            {
                ["pc"] = ParseRule("PC", RulePcStarterMax, RulePcStarterSeconds, RulePcNormalizedFrom, RulePcPerPesoSeconds),
                ["phn"] = ParseRule("Phone", RulePhnStarterMax, RulePhnStarterSeconds, RulePhnNormalizedFrom, RulePhnPerPesoSeconds),
                ["tab"] = ParseRule("Tablet", RuleTabStarterMax, RuleTabStarterSeconds, RuleTabNormalizedFrom, RuleTabPerPesoSeconds)
            };

            EditedRateSheet = new RateSheet(addTimeRates, insertCoinRules);
            DialogResult = true;
            Close();
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static IReadOnlyDictionary<int, int> ParseAddTimeRates(
        string device,
        TextBox d1,
        TextBox d5,
        TextBox d10,
        TextBox d20)
    {
        return new Dictionary<int, int>
        {
            [1] = ParseDuration(d1.Text, $"{device} / PHP 1"),
            [5] = ParseDuration(d5.Text, $"{device} / PHP 5"),
            [10] = ParseDuration(d10.Text, $"{device} / PHP 10"),
            [20] = ParseDuration(d20.Text, $"{device} / PHP 20")
        };
    }

    private static InsertCoinRateRule ParseRule(
        string device,
        TextBox starterMax,
        TextBox starterSeconds,
        TextBox normalizedFrom,
        TextBox perPesoSeconds)
    {
        var rule = new InsertCoinRateRule(
            StarterMaxPesos: ParsePositiveInt(starterMax.Text, $"{device} locked starter unlock PHP"),
            StarterSeconds: ParseDuration(starterSeconds.Text, $"{device} locked starter time"),
            NormalizedFromPesos: ParsePositiveInt(normalizedFrom.Text, $"{device} locked normalize-from PHP"),
            PerPesoSeconds: ParseDuration(perPesoSeconds.Text, $"{device} locked per-PHP time"));

        if (rule.NormalizedFromPesos <= rule.StarterMaxPesos)
        {
            throw new ArgumentException($"{device}: normalize-from PHP must be greater than starter unlock PHP.");
        }

        return rule;
    }

    private static int ParsePositiveInt(string text, string label)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"{label}: invalid number '{trimmed}'.");
        }

        if (value <= 0)
        {
            throw new ArgumentException($"{label}: must be greater than 0 (got {value}).");
        }

        return value;
    }

    private static int ParseDuration(string text, string label)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        var parts = trimmed.Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            hours < 0 ||
            minutes is < 0 or > 59 ||
            seconds is < 0 or > 59)
        {
            throw new ArgumentException($"{label}: enter time as HH:MM:SS.");
        }

        var totalSecondsLong = ((long)hours * 3600) + (minutes * 60) + seconds;
        if (totalSecondsLong > int.MaxValue)
        {
            throw new ArgumentException($"{label}: too large.");
        }

        var totalSeconds = (int)totalSecondsLong;
        if (totalSeconds <= 0)
        {
            throw new ArgumentException($"{label}: must be greater than 00:00:00.");
        }

        if (totalSeconds > MaxSecondsPerCoin)
        {
            throw new ArgumentException($"{label}: too large ({FormatDuration(totalSeconds)} > {FormatDuration(MaxSecondsPerCoin)}).");
        }

        return totalSeconds;
    }

    private static string FormatDuration(int totalSeconds)
    {
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
    }
}
