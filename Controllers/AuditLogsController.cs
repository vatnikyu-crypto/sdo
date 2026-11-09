using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models.ViewModels;

namespace SdoApp.Controllers;

[Authorize(Roles = "Админ,Главный_Админ")]
public class AuditLogsController : Controller
{
    private readonly AppDbContext _context;

    public AuditLogsController(AppDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index(string searchString, int page = 1)
    {
        var query = _context.AuditLogs
            .Include(l => l.User)
            .AsQueryable();

        // 1. Поиск (по описанию, действию или ФИО)
        if (!string.IsNullOrWhiteSpace(searchString))
        {
            searchString = searchString.Trim().ToLower();
            query = query.Where(l => 
                l.Description.ToLower().Contains(searchString) ||
                l.Action.ToLower().Contains(searchString) ||
                (l.User != null && (l.User.LastName.ToLower().Contains(searchString) || 
                                    l.User.FirstName.ToLower().Contains(searchString))));
        }

        // 2. Сортировка (сначала новые)
        query = query.OrderByDescending(l => l.Timestamp);

        // 3. Пагинация
        int totalItems = await query.CountAsync();
        int pageSize = 50; // Размер страницы
        int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
        
        // Защита от выхода за границы
        if (page < 1) page = 1;
        if (page > totalPages && totalPages > 0) page = totalPages;

        var logs = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

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
}