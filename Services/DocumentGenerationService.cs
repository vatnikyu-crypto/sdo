using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SdoApp.Models;
using SdoApp.Models.DTO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace SdoApp.Services;

public class DocumentGenerationService
{
    private readonly string _webRootPath;

    public DocumentGenerationService(string webRootPath)
    {
        _webRootPath = webRootPath;
    }

    public async Task<byte[]> GenerateDocxReportAsync(
        GroupCourseConfig config,
        CourseDocumentTemplate template,
        List<StudentDocumentData> students,
        (string Name, string Position) chairman,
        (string Name, string Position) member1,
        (string Name, string Position) member2,
        string? commissionOrderNumber,
        string? commissionOrderDate,
        string baseUrl)
    {
        string templateFullPath = Path.Combine(_webRootPath, template.FilePath.TrimStart('~', '/'));
        if (!File.Exists(templateFullPath)) throw new FileNotFoundException("Шаблон не найден.");

        byte[] templateBytes = await File.ReadAllBytesAsync(templateFullPath);
        using var resultStream = new MemoryStream();
        await resultStream.WriteAsync(templateBytes, 0, templateBytes.Length);
        resultStream.Position = 0;

        using (var doc = WordprocessingDocument.Open(resultStream, true))
        {
            var body = doc.MainDocumentPart.Document.Body;

            var templateRow = body.Descendants<TableRow>()
                .FirstOrDefault(r => r.InnerText.Contains("{{i}}") || r.InnerText.Contains("{{t.FullName}}"));

            // РЕЖИМ 1: Обычный протокол (табличный)
            if (templateRow != null && !template.IsPerStudent)
            {
                ReplaceGlobalTags(body, config, chairman, member1, member2, commissionOrderNumber, commissionOrderDate, students[0].User.Company?.Name);

                var table = templateRow.Parent;
                var insertAfterRow = templateRow;

                for (int i = 0; i < students.Count; i++)
                {
                    var student = students[i];
                    var newRow = (TableRow)templateRow.CloneNode(true);
                    ReplaceStudentTags(newRow, student, i + 1);

                    string qrText = !string.IsNullOrEmpty(student.Progress?.MintrudOfficialNumber)
                        ? $"{baseUrl}/ohranatruda/{student.Progress.MintrudOfficialNumber}"
                        : baseUrl;
                    var qrGenerator = Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(qrText, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.High);
                    ReplaceTagWithImage(doc, newRow, "{{t.qr_code}}", qrGenerator.ToPngBitmap());

                    table.InsertAfter(newRow, insertAfterRow);
                    insertAfterRow = newRow;
                }
                templateRow.Remove();
            }
            // РЕЖИМ 2: Потоковая печать карточек
            else if (templateRow == null && !template.IsPerStudent)
            {
                var templateElements = body.ChildElements
                    .Where(e => e is Table || (e is Paragraph && !string.IsNullOrWhiteSpace(e.InnerText)))
                    .Select(e => e.CloneNode(true))
                    .ToList();

                body.RemoveAllChildren();

                for (int i = 0; i < students.Count; i++)
                {
                    var student = students[i];

                    foreach (var element in templateElements)
                    {
                        var clonedElement = element.CloneNode(true);

                        ReplaceGlobalTagsInBlock(clonedElement, config, chairman, member1, member2, commissionOrderNumber, commissionOrderDate, student.User.Company?.Name);
                        ReplaceStudentTagsInBlock(clonedElement, student, i + 1);

                        if (clonedElement.InnerText.Contains("{{t.qr_code}}"))
                        {
                            string qrText = !string.IsNullOrEmpty(student.Progress?.MintrudOfficialNumber)
                                ? $"{baseUrl}/ohranatruda/{student.Progress.MintrudOfficialNumber}"
                                : baseUrl;
                            var qrGenerator = Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(qrText, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.High);
                            ReplaceTagWithImage(doc, clonedElement, "{{t.qr_code}}", qrGenerator.ToPngBitmap());
                        }

                        body.AppendChild(clonedElement);
                    }

                    if ((i + 1) % 3 == 0 && i < students.Count - 1)
                    {
                        body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                    }
                }
            }

            doc.MainDocumentPart.Document.Save();
        }
        return resultStream.ToArray();
    }

