using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;

namespace KNE;

/// <summary>Stable-ID dialogue interchange for translation tools and plugin-defined formats.</summary>
public static class PluginDialogues
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const int Limit = 16 * 1024 * 1024;
    public static IReadOnlyList<PluginDialogue> Read(VisualScript graph) => graph.Nodes.Where(n => n.Kind is ScriptNodeKind.Dialogue or ScriptNodeKind.AutoDialogue or ScriptNodeKind.EffectDialogue)
        .Select(n => new PluginDialogue(n.Id, n.Speaker, n.Text)).ToArray();
    public static VisualScript Apply(VisualScript graph, IReadOnlyList<PluginDialogue> translation)
    {
        Validate(translation); var copy = VisualScript.Parse(graph.Serialize()); var allowed = Read(copy).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in translation)
        {
            if (!allowed.Contains(row.Id)) throw new InvalidDataException("Не найдена реплика: " + row.Id);
            var node = copy.Nodes.Single(n => n.Id == row.Id); node.Speaker = row.Speaker; node.Text = row.Text;
        }
        _ = copy.Serialize(); return copy;
    }
    public static byte[] Export(IReadOnlyList<PluginDialogue> dialogue, string extension)
    {
        Validate(dialogue);
        return extension.ToLowerInvariant() switch
        {
            ".json" => JsonSerializer.SerializeToUtf8Bytes(dialogue, new JsonSerializerOptions { WriteIndented = true }),
            ".docx" => ExportDocx(dialogue), ".pdf" => ExportPdf(dialogue),
            _ => throw new ArgumentException("Встроенный экспорт поддерживает JSON, DOCX, PDF.")
        };
    }
    public static IReadOnlyList<PluginDialogue> Import(byte[] data, string extension)
    {
        if (data.Length > Limit) throw new InvalidDataException("Перевод больше 16 МБ.");
        var rows = extension.ToLowerInvariant() switch
        {
            ".json" => JsonSerializer.Deserialize<PluginDialogue[]>(data) ?? throw new InvalidDataException("Пустой перевод."),
            ".docx" => ImportDocx(data), _ => throw new ArgumentException("Обратный импорт поддерживает JSON и DOCX; PDF предназначен для чтения.")
        };
        Validate(rows); return rows;
    }
    private static void Validate(IReadOnlyList<PluginDialogue> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > 10000 || rows.Any(r => r == null || string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 200 || r.Text == null || r.Speaker == null || r.Text.Length > 16000 || r.Speaker.Length > 16000) ||
            rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count || rows.Sum(r => (long)r.Text.Length + r.Speaker.Length + r.Id.Length) > 2_000_000)
            throw new InvalidDataException("Перевод: уникальные ID, текст до 16000 символов, не более 10000 реплик и 2 млн символов.");
    }
    private static byte[] ExportDocx(IReadOnlyList<PluginDialogue> rows)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            void Write(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
            Write("[Content_Types].xml", """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""");
            Write("_rels/.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");
            XElement Cell(string value) => new(W + "tc", value.Split('\n').Select(line => new XElement(W + "p", new XElement(W + "r", new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), line)))));
            XElement Row(params string[] values) => new(W + "tr", values.Select(Cell));
            var table = new XElement(W + "tbl", new XElement(W + "tblPr", new XElement(W + "tblW", new XAttribute(W + "w", "0"), new XAttribute(W + "type", "auto"))), Row("ID", "Speaker", "Text"));
            foreach (var row in rows) table.Add(Row(row.Id, row.Speaker, row.Text));
            Write("word/document.xml", new XDocument(new XElement(W + "document", new XAttribute(XNamespace.Xmlns + "w", W), new XElement(W + "body", table, new XElement(W + "sectPr")))).ToString());
        }
        return memory.ToArray();
    }
    private static PluginDialogue[] ImportDocx(byte[] data)
    {
        using var memory = new MemoryStream(data); using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("В DOCX отсутствует word/document.xml.");
        if (entry.Length > Limit) throw new InvalidDataException("XML перевода больше 16 МБ.");
        using var stream = entry.Open(); using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = Limit });
        var xml = XDocument.Load(reader); var rows = new List<PluginDialogue>();
        foreach (var tr in xml.Descendants(W + "tr").Skip(1))
        {
            var cells = tr.Elements(W + "tc").Select(c => string.Join("\n", c.Elements(W + "p").Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value))))).ToArray();
            if (cells.Length != 3) throw new InvalidDataException("Таблица перевода должна содержать ID, Speaker, Text.");
            rows.Add(new(cells[0], cells[1], cells[2]));
        }
        return rows.ToArray();
    }
    private static byte[] ExportPdf(IReadOnlyList<PluginDialogue> rows)
    {
        var document = new Document(); document.Info.Title = "KNE · Диалоги для перевода"; document.Styles[StyleNames.Normal]!.Font.Name = "Segoe UI";
        var section = document.AddSection(); section.AddParagraph("KNE · Диалоги для перевода", StyleNames.Heading1);
        foreach (var row in rows)
        {
            var label = section.AddParagraph("[" + row.Id + "] " + row.Speaker); label.Format.Font.Bold = true; label.Format.KeepWithNext = true;
            var text = section.AddParagraph(row.Text); text.Format.SpaceAfter = Unit.FromPoint(10);
        }
        var renderer = new PdfDocumentRenderer { Document = document }; renderer.RenderDocument();
        using var memory = new MemoryStream(); using (renderer.PdfDocument) renderer.PdfDocument.Save(memory, false); return memory.ToArray();
    }
}
