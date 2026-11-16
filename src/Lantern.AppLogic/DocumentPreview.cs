using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Lantern.AppLogic;

internal record PreviewSection(string? Title, string Text, IReadOnlyList<string[]>? Rows = null);
internal record DocumentContents(string Format, IReadOnlyList<PreviewSection> Sections, bool Limited);

internal static class DocumentPreview
{
    private const int MaxText = 24000;
    private const int MaxPackageBuffer = 24 * 1024 * 1024;
    private const int MaxPartSize = 2 * 1024 * 1024;
    private const int MaxSlides = 12;
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Presentation = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Sheets = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Parts = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace References = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly Regex SlidePart = new(@"\A(?:/ppt/)?slides/slide[0-9]+\.xml\z", RegexOptions.CultureInvariant);
    private static readonly Regex SheetPart = new(@"\A(?:/xl/)?worksheets/sheet[0-9]+\.xml\z", RegexOptions.CultureInvariant);

    internal static string? Format(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".docx" => "Word",
        ".pptx" => "PowerPoint",
        ".xlsx" => "Excel",
        ".csv" or ".tsv" => "表格文本",
        ".txt" or ".md" or ".log" or ".json" or ".xml" or ".ini" => "纯文本",
        _ => null
    };

    internal static FileStream OpenLocal(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);

    internal static async Task<DocumentContents> ReadAsync(Stream stream, string format, CancellationToken token)
    {
        if (format is not ("Word" or "PowerPoint" or "Excel")) return await ReadTextAsync(stream, format, token);

        using var buffered = stream.CanSeek ? null : new MemoryStream();
        if (buffered is not null)
        {
            byte[] block = new byte[64 * 1024];
            int count;
            while ((count = await stream.ReadAsync(block, token)) != 0)
            {
                if (buffered.Length + count > MaxPackageBuffer) throw new IOException("文档较大，请使用系统应用打开");
                await buffered.WriteAsync(block.AsMemory(0, count), token);
            }
        }

        Stream package = buffered ?? stream;
        CheckCentralDirectory(package);
        package.Position = 0;
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        return format switch
        {
            "Word" => ReadWord(archive, token),
            "PowerPoint" => ReadSlides(archive, token),
            _ => ReadSheets(archive, token)
        };
    }