    private void ReplaceGlobalTagsInBlock(OpenXmlElement element, GroupCourseConfig config,
        (string Name, string Position) chairman, (string Name, string Position) member1, (string Name, string Position) member2,
        string? orderNum, string? orderDate, string? companyName)
    {
        var values = new Dictionary<string, string>
        {
            { "{{CourseTitle}}", config.Course?.Title ?? "—" },
            { "{{ProtocolNumber}}", config.ProtocolNumber ?? "—" },
            { "{{CommissionOrderNumber}}", orderNum ?? "—" },
            { "{{CommissionOrderDate}}", !string.IsNullOrEmpty(orderDate) ? DateTime.Parse(orderDate).ToString("dd.MM.yyyy") : "—" },
            { "{{EndDate}}", (config.CourseEndDate ?? config.Group?.EndDate)?.ToString("dd.MM.yyyy") ?? "—" },
            { "{{ChairmanName}}", chairman.Name },
            { "{{ChairmanPosition}}", chairman.Position },
            { "{{userCompany}}", companyName ?? "Частное лицо" },
            { "{{DurationHours}}", config.Course?.DurationHours?.ToString() ?? "—" }
        };
        ReplaceTextInElement(element, values);
    }

    private void ReplaceStudentTagsInBlock(OpenXmlElement element, StudentDocumentData data, int index)
    {
        var s = data.User;
        var p = data.Progress;
        string fullName = $"{s.LastName} {s.FirstName} {s.MiddleName}".Trim();

        var values = new Dictionary<string, string>
        {
            { "{{i}}", index.ToString() },
            { "{{t.FullName}}", fullName }, { "{{FullName}}", fullName },
            { "{{t.Position}}", s.Position ?? "—" }, { "{{Position}}", s.Position ?? "—" },
            { "{{t.Company}}", s.Company?.Name ?? "Частное лицо" }, { "{{Company}}", s.Company?.Name ?? "Частное лицо" },
            { "{{t.userIdNum}}", p?.CertificateNumber ?? "—" }, { "{{userIdNum}}", p?.CertificateNumber ?? "—" },
            { "{{t.userDepartment}}", s.Department ?? "—" }, { "{{userDepartment}}", s.Department ?? "—" },
            { "{{t.eisot}}", p?.MintrudOfficialNumber ?? p?.MintrudNumber ?? "—" }, { "{{eisot}}", p?.MintrudOfficialNumber ?? p?.MintrudNumber ?? "—" }
        };
        ReplaceTextInElement(element, values);
    }

    private void ReplaceGlobalTags(Body body, GroupCourseConfig config,
        (string Name, string Position) chairman, (string Name, string Position) member1, (string Name, string Position) member2,
        string? orderNum, string? orderDate, string? companyName)
    {
        var values = new Dictionary<string, string>
        {
            { "{{CourseTitle}}", config.Course?.Title ?? "—" },
            { "{{ProtocolNumber}}", config.ProtocolNumber ?? "—" },
            { "{{CommissionOrderNumber}}", orderNum ?? "—" },
            { "{{CommissionOrderDate}}", !string.IsNullOrEmpty(orderDate) ? DateTime.Parse(orderDate).ToString("dd.MM.yyyy") : "—" },
            { "{{EndDate}}", (config.CourseEndDate ?? config.Group?.EndDate)?.ToString("dd.MM.yyyy") ?? "—" },
            { "{{ChairmanName}}", chairman.Name },
            { "{{ChairmanPosition}}", chairman.Position },
            { "{{Member1Name}}", member1.Name },
            { "{{Member1Position}}", member1.Position },
            { "{{Member2Name}}", member2.Name },
            { "{{Member2Position}}", member2.Position },
            { "{{userCompany}}", companyName ?? "Частное лицо" }
        };
        ReplaceTextInElement(body, values);
    }

