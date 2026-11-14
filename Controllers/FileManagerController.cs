using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models.ViewModels;
using SdoApp.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace SdoApp.Controllers;

[Authorize(Roles = "Главный_Админ")]
public class FileManagerController : Controller
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _env;
    private readonly AuditService _auditService;

    public FileManagerController(AppDbContext context, IWebHostEnvironment env, AuditService auditService)
    {
        _context = context;
        _env = env;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string tab = "support", string search = "", int page = 1)
    {
        int pageSize = 50;
        var model = new FileManagerViewModel { ActiveTab = tab, SearchString = search, CurrentPage = page, PageSize = pageSize };
        var allItems = new List<FileItemDto>();

        if (tab == "support")
        {
            var files = await _context.TicketFiles
                .Include(f => f.Message).ThenInclude(m => m!.Ticket).ThenInclude(t => t!.Student)
                .ToListAsync();

            allItems = files.Select(f => new FileItemDto
            {
                Id = f.Id,
                FileName = f.FileName,
                FilePath = f.FilePath,
                UploadedAt = f.Message?.CreatedAt ?? DateTime.UtcNow,
                ContextInfo = $"Тикет №{f.Message?.TicketId} от {f.Message?.Ticket?.Student?.LastName} {f.Message?.Ticket?.Student?.FirstName}",
                SearchText = $"{f.FileName} {f.Message?.TicketId} {f.Message?.Ticket?.Student?.LastName} {f.Message?.Ticket?.Student?.FirstName}".ToLower()
            }).ToList();
        }
        else if (tab == "submissions")
        {
            var files = await _context.SubmissionFiles
                .Include(f => f.Message).ThenInclude(m => m!.StudentSubmission).ThenInclude(s => s!.Student)
                .Include(f => f.Message).ThenInclude(m => m!.StudentSubmission).ThenInclude(s => s!.CourseMaterial)
                .ToListAsync();

            allItems = files.Select(f => new FileItemDto
            {
                Id = f.Id,
                FileName = f.FileName,
                FilePath = f.FilePath,
                UploadedAt = f.Message?.CreatedAt ?? DateTime.UtcNow,
                ContextInfo = $"Задание: {f.Message?.StudentSubmission?.CourseMaterial?.Title} — {f.Message?.StudentSubmission?.Student?.LastName} {f.Message?.StudentSubmission?.Student?.FirstName}",
                SearchText = $"{f.FileName} {f.Message?.StudentSubmission?.CourseMaterial?.Title} {f.Message?.StudentSubmission?.Student?.LastName}".ToLower()
            }).ToList();
        }
        else if (tab == "group_courses")
        {
            var files = await _context.GroupCourseConfigFiles
                .Include(f => f.GroupCourseConfig).ThenInclude(c => c!.Course)
                .Include(f => f.GroupCourseConfig).ThenInclude(c => c!.Group)
                .ToListAsync();

            allItems = files.Select(f => new FileItemDto
            {
                Id = f.Id,
                FileName = f.FileName,
                FilePath = f.FilePath,
                UploadedAt = f.UploadedAt,
                ContextInfo = $"Группа №{f.GroupCourseConfig?.Group?.GroupNumber} — Курс: {f.GroupCourseConfig?.Course?.Title}",
                SearchText = $"{f.FileName} {f.GroupCourseConfig?.Group?.GroupNumber} {f.GroupCourseConfig?.Course?.Title}".ToLower()
            }).ToList();
        }

        // Поиск
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            allItems = allItems.Where(x => x.SearchText.Contains(s)).ToList();
        }

        // Сортировка по дате (сначала новые)
        allItems = allItems.OrderByDescending(x => x.UploadedAt).ToList();

        model.TotalItems = allItems.Count;
        model.TotalPages = (int)Math.Ceiling(model.TotalItems / (double)pageSize);
        if (page < 1) page = 1;
        if (page > model.TotalPages && model.TotalPages > 0) page = model.TotalPages;

        model.CurrentPage = page;
        model.Items = allItems.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> DeleteFile(string tab, int id)
    {
        try
        {
            string deletedFileName = "";
            string contextDesc = "";

            if (tab == "support")
            {
                var file = await _context.TicketFiles.Include(f => f.Message).ThenInclude(m => m!.Ticket).FirstOrDefaultAsync(f => f.Id == id);
                if (file != null)
                {
                    deletedFileName = file.FileName;
                    contextDesc = $"Тикет №{file.Message?.TicketId}";
                    if (file.Message != null) file.Message.Text += "\n\n[📎 Файл удален администратором]";
                    DeletePhysicalFile(file.FilePath);
                    _context.TicketFiles.Remove(file);
                }
            }
            else if (tab == "submissions")
            {
                var file = await _context.SubmissionFiles.Include(f => f.Message).ThenInclude(m => m!.StudentSubmission).FirstOrDefaultAsync(f => f.Id == id);
                if (file != null)
                {
                    deletedFileName = file.FileName;
                    contextDesc = $"Сдача задания (ID: {file.Message?.StudentSubmissionId})";
                    if (file.Message != null) file.Message.TextContent += "\n\n[📎 Файл удален администратором]";
                    DeletePhysicalFile(file.FilePath);
                    _context.SubmissionFiles.Remove(file);
                }
            }
            else if (tab == "group_courses")
            {
                var file = await _context.GroupCourseConfigFiles.FindAsync(id);
                if (file != null)
                {
                    deletedFileName = file.FileName;
                    contextDesc = $"Файл курса (Config ID: {file.GroupCourseConfigId})";
                    DeletePhysicalFile(file.FilePath);
                    _context.GroupCourseConfigFiles.Remove(file);
                }
            }

            await _context.SaveChangesAsync();

            // ДОБАВЛЯЕМ ЛОГИРОВАНИЕ
            if (!string.IsNullOrEmpty(deletedFileName))
            {
                await _auditService.LogAsync("Delete", "File", id, $"Удален файл '{deletedFileName}' из раздела '{tab}' ({contextDesc})");
            }

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    private void DeletePhysicalFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith("/uploads/")) return;
        string abs = Path.Combine(_env.WebRootPath, path.TrimStart('/'));
        if (System.IO.File.Exists(abs)) System.IO.File.Delete(abs);
    }
}