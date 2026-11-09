using Microsoft.AspNetCore.Http;
using SdoApp.Data;
using SdoApp.Models;
using SdoApp.Helpers;

namespace SdoApp.Services;

public class AuditService
{
    private readonly AppDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuditService(AppDbContext context, IHttpContextAccessor httpContextAccessor)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
    }

    // ДОБАВЛЕН ПАРАМЕТР userId
    public async Task LogAsync(string action, string entityType, int? entityId, string description, int? userId = null)
    {
        // Если userId передан вручную, используем его. Иначе берем из контекста.
        int finalUserId = userId ?? _httpContextAccessor.HttpContext?.User.GetUserId() ?? 0;

        var log = new AuditLog
        {
            UserId = finalUserId == 0 ? null : finalUserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Description = description,
            Timestamp = DateTime.UtcNow
        };

        _context.AuditLogs.Add(log);
        await _context.SaveChangesAsync();
    }
}