    private void ReplaceStudentTags(OpenXmlElement row, StudentDocumentData data, int index)
    {
        var s = data.User;
        var p = data.Progress;
        string fullName = $"{s.LastName} {s.FirstName} {s.MiddleName}".Trim();

        var values = new Dictionary<string, string>
        {
            { "{{i}}", index.ToString() },
            { "{{t.FullName}}", fullName }, { "{{FullName}}", fullName },
            { "{{t.Position}}", s.Position ?? "—" }, { "{{Position}}", s.Position ?? "—" },
            { "{{t.Company}}", s.Company?.Name ?? "Частное лицо" }, { "{{Company}}", s.Company?.Name ?? "Частное лицо" },
            { "{{t.userIdNum}}", p?.CertificateNumber ?? "—" }, { "{{userIdNum}}", p?.CertificateNumber ?? "—" },
            { "{{t.userDepartment}}", s.Department ?? "—" }, { "{{userDepartment}}", s.Department ?? "—" },
            { "{{t.eisot}}", p?.MintrudOfficialNumber ?? p?.MintrudNumber ?? "—" }, { "{{eisot}}", p?.MintrudOfficialNumber ?? p?.MintrudNumber ?? "—" }
        };
        ReplaceTextInElement(row, values);
    }

    private void ReplaceTextInElement(OpenXmlElement element, Dictionary<string, string> values)
    {
        foreach (var p in element.Descendants<Paragraph>().ToList())
        {
            var textNodes = p.Descendants<Text>().ToList();
            if (!textNodes.Any()) continue;

            string fullText = string.Concat(textNodes.Select(t => t.Text));
            string newText = fullText;
            bool changed = false;

            foreach (var kvp in values)
            {
                if (newText.Contains(kvp.Key))
                {
                    newText = newText.Replace(kvp.Key, kvp.Value);
                    changed = true;
                }
            }

            if (changed)
            {
                textNodes[0].Text = newText;
                textNodes[0].Space = SpaceProcessingModeValues.Preserve;
                for (int i = 1; i < textNodes.Count; i++) textNodes[i].Text = "";
            }
        }
    }

    private void ReplaceTagWithImage(WordprocessingDocument doc, OpenXmlElement element, string tag, byte[] imageBytes)
    {
        foreach (var p in element.Descendants<Paragraph>().ToList())
        {
            var textNodes = p.Descendants<Text>().ToList();
            if (!textNodes.Any()) continue;

            string fullText = string.Concat(textNodes.Select(t => t.Text));
            if (!fullText.Contains(tag)) continue;

            string newText = fullText.Replace(tag, "");
            textNodes[0].Text = newText;
            textNodes[0].Space = SpaceProcessingModeValues.Preserve;
            for (int i = 1; i < textNodes.Count; i++) textNodes[i].Text = "";

            var run = new Run();
            run.AppendChild(CreateImageElement(doc, imageBytes));
            p.AppendChild(run);
        }
    }

    private Drawing CreateImageElement(WordprocessingDocument doc, byte[] imageBytes)
    {
        var imagePart = doc.MainDocumentPart.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(imageBytes)) imagePart.FeedData(stream);
        string relationshipId = doc.MainDocumentPart.GetIdOfPart(imagePart);

        long widthEmu = 540000;
        long heightEmu = 540000;

        return new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = widthEmu, Cy = heightEmu },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = (UInt32Value)1U, Name = "QR Code" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = (UInt32Value)0U, Name = "qr.png" },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = widthEmu, Cy = heightEmu }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })
                        )
                    )
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })
            )
            { DistanceFromTop = (UInt32Value)0U, DistanceFromBottom = (UInt32Value)0U, DistanceFromLeft = (UInt32Value)0U, DistanceFromRight = (UInt32Value)0U }
        );
    }
}