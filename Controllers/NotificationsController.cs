using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Helpers;

namespace SdoApp.Controllers;

[Authorize]
public class NotificationsController : Controller
{
    private readonly AppDbContext _context;

    public NotificationsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> GetLatest()
    {
        int userId = User.GetUserId();
        if (userId == 0) return Unauthorized();

        // НОВАЯ ПРОВЕРКА: если согласие не дано, отдаем пустоту
        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null || !user.IsPrivacyAccepted) return Json(new List<object>());

        var notifications = await _context.UserNotifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(5)
            .Select(n => new
            {
                id = n.Id,
                message = n.Message,
                link = n.Link,
                isRead = n.IsRead,
                date = n.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            })
            .ToListAsync();

        return Json(notifications);
    }

    [HttpGet]
    public async Task<IActionResult> GetUnreadCount()
    {
        int userId = User.GetUserId();
        if (userId == 0) return Json(new { count = 0 });

        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null || !user.IsPrivacyAccepted) return Json(new { count = 0 });

        int count = await _context.UserNotifications
            .CountAsync(n => n.UserId == userId && !n.IsRead);

        return Json(new { count });
    }

    [HttpPost]
    public async Task<IActionResult> MarkAllAsRead()
    {
        int userId = User.GetUserId();
        if (userId == 0) return Unauthorized();

        var unread = await _context.UserNotifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ToListAsync();

        if (unread.Any())
        {
            foreach (var n in unread) { n.IsRead = true; }
            await _context.SaveChangesAsync();
        }

        return Json(new { success = true });
    }
}