using System.Text;
using ClosedXML.Excel;

namespace ElectionRides.Api.VoterBase;

/// <summary>Reads an Excel (.xlsx) or CSV file into a header row and data rows of text. Nothing is stored.</summary>
public static class TableFile
{
    public record Table(List<string> Headers, List<string[]> Rows);

    static TableFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Table Read(Stream stream, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".xlsx") return ReadExcel(stream);
        if (ext == ".csv") return ReadCsv(stream);
        throw new InvalidDataException("אפשר לטעון קובץ Excel ‏(xlsx) או CSV");
    }

    static Table ReadExcel(Stream stream)
    {
        using var book = new XLWorkbook(stream);
        var sheet = book.Worksheets.First();
        var used = sheet.RangeUsed() ?? throw new InvalidDataException("הקובץ ריק");
        var rows = used.RowsUsed().Select(r => r.Cells(1, used.ColumnCount()).Select(c => c.GetFormattedString().Trim()).ToArray()).ToList();
        if (rows.Count == 0) throw new InvalidDataException("הקובץ ריק");
        return new Table(rows[0].ToList(), rows.Skip(1).ToList());
    }

    /// <summary>CSV in UTF-8, or in the older Hebrew Windows encoding (1255) that Excel often saves.</summary>
    static Table ReadCsv(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.GetEncoding(1255).GetString(bytes);
        }
        text = text.TrimStart('﻿');
        var lines = ParseCsv(text);
        if (lines.Count == 0) throw new InvalidDataException("הקובץ ריק");
        return new Table(lines[0].Select(h => h.Trim()).ToList(), lines.Skip(1).Where(l => l.Any(c => c.Length > 0)).ToList());
    }

    /// <summary>Comma or semicolon separated, with quoted fields.</summary>
    static List<string[]> ParseCsv(string text)
    {
        var first = text.Split('\n')[0];
        var sep = first.Count(c => c == ';') > first.Count(c => c == ',') ? ';' : ',';
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == sep) { row.Add(cell.ToString().Trim()); cell.Clear(); }
            else if (ch == '\n')
            {
                row.Add(cell.ToString().Trim().TrimEnd('\r'));
                cell.Clear();
                rows.Add(row.ToArray());
                row = [];
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString().Trim().TrimEnd('\r'));
            rows.Add(row.ToArray());
        }
        return rows;
    }
}
