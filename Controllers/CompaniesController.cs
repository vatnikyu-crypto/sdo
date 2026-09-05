using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using System.Linq;
using System.Threading.Tasks;

namespace SdoApp.Controllers;

[Authorize(Roles = "Админ")] // Доступ только для обычного Администратора
public class CompaniesController : Controller
{
    private readonly AppDbContext _context;

    public CompaniesController(AppDbContext context)
    {
        _context = context;
    }

    // GET: /Companies/Index
    // Вывод списка всех компаний с подсчетом сотрудников
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var companies = await _context.Companies
            .Include(c => c.Users) // Подгружаем пользователей, чтобы посчитать их количество
            .ToListAsync();
            
        return View(companies);
    }

    // GET: /Companies/Create
    [HttpGet]
    public IActionResult Create()
    {
        return View();
    }

    // POST: /Companies/Create
    // Добавление новой компании с проверкой уникальности ИНН
    [HttpPost]
    public async Task<IActionResult> Create(string name, string inn)
    {
        ViewBag.Name = name;
        ViewBag.Inn = inn;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(inn))
        {
            ModelState.AddModelError("", "Ошибка: Все поля обязательны для заполнения!");
            return View();
        }

        // Проверка уникальности ИНН
        var isInnDuplicate = await _context.Companies.AnyAsync(c => c.Inn == inn.Trim());
        if (isInnDuplicate)
        {
            ModelState.AddModelError("", $"Ошибка: Организация с ИНН '{inn}' уже зарегистрирована в системе!");
            return View();
        }

        var newCompany = new Company
        {
            Name = name.Trim(),
            Inn = inn.Trim()
        };

        _context.Companies.Add(newCompany);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    // GET: /Companies/Edit/5
    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var company = await _context.Companies.FindAsync(id);
        if (company == null) return NotFound();

        ViewBag.Name = company.Name;
        ViewBag.Inn = company.Inn;
        return View();
    }

    // POST: /Companies/Edit/5
    [HttpPost]
    public async Task<IActionResult> Edit(int id, string name, string inn)
    {
        ViewBag.Name = name;
        ViewBag.Inn = inn;

        var company = await _context.Companies.FindAsync(id);
        if (company == null) return NotFound();

        // Проверка уникальности ИНН среди ДРУГИХ компаний
        var isInnDuplicate = await _context.Companies.AnyAsync(c => c.Inn == inn.Trim() && c.Id != id);
        if (isInnDuplicate)
        {
            ModelState.AddModelError("", $"Ошибка: ИНН '{inn}' уже принадлежит другой организации!");
            return View();
        }

        company.Name = name.Trim();
        company.Inn = inn.Trim();

        _context.Companies.Update(company);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    // POST: /Companies/Delete/5
    // Безопасное удаление компании (с запретом удаления, если есть привязанные люди)
    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var company = await _context.Companies.Include(c => c.Users).FirstOrDefaultAsync(c => c.Id == id);
        if (company == null)
        {
            return Json(new { success = false, message = "Организация не найдена!" });
        }

        // Жесткое бизнес-правило: нельзя удалить компанию, если в ней учатся люди
        if (company.Users.Any())
        {
            return Json(new { success = false, message = $"Нельзя удалить компанию '{company.Name}', так как к ней привязано {company.Users.Count} слушателей! Сначала переведите или удалите сотрудников." });
        }

        _context.Companies.Remove(company);
        await _context.SaveChangesAsync();

        return Json(new { success = true });
    }
}
