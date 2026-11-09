using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Linq;

namespace SdoApp.Helpers;

public static class FileValidationHelper
{
    // Безопасный список документов для заданий и техподдержки
    public static readonly string[] SafeDocumentExtensions = 
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".txt", ".jpg", ".jpeg", ".png", ".webp", ".zip", ".rar", ".7z" };

    // Строгий список для Базы Знаний
    public static readonly string[] KnowledgeBaseExtensions = 
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".txt", ".zip", ".rar" };

    /// <summary>
    /// Проверяет, безопасно ли расширение файла.
    /// </summary>
    public static bool IsAllowed(IFormFile? file, string[] allowedExtensions)
    {
        if (file == null || file.Length == 0) return false;

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        return allowedExtensions.Contains(ext);
    }
}