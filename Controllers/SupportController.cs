using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace SdoApp.Controllers;

[Authorize]
public class SupportController : Controller
{
    private readonly AppDbContext _context;
    private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _env;

    public SupportController(AppDbContext context, Microsoft.AspNetCore.Hosting.IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
    }

    // GET: /Support/StudentIndex (ОБНОВЛЕННЫЙ)
    [HttpGet]
    [Authorize]
    public async Task<IActionResult> StudentIndex()
    {
        var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? User.FindFirst("Id")?.Value
                     ?? User.FindFirst("UserId")?.Value;

        if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out int studentId))
        {
            return RedirectToAction("Logout", "Auth");
        }

        // Вытаскиваем тикеты сразу с их сообщениями, чтобы узнать автора последнего
        var myTickets = await _context.SupportTickets
            .Include(t => t.Messages)
            .Where(t => t.StudentId == studentId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();

        return View(myTickets);
    }

    // GET: /Support/AdminIndex (ОБНОВЛЕННЫЙ)
    [HttpGet]
    [Authorize(Roles = "Админ,Главный_Админ")]
    public async Task<IActionResult> AdminIndex()
    {
        var allTickets = await _context.SupportTickets
            .Include(t => t.Student)
            .Include(t => t.Messages) // Обязательно подключаем сообщения
            .OrderByDescending(t => t.Status == TicketStatus.Открыто)
            .ThenByDescending(t => t.UpdatedAt)
            .ToListAsync();

        return View(allTickets);
    }

    // POST: /Support/CreateTicket
    [HttpPost]
    [Authorize(Roles = "Обучающийся")]
    public async Task<IActionResult> CreateTicket(string phone, string text, List<IFormFile>? files)
    {
        // БЕЗОПАСНЫЙ СБОР ID ПО ВСЕМ ВОЗМОЖНЫМ КЛЕЙМАМ
        var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? User.FindFirst("Id")?.Value
                     ?? User.FindFirst("UserId")?.Value
                     ?? User.Identity?.Name;

        if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out int studentId))
        {
            return Json(new { success = false, message = "Ошибка: Авторизация пользователя истекла!" });
        }

        if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(text))
        {
            return Json(new { success = false, message = "Ошибка: Номер телефона и текст обращения обязательны!" });
        }

        // ВАЛИДАЦИЯ ФАЙЛОВ: Максимум 3 файла, общий вес до 15 МБ
        if (files != null && files.Count > 0)
        {
            if (files.Count > 3)
            {
                return Json(new { success = false, message = "Ошибка: Можно прикрепить не более 3 файлов!" });
            }

            long totalSizeBytes = files.Sum(f => f.Length);
            double totalSizeMb = (double)totalSizeBytes / (1024 * 1024);

            if (totalSizeMb > 15.0)
            {
                return Json(new { success = false, message = $"Ошибка: Общий размер файлов ({Math.Round(totalSizeMb, 2)} МБ) превышает лимит в 15 МБ!" });
            }
        }

        var ticket = new SupportTicket
        {
            Phone = phone.Trim(),
            StudentId = studentId,
            Status = TicketStatus.Открыто,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.SupportTickets.Add(ticket);
        await _context.SaveChangesAsync();

        var message = new SupportMessage
        {
            TicketId = ticket.Id,
            SenderId = studentId,
            Text = text.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        _context.SupportMessages.Add(message);
        await _context.SaveChangesAsync();

        if (files != null && files.Count > 0)
        {
            string uploadFolder = Path.Combine(_env.WebRootPath, "uploads", "support");
            if (!Directory.Exists(uploadFolder)) Directory.CreateDirectory(uploadFolder);

            foreach (var file in files)
            {
                string extension = Path.GetExtension(file.FileName).ToLower();
                string uniqueName = Guid.NewGuid().ToString() + extension;
                string savePath = Path.Combine(uploadFolder, uniqueName);

                using (var stream = new FileStream(savePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                _context.TicketFiles.Add(new TicketFile
                {
                    MessageId = message.Id,
                    FilePath = $"/uploads/support/{uniqueName}",
                    FileName = file.FileName
                });
            }
            await _context.SaveChangesAsync();
        }

        return Json(new { success = true });
    }

    // =================================================================
    // ТОЧКА ПОДКЛЮЧЕНИЯ ЧАСТИ 2 (ВСТАВЛЯТЬ СЕКЦИЮ АДМИНА СЮДА)
    // =================================================================


    // GET: /Support/GetDetails/5
    [HttpGet]
    public async Task<IActionResult> GetDetails(int id)
    {
        var ticket = await _context.SupportTickets
            .Include(t => t.Student)
            .Include(t => t.Messages).ThenInclude(m => m.Sender)
            .Include(t => t.Messages).ThenInclude(m => m.Files)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (ticket == null) return NotFound();

        var currentUserIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                            ?? User.FindFirst("Id")?.Value
                            ?? User.FindFirst("UserId")?.Value;

        if (string.IsNullOrEmpty(currentUserIdStr) || !int.TryParse(currentUserIdStr, out int currentUserId))
        {
            return Json(new { success = false, message = "Сессия пользователя истекла." });
        }

        if (User.IsInRole("Обучающийся") && ticket.StudentId != currentUserId)
        {
            return Forbid();
        }

        var result = new
        {
            ticketId = ticket.Id,
            status = (int)ticket.Status, // 0 - Открыто, 1 - Закрыто
            phone = ticket.Phone,
            studentFio = $"{ticket.Student?.LastName} {ticket.Student?.FirstName}".Trim(),
            createdAt = ticket.CreatedAt.ToString("dd.MM.yyyy HH:mm"),
            messages = ticket.Messages.OrderBy(m => m.CreatedAt).Select(m => new
            {
                id = m.Id,
                text = m.Text,
                // ЖЕСТКАЯ ПРОВЕРКА: Если SenderId равен StudentId тикета — это Студент. Иначе — это Техподдержка!
                senderFio = m.SenderId == ticket.StudentId
                    ? $"{ticket.Student?.LastName} {ticket.Student?.FirstName}".Trim()
                    : "🛠️ Служба техподдержки (Администратор)",
                isMe = m.SenderId == currentUserId,
                isAdminMessage = m.SenderId != ticket.StudentId, // Флаг, что сообщение от админа
                createdAt = m.CreatedAt.ToString("dd.MM.yyyy HH:mm"),
                files = m.Files.Select(f => new { name = f.FileName, url = f.FilePath }).ToList()
            }).ToList()
        };

        return Json(result);
    }


    // POST: /Support/SendAdminReply
    // УНИВЕРСАЛЬНЫЙ МЕТОД: Обрабатывает ответы и от Администратора, и от Студента
    [HttpPost]
    public async Task<IActionResult> SendAdminReply(int ticketId, string text, IFormFile? replyFile)
    {
        // Система сама железно вытащит ID того, кто кликнул кнопку отправить — админа или студента
        var senderIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("Id")?.Value
                       ?? User.FindFirst("UserId")?.Value;

        if (string.IsNullOrEmpty(senderIdStr) || !int.TryParse(senderIdStr, out int senderId))
        {
            return Json(new { success = false, message = "Ошибка: Авторизация пользователя истекла!" });
        }

        var ticket = await _context.SupportTickets.FindAsync(ticketId);
        if (ticket == null)
        {
            return Json(new { success = false, message = "Обращение не найдено в системе!" });
        }

        // ЖЕСТКАЯ БЛОКИРОВКА ПРИ ЗАКРЫТОМ ТИКЕТЕ
        if (ticket.Status == TicketStatus.Закрыто)
        {
            return Json(new { success = false, message = "Ошибка: Данное обращение успешно решено и закрыто. Писать в него больше нельзя!" });
        }

        if (string.IsNullOrWhiteSpace(text) && replyFile == null)
        {
            return Json(new { success = false, message = "Ошибка: Напишите текст сообщения или прикрепите файл!" });
        }

        // Формируем новое сообщение диалога техподдержки
        var message = new SupportMessage
        {
            TicketId = ticketId,
            SenderId = senderId, // Записываем реального автора (студента или админа)
            Text = !string.IsNullOrWhiteSpace(text) ? text.Trim() : "Прикрепленный файл от техподдержки",
            CreatedAt = DateTime.UtcNow
        };

        _context.SupportMessages.Add(message);

        // Поднимаем тикет вверх в админском реестре по дате последнего обновления
        ticket.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        // Сохранение одиночного вложенного файла на диск (если он прикреплен)
        if (replyFile != null && replyFile.Length > 0)
        {
            try
            {
                string uploadFolder = Path.Combine(_env.WebRootPath, "uploads", "support");
                if (!Directory.Exists(uploadFolder))
                {
                    Directory.CreateDirectory(uploadFolder);
                }

                string extension = Path.GetExtension(replyFile.FileName).ToLower();
                string uniqueName = Guid.NewGuid().ToString() + extension;
                string savePath = Path.Combine(uploadFolder, uniqueName);

                using (var stream = new FileStream(savePath, FileMode.Create))
                {
                    await replyFile.CopyToAsync(stream);
                }

                _context.TicketFiles.Add(new TicketFile
                {
                    MessageId = message.Id,
                    FilePath = $"/uploads/support/{uniqueName}",
                    FileName = replyFile.FileName
                });

                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Сообщение отправлено, но файл не сохранился: {ex.Message}" });
            }
        }

        return Json(new { success = true });
    }


    // POST: /Support/CloseTicket
    [HttpPost]
    [Authorize(Roles = "Админ,Главный_Админ")]
    public async Task<IActionResult> CloseTicket(int ticketId)
    {
        var ticket = await _context.SupportTickets.FindAsync(ticketId);
        if (ticket == null) return Json(new { success = false, message = "Обращение не найдено!" });

        ticket.Status = TicketStatus.Закрыто;
        ticket.UpdatedAt = DateTime.UtcNow;

        _context.SupportTickets.Update(ticket);
        await _context.SaveChangesAsync();

        return Json(new { success = true });
    }

    // GET: /Support/GetActiveTicketsCount
    [HttpGet]
    [Authorize(Roles = "Admin,Админ,Главный_Админ")]
    public async Task<IActionResult> GetActiveTicketsCount()
    {
        // Ищем открытые тикеты, у которых последнее сообщение отправлено СТУДЕНТОМ
        // (то есть SenderId последнего сообщения равен StudentId самого тикета)
        int pendingCount = await _context.SupportTickets
            .Where(t => t.Status == TicketStatus.Открыто)
            .CountAsync(t => t.Messages
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => m.SenderId)
                .FirstOrDefault() == t.StudentId);

        return Json(new { count = pendingCount });
    }

    // =================================================================
    // ИСПРАВЛЕНО: Полностью всеядный метод для счетчика студента
    // =================================================================

    // GET: /Support/GetStudentUnreadCount
    [HttpGet]
    [Authorize] // Убрали роли, теперь метод гарантированно доступен любому авторизованному студенту
    public async Task<IActionResult> GetStudentUnreadCount()
    {
        // Безопасный сбор ID студента по всем возможным клеймам
        var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? User.FindFirst("Id")?.Value
                     ?? User.FindFirst("UserId")?.Value;

        if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out int studentId))
        {
            return Json(new { count = 0 });
        }

        // Считаем открытые тикеты этого студента, где последнее сообщение написал КТО-ТО ДРУГОЙ (Администратор)
        int unreadReplies = await _context.SupportTickets
            .Where(t => t.StudentId == studentId && t.Status == TicketStatus.Открыто)
            .CountAsync(t => t.Messages
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => m.SenderId)
                .FirstOrDefault() != studentId);

        return Json(new { count = unreadReplies });
    }
}