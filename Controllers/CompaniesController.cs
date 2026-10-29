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
     private readonly IWebHostEnvironment _env;

    public CompaniesController(AppDbContext context, IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
    }

    // GET: /Companies/Index
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var companies = await _context.Companies
            .Include(c => c.Users)
            .OrderByDescending(c => c.Id) // Подгружаем пользователей, чтобы посчитать их количество
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
    public async Task<IActionResult> Create(string Name, string Inn, IFormFile? logoFile)
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

            string? savedLogoUrl = null;

            if (logoFile != null && logoFile.Length > 0)
            {
                var extension = Path.GetExtension(logoFile.FileName).ToLower();
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".svg" };
                
                if (!allowedExtensions.Contains(extension))
                {
                    return Json(new { success = false, message = "Логотип должен быть картинкой (.jpg, .png, .webp, .svg)!" });
                }

                string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "companies");
                if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

                string uniqueFileName = Guid.NewGuid().ToString() + extension;
                string fileSavePath = Path.Combine(uploadsFolder, uniqueFileName);

                using (var fileStream = new FileStream(fileSavePath, FileMode.Create))
                {
                    await logoFile.CopyToAsync(fileStream);
                }
                savedLogoUrl = $"/uploads/companies/{uniqueFileName}";
            }

            var newCompany = new Company
            {
                Name = Name.Trim(),
                Inn = Inn.Trim(),
                LogoUrl = savedLogoUrl
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
        ViewBag.LogoUrl = company.LogoUrl;
        return View();
    }

    // POST: /Companies/Edit/5
    // AJAX-метод для редактирования компании
    [HttpPost]
    public async Task<IActionResult> Edit(int id, string Name, string Inn, IFormFile? logoFile)
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

            if (logoFile != null && logoFile.Length > 0)
            {
                var extension = Path.GetExtension(logoFile.FileName).ToLower();
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".svg" };
                
                if (!allowedExtensions.Contains(extension))
                {
                    return Json(new { success = false, message = "Логотип должен быть картинкой (.jpg, .png, .webp, .svg)!" });
                }

                // 1. Удаляем старый файл логотипа, если он был
                if (!string.IsNullOrEmpty(company.LogoUrl) && company.LogoUrl.StartsWith("/uploads/"))
                {
                    string oldFilePath = Path.Combine(_env.WebRootPath, company.LogoUrl.TrimStart('/'));
                    if (System.IO.File.Exists(oldFilePath))
                    {
                        System.IO.File.Delete(oldFilePath);
                    }
                }

                // 2. Сохраняем новый файл
                string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "companies");
                if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

                string uniqueFileName = Guid.NewGuid().ToString() + extension;
                string fileSavePath = Path.Combine(uploadsFolder, uniqueFileName);

                using (var fileStream = new FileStream(fileSavePath, FileMode.Create))
                {
                    await logoFile.CopyToAsync(fileStream);
                }
                
                company.LogoUrl = $"/uploads/companies/{uniqueFileName}";
            }

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
            // УДАЛЯЕМ ФИЗИЧЕСКИЙ ФАЙЛ ЛОГОТИПА, ЕСЛИ ОН ЕСТЬ
            if (!string.IsNullOrEmpty(company.LogoUrl) && company.LogoUrl.StartsWith("/uploads/"))
            {
                string absolutePath = Path.Combine(_env.WebRootPath, company.LogoUrl.TrimStart('/'));
                if (System.IO.File.Exists(absolutePath))
                {
                    System.IO.File.Delete(absolutePath);
                }
            }

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