    private static async Task<DocumentContents> ReadTextAsync(Stream stream, string format, CancellationToken token)
    {
        byte[] sample = new byte[96 * 1024];
        int count = await stream.ReadAtLeastAsync(sample, sample.Length, false, token);
        bool limited = count == sample.Length;
        string text;
        if (sample.AsSpan(0, count).StartsWith(new byte[] { 0xff, 0xfe })) text = Encoding.Unicode.GetString(sample, 2, (count - 2) & ~1);
        else if (sample.AsSpan(0, count).StartsWith(new byte[] { 0xfe, 0xff })) text = Encoding.BigEndianUnicode.GetString(sample, 2, (count - 2) & ~1);
        else
        {
            int start = sample.AsSpan(0, count).StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            try { text = Decode(new UTF8Encoding(false, true), sample, start, count, limited); }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Decode(Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), sample, start, count, limited);
            }
        }
        if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t' or '\f')))
            throw new IOException("此文件无法作为文本预览");
        return new(format, [new(null, Clip(text, ref limited))], limited);
    }

    private static string Decode(Encoding encoding, byte[] bytes, int start, int count, bool limited)
    {
        Decoder decoder = encoding.GetDecoder();
        char[] text = new char[encoding.GetMaxCharCount(count - start)];
        int length = decoder.GetChars(bytes, start, count - start, text, 0, flush: !limited);
        return new string(text, 0, length);
    }

    private static void CheckCentralDirectory(Stream package)
    {
        // Documents are read in place. Bound directory metadata before ZipArchive indexes it.
        int length = (int)Math.Min(package.Length, 65557);
        byte[] tail = new byte[length];
        package.Seek(-length, SeekOrigin.End);
        package.ReadExactly(tail);
        for (int i = length - 22; i >= 0; i--)
        {
            var end = tail.AsSpan(i);
            if (BinaryPrimitives.ReadUInt32LittleEndian(end) != 0x06054b50 ||
                BinaryPrimitives.ReadUInt16LittleEndian(end[20..]) != length - i - 22) continue;
            int entries = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(end[4..]) == 0 &&
                BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) == entries && entries is > 0 and <= 4096 &&
                size <= MaxPartSize && offset + size == package.Length - length + i) return;
            break;
        }
        throw new InvalidDataException("文档结构超出预览支持范围");
    }

    private static XDocument ReadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidDataException("缺少文档内容");
        if (entry.Length > MaxPartSize) throw new IOException("此文档内容过长，请使用系统应用打开");
        using var contents = entry.Open();
        using var reader = XmlReader.Create(contents, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxPartSize,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static DocumentContents ReadWord(ZipArchive archive, CancellationToken token)
    {
        var xml = ReadXml(archive, "word/document.xml");
        var body = xml.Root?.Element(Word + "body") ?? throw new InvalidDataException("无法识别 Word 正文");
        var text = new StringBuilder();
        bool limited = false;
        foreach (var paragraph in body.Descendants(Word + "p"))
        {
            token.ThrowIfCancellationRequested();
            if (text.Length >= MaxText) { limited = true; break; }
            text.AppendLine(string.Concat(paragraph.Descendants().Select(node =>
                node.Name == Word + "t" ? node.Value : node.Name == Word + "tab" ? "\t" : node.Name == Word + "br" ? "\n" : "")));
        }
        string excerpt = Clip(text.ToString().TrimEnd(), ref limited);
        return new("Word", [new(null, excerpt)], limited);
    }

    private static DocumentContents ReadSlides(ZipArchive archive, CancellationToken token)
    {
        var presentation = ReadXml(archive, "ppt/presentation.xml");
        var parts = ReadParts(archive, "ppt/_rels/presentation.xml.rels", "ppt", SlidePart);
        var ids = presentation.Root?.Element(Presentation + "sldIdLst")?.Elements(Presentation + "sldId").ToArray() ?? [];
        if (ids.Length == 0) throw new InvalidDataException("演示文稿没有幻灯片");
        bool limited = ids.Length > MaxSlides;
        var sections = new List<PreviewSection>();
        for (int i = 0; i < Math.Min(ids.Length, MaxSlides); i++)
        {
            token.ThrowIfCancellationRequested();
            var id = (string?)ids[i].Attribute(References + "id");
            if (id is null || !parts.TryGetValue(id, out string? part)) throw new InvalidDataException("无法读取幻灯片");
            var slide = ReadXml(archive, part);
            string text = string.Join('\n', slide.Descendants(Drawing + "p").Select(p => string.Concat(p.Descendants(Drawing + "t").Select(t => t.Value))));
            sections.Add(new("第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 张幻灯片", Limit(text.Trim(), 1600, ref limited)));
        }
        return new("PowerPoint", sections, limited);
    }

    private static Dictionary<string, string> ReadParts(ZipArchive archive, string path, string root, Regex allowed)
    {
        var relationships = ReadXml(archive, path);
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in relationships.Descendants(Parts + "Relationship"))
        {
            string? id = (string?)item.Attribute("Id"), target = (string?)item.Attribute("Target");
            if (id is not null && target is not null && (string?)item.Attribute("TargetMode") != "External" && allowed.IsMatch(target))
                parts.TryAdd(id, target.StartsWith('/') ? target[1..] : root + "/" + target);
        }
        return parts;
    }

    private static DocumentContents ReadSheets(ZipArchive archive, CancellationToken token)
    {
        const int maxSheets = 3, maxRows = 16, maxColumns = 8;
        var workbook = ReadXml(archive, "xl/workbook.xml");
        var parts = ReadParts(archive, "xl/_rels/workbook.xml.rels", "xl", SheetPart);
        var shared = archive.GetEntry("xl/sharedStrings.xml") is null ? [] : ReadXml(archive, "xl/sharedStrings.xml")
            .Descendants(Sheets + "si").Select(cell => string.Concat(cell.Descendants(Sheets + "t").Select(t => t.Value))).ToArray();
        var sheets = workbook.Root?.Element(Sheets + "sheets")?.Elements(Sheets + "sheet").ToArray() ?? [];
        if (sheets.Length == 0) throw new InvalidDataException("工作簿没有工作表");
        bool limited = sheets.Length > maxSheets;
        var sections = new List<PreviewSection>();
        foreach (var sheet in sheets.Take(maxSheets))
        {
            token.ThrowIfCancellationRequested();
            var id = (string?)sheet.Attribute(References + "id");
            if (id is null || !parts.TryGetValue(id, out var path)) throw new InvalidDataException("无法读取工作表");
            var data = ReadXml(archive, path).Root?.Element(Sheets + "sheetData");
            var rows = new List<string[]>();
            int widest = 0;
            if (data is not null)
                foreach (var row in data.Elements(Sheets + "row"))
                {
                    if (rows.Count == maxRows) { limited = true; break; }
                    string[] cells = Enumerable.Repeat("", maxColumns).ToArray();
                    int column = -1;
                    foreach (var cell in row.Elements(Sheets + "c"))
                    {
                        string? reference = (string?)cell.Attribute("r");
                        column = reference is null ? column + 1 : reference.Length > 1 && char.IsAsciiDigit(reference[1]) ? reference[0] - 'A' : maxColumns;
                        if (column < 0 || column >= maxColumns) { limited = true; continue; }
                        string value = cell.Element(Sheets + "v")?.Value ?? "";
                        var kind = (string?)cell.Attribute("t");
                        if (kind == "inlineStr") value = string.Concat(cell.Descendants(Sheets + "t").Select(t => t.Value));
                        if (kind == "s") value = int.TryParse(value, out int index) && index >= 0 && index < shared.Length ? shared[index] : "";
                        cells[column] = Limit(value, 160, ref limited);
                        widest = Math.Max(widest, column + 1);
                    }
                    rows.Add(cells);
                }
            string name = Limit((string?)sheet.Attribute("name") ?? "工作表", 80, ref limited);
            sections.Add(new(name, "", widest > 0 ? rows.Select(row => row[..widest]).ToArray() : []));
        }
        return new("Excel", sections, limited);
    }

    private static string Clip(string text, ref bool limited) => Limit(text, MaxText, ref limited);

    private static string Limit(string text, int size, ref bool limited)
    {
        if (text.Length <= size) return text;
        limited = true;
        return text[..(char.IsHighSurrogate(text[size - 1]) ? size - 1 : size)];
    }
}
