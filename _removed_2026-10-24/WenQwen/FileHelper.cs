using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ExcelDataReader;
using System.Data;
using iTextSharp.text.pdf;
using iTextSharp.text.pdf.parser;
using DocumentFormat.OpenXml.Packaging;
using Path = System.IO.Path;

public static class FileHelper
{
    /// <summary>
    /// 读取【资料】文件夹下所有支持的文档
    /// 支持：txt / pdf / docx / xlsx / xls
    /// </summary>
    public static Dictionary<string, string> LoadAllKnowledgeFiles()
    {
        var knowledge = new Dictionary<string, string>();
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string learnDir = Path.Combine(baseDir, "资料");

        if (!Directory.Exists(learnDir))
        {
            Directory.CreateDirectory(learnDir);
            return knowledge;
        }

        string[] exts = { ".txt", ".pdf", ".docx", ".xlsx", ".xls" };
        var files = Directory.GetFiles(learnDir, "*.*", SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            string ext = Path.GetExtension(file).ToLower();
            if (!Array.Exists(exts, e => e == ext)) continue;

            try
            {
                string content = ext switch
                {
                    ".txt" => ReadText(file),
                    ".pdf" => ReadPdf(file),
                    ".docx" => ReadDocx(file),
                    ".xlsx" or ".xls" => ReadExcel(file),
                    _ => ""
                };

                if (!string.IsNullOrWhiteSpace(content))
                    knowledge[Path.GetFileName(file)] = content;
            }
            catch { }
        }

        return knowledge;
    }

    public static string ReadText(string path) => File.ReadAllText(path, Encoding.UTF8);

    public static string ReadPdf(string path)
    {
        try
        {
            var sb = new StringBuilder();
            using var reader = new PdfReader(path);
            for (int i = 1; i <= reader.NumberOfPages; i++)
                sb.AppendLine(PdfTextExtractor.GetTextFromPage(reader, i));
            return sb.ToString();
        }
        catch { return ""; }
    }

    public static string ReadDocx(string path)
    {
        try
        {
            var sb = new StringBuilder();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = WordprocessingDocument.Open(fs, false);
            if (doc.MainDocumentPart?.Document?.Body == null) return "";

            foreach (var p in doc.MainDocumentPart.Document.Body.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
            {
                string text = p.InnerText?.Trim();
                if (!string.IsNullOrEmpty(text)) sb.AppendLine(text);
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    public static string ReadExcel(string path)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var stream = File.OpenRead(path);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var ds = reader.AsDataSet();
            var sb = new StringBuilder();

            foreach (DataTable table in ds.Tables)
            {
                sb.AppendLine("=== " + table.TableName + " ===");
                foreach (DataRow row in table.Rows)
                {
                    foreach (DataColumn col in table.Columns)
                        sb.Append(row[col] + "\t");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }
        catch { return ""; }
    }
}