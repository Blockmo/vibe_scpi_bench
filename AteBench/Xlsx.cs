using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace AteBench
{
    /// <summary>最精簡的 .xlsx 寫出器 (多工作表、文字與數字儲存格)，不需額外套件。</summary>
    public static class Xlsx
    {
        public sealed class Sheet
        {
            public string Name;
            public IList<string> Header;
            public IEnumerable<object[]> Rows;
        }

        static string Esc(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
                if (ch >= ' ' || ch == '\t') sb.Append(ch);     // XML 不允許其他控制字元
            return SecurityElement.Escape(sb.ToString());
        }

        static void WriteEntry(ZipArchive zip, string name, string content)
        {
            using (var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)))
                w.Write(content);
        }

        static void Cell(StreamWriter w, object v)
        {
            if (v == null) { w.Write("<c/>"); return; }
            if (v is string)
            {
                string s = (string)v;
                if (s.Length > 32000) s = s.Substring(0, 32000);
                w.Write("<c t=\"inlineStr\"><is><t xml:space=\"preserve\">");
                w.Write(Esc(s));
                w.Write("</t></is></c>");
            }
            else if (v is bool) w.Write("<c><v>" + ((bool)v ? 1 : 0) + "</v></c>");
            else w.Write("<c><v>" + Convert.ToString(v, CultureInfo.InvariantCulture) + "</v></c>");
        }

        public static void Write(string path, IList<Sheet> sheets)
        {
            if (File.Exists(path)) File.Delete(path);
            using (var fs = new FileStream(path, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var types = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
                var wb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                    "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
                var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                for (int i = 0; i < sheets.Count; i++)
                {
                    int n = i + 1;
                    types.Append("<Override PartName=\"/xl/worksheets/sheet" + n + ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                    wb.Append("<sheet name=\"" + Esc(sheets[i].Name) + "\" sheetId=\"" + n + "\" r:id=\"rId" + n + "\"/>");
                    rels.Append("<Relationship Id=\"rId" + n + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet" + n + ".xml\"/>");
                }
                WriteEntry(zip, "[Content_Types].xml", types + "</Types>");
                WriteEntry(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                WriteEntry(zip, "xl/workbook.xml", wb + "</sheets></workbook>");
                WriteEntry(zip, "xl/_rels/workbook.xml.rels", rels + "</Relationships>");

                for (int i = 0; i < sheets.Count; i++)
                {
                    using (var w = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet" + (i + 1) + ".xml", CompressionLevel.Optimal).Open(), new UTF8Encoding(false)))
                    {
                        w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row>");
                        foreach (string h in sheets[i].Header) Cell(w, h);
                        w.Write("</row>");
                        foreach (object[] row in sheets[i].Rows)
                        {
                            w.Write("<row>");
                            foreach (object v in row) Cell(w, v);
                            w.Write("</row>");
                        }
                        w.Write("</sheetData></worksheet>");
                    }
                }
            }
        }
    }
}
