using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using System;
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
    // AJAX-метод для создания компании
    [HttpPost]
    public async Task<IActionResult> Create(string Name, string Inn)
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Inn))
        {
            return Json(new { success = false, message = "Все поля обязательны для заполнения!" });
        }

        // Проверка формата ИНН на сервере
        if (Inn.Trim().Length != 10 && Inn.Trim().Length != 12)
        {
            return Json(new { success = false, message = "ИНН должен содержать 10 или 12 цифр." });
        }

        try
        {
            // Проверка уникальности ИНН
            var isInnDuplicate = await _context.Companies.AnyAsync(c => c.Inn == Inn.Trim());
            if (isInnDuplicate)
            {
                return Json(new { success = false, message = $"Организация с ИНН '{Inn}' уже зарегистрирована в системе!" });
            }

            var newCompany = new Company
            {
                Name = Name.Trim(),
                Inn = Inn.Trim()
            };

            _context.Companies.Add(newCompany);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка БД: {ex.Message}" });
        }
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
    // AJAX-метод для редактирования компании
    [HttpPost]
    public async Task<IActionResult> Edit(int id, string Name, string Inn)
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Inn))
        {
            return Json(new { success = false, message = "Все поля обязательны для заполнения!" });
        }

        if (Inn.Trim().Length != 10 && Inn.Trim().Length != 12)
        {
            return Json(new { success = false, message = "ИНН должен содержать 10 или 12 цифр." });
        }

        try
        {
            var company = await _context.Companies.FindAsync(id);
            if (company == null) 
            {
                return Json(new { success = false, message = "Организация не найдена!" });
            }

            // Проверка уникальности ИНН среди ДРУГИХ компаний
            var isInnDuplicate = await _context.Companies.AnyAsync(c => c.Inn == Inn.Trim() && c.Id != id);
            if (isInnDuplicate)
            {
                return Json(new { success = false, message = $"ИНН '{Inn}' уже принадлежит другой организации!" });
            }

            company.Name = Name.Trim();
            company.Inn = Inn.Trim();

            _context.Companies.Update(company);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка при обновлении: {ex.Message}" });
        }
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

        try
        {
            _context.Companies.Remove(company);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка при удалении: {ex.Message}" });
        }
    }
}