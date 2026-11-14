using SdoApp.Data;
using SdoApp.Models;

namespace SdoApp.Services;

public class NotificationService
{
    private readonly AppDbContext _context;

    public NotificationService(AppDbContext context)
    {
        _context = context;
    }

    public async Task SendAsync(int userId, string message, string? link = null)
    {
        var notification = new UserNotification
        {
            UserId = userId,
            Message = message,
            Link = link,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        _context.UserNotifications.Add(notification);
        await _context.SaveChangesAsync();
    }
}