using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using SdoApp.Models.ViewModels;
using SdoApp.Services;
using ClosedXML.Excel;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace SdoApp.Controllers;

[Authorize(Roles = "Админ,Главный_Админ")]
public class AuditLogsController : Controller
{
    private readonly AppDbContext _context;
    private readonly AuditService _auditService;

    // Внедряем AuditService
    public AuditLogsController(AppDbContext context, AuditService auditService)
    {
        _context = context;
        _auditService = auditService;
    }

    // Загружаем ВСЕ логи из базы и фильтруем в памяти (обход бага SQLite с кириллицей)
    private async Task<List<AuditLog>> GetFilteredLogsAsync(string? searchString)
    {
        var allLogs = await _context.AuditLogs
            .Include(l => l.User)
            .OrderByDescending(l => l.Timestamp)
            .ToListAsync();

        if (!string.IsNullOrWhiteSpace(searchString))
        {
            var s = searchString.Trim();
            
            // C# идеально ищет русские буквы в любом регистре
            allLogs = allLogs.Where(l => 
                (l.Description != null && l.Description.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                (l.Action != null && l.Action.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                (l.User != null && (
                    (l.User.LastName != null && l.User.LastName.Contains(s, StringComparison.OrdinalIgnoreCase)) || 
                    (l.User.FirstName != null && l.User.FirstName.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                    (l.User.MiddleName != null && l.User.MiddleName.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                    (l.User.Login != null && l.User.Login.Contains(s, StringComparison.OrdinalIgnoreCase))
                ))).ToList();
        }

        return allLogs;
    }

    public async Task<IActionResult> Index(string? searchString, int page = 1, int pageSize = 50)
    {
        if (pageSize != 100 && pageSize != 500 && pageSize != 1000) pageSize = 50;

        var filteredLogs = await GetFilteredLogsAsync(searchString);

        int totalItems = filteredLogs.Count;
        int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
        
        if (page < 1) page = 1;
        if (page > totalPages && totalPages > 0) page = totalPages;

        // Пагинация в памяти (очень быстро для 10-15к записей)
        var logs = filteredLogs
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var model = new AuditLogsViewModel
        {
            Logs = logs,
            SearchString = searchString,
            CurrentPage = page,
            TotalPages = totalPages,
            TotalItems = totalItems,
            PageSize = pageSize
        };

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> ExportToExcel(string? searchString)
    {
        // Логируем факт скачивания журнала
        await _auditService.LogAsync("Export", "AuditLog", null, $"Скачан журнал аудита в Excel (Поиск: '{searchString ?? "все"}')");

        // Берем все отфильтрованные записи
        var logs = await GetFilteredLogsAsync(searchString);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Журнал аудита");

        worksheet.Cell(1, 1).Value = "Дата и время";
        worksheet.Cell(1, 2).Value = "ФИО пользователя";
        worksheet.Cell(1, 3).Value = "Логин";
        worksheet.Cell(1, 4).Value = "Действие";
        worksheet.Cell(1, 5).Value = "Описание";

        var headerRange = worksheet.Range(1, 1, 1, 5);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#F0F4FF");
        headerRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

        int row = 2;
        foreach (var log in logs)
        {
            var fio = log.User != null 
                ? $"{log.User.LastName} {log.User.FirstName} {log.User.MiddleName}".Trim() 
                : "Система";

            worksheet.Cell(row, 1).Value = log.Timestamp.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
            worksheet.Cell(row, 2).Value = fio;
            worksheet.Cell(row, 3).Value = log.User?.Login ?? "—";
            worksheet.Cell(row, 4).Value = log.Action;
            worksheet.Cell(row, 5).Value = log.Description;
            row++;
        }

        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        
        string fileName = $"AuditLog_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }
}