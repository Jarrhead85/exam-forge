using System.Text;
using UglyToad.PdfPig;
using Xunit;

namespace ExamForge.UnitTests.Infrastructure;

/// <summary>Проверяет, что минимальный рукописный PDF теста читается PdfPig'ом.</summary>
public class MinimalPdfTests
{
    [Fact]
    public void PdfPig_ReadsHandCraftedPdf_Text()
    {
        var line = "oculus, i m - glaz";
        var content = $"BT /F1 12 Tf 72 720 Td ({line}) Tj ET";
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var o in objects)
        {
            offsets.Add(Encoding.ASCII.GetBytes(pdf.ToString()).Length);
            pdf.Append(o);
        }
        var xrefStart = Encoding.ASCII.GetBytes(pdf.ToString()).Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF");
        var bytes = Encoding.ASCII.GetBytes(pdf.ToString());

        using var document = PdfDocument.Open(bytes);
        var page = Assert.Single(document.GetPages());
        var text = page.Text;
        Assert.Contains(line, text);
    }
}
