using System.Globalization;
using System.Text;

namespace CaseAgents.Eval;

public static class EvalReport
{
    public const string RiskSignalLine = "Accepted unchanged is a risk signal (possible over-trust), not a success metric.";

    public static string Render(IReadOnlyList<EvalRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{"Type",-10} {"Proposals",10} {"Correct",8} {"Precision",10} {"Decided",8} {"Accepted",9} {"Unchanged",10}");
        foreach (var r in rows)
            sb.AppendLine($"{r.QuestionType,-10} {r.Proposals,10} {r.Correct,8} {Percent(r.Precision),10} {r.Decided,8} {r.AcceptedUnchanged,9} {Percent(r.AcceptedUnchangedRate),10}");
        sb.AppendLine();
        sb.AppendLine(RiskSignalLine);
        return sb.ToString();
    }

    public static string Percent(double value) =>
        double.IsNaN(value) ? "n/a" : (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
