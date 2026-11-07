using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SdoApp.Controllers
{
    [Authorize(Roles = "Главный_Админ")]
    public class SuperAdminController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IWebHostEnvironment _env;

        public SuperAdminController(AppDbContext context, IWebHostEnvironment env)
        {
            _context = context;
            _env = env;
        }

        // ==================== СУЩЕСТВУЮЩИЕ МЕТОДЫ ====================

        public async Task<IActionResult> Index()
        {
            var admins = await _context.Users
                .Where(u => u.Role == UserRole.Админ)
                .OrderByDescending(u => u.Id)
                .ToListAsync();
            return View(admins);
        }

        [HttpGet]
        public IActionResult CreateAdmin() => View();

        [HttpPost]
        public async Task<IActionResult> CreateAdmin(User model, string passwordConfirm)
        {
            if (string.IsNullOrWhiteSpace(model.Login) || string.IsNullOrWhiteSpace(model.Password))
                return Json(new { success = false, message = "Логин и пароль обязательны!" });

            if (model.Password != passwordConfirm)
                return Json(new { success = false, message = "Пароли не совпадают!" });

            if (await _context.Users.AnyAsync(u => u.Login == model.Login))
                return Json(new { success = false, message = "Пользователь с таким логином уже существует." });

            model.Role = UserRole.Админ;
            _context.Users.Add(model);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> EditAdmin(int id)
        {
            var admin = await _context.Users.FirstOrDefaultAsync(u => u.Id == id && u.Role == UserRole.Админ);
            if (admin == null) return NotFound();
            return View(admin);
        }

        [HttpPost]
        public async Task<IActionResult> EditAdmin(User model, string? newPassword)
        {
            var admin = await _context.Users.FirstOrDefaultAsync(u => u.Id == model.Id && u.Role == UserRole.Админ);
            if (admin == null) return Json(new { success = false, message = "Администратор не найден." });

            if (await _context.Users.AnyAsync(u => u.Login == model.Login && u.Id != model.Id))
                return Json(new { success = false, message = "Этот логин уже занят другим пользователем." });

            admin.LastName = model.LastName;
            admin.FirstName = model.FirstName;
            admin.MiddleName = model.MiddleName;
            admin.Login = model.Login;
            admin.Position = model.Position;
            admin.Department = model.Department;

            if (!string.IsNullOrWhiteSpace(newPassword))
                admin.Password = newPassword;

            _context.Users.Update(admin);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteAdmin(int id)
        {
            var admin = await _context.Users.FirstOrDefaultAsync(u => u.Id == id && u.Role == UserRole.Админ);
            if (admin == null) return Json(new { success = false, message = "Администратор не найден." });

            try
            {
                _context.Users.Remove(admin);
                await _context.SaveChangesAsync();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Ошибка при удалении: {ex.Message}" });
            }
        }

        // ==================== РЕЗЕРВНОЕ КОПИРОВАНИЕ БД ====================

        // Папка для бэкапов: <корень проекта>/backups (рядом с sdo.db, НЕ в wwwroot)
        private string GetBackupFolder()
        {
            string folder = Path.Combine(_env.ContentRootPath, "backups");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return folder;
        }

        [HttpPost]
        public async Task<IActionResult> BackupDatabase()
        {
            try
            {
                string folder = GetBackupFolder();
                string fileName = $"sdo_{DateTime.Now:yyyyMMdd_HHmmss_fff}.db";
                string fullPath = Path.Combine(folder, fileName);

                // VACUUM INTO — атомарный снимок БД без блокировки чтения/записи.
                // Если файл с таким именем уже существует — SQLite выдаст ошибку (защита от перезаписи).
                string escapedPath = fullPath.Replace("'", "''");
                await _context.Database.ExecuteSqlRawAsync($"VACUUM INTO '{escapedPath}'");

                // Проверка, что файл действительно создан и не пустой
                var info = new FileInfo(fullPath);
                if (!info.Exists || info.Length == 0)
                    return Json(new { success = false, message = "Файл бэкапа не создан или пуст." });

                // Оставляем последние 10 бэкапов, старые удаляем
                var oldFiles = new DirectoryInfo(folder)
                    .GetFiles("sdo_*.db")
                    .OrderByDescending(f => f.CreationTime)
                    .Skip(10)
                    .ToList();

                foreach (var old in oldFiles)
                {
                    try { old.Delete(); } catch { /* пропускаем, если занят */ }
                }

                return Json(new
                {
                    success = true,
                    fileName,
                    sizeMb = Math.Round(info.Length / 1024.0 / 1024.0, 2)
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Ошибка создания бэкапа: {ex.Message}" });
            }
        }

        [HttpGet]
        public IActionResult DownloadBackup(string fileName)
        {
            // Защита от path traversal
            if (string.IsNullOrWhiteSpace(fileName)
                || fileName.Contains("..")
                || fileName.Contains("/")
                || fileName.Contains("\\")
                || !fileName.StartsWith("sdo_")
                || !fileName.EndsWith(".db"))
            {
                return BadRequest("Некорректное имя файла.");
            }

            string fullPath = Path.Combine(GetBackupFolder(), fileName);
            if (!System.IO.File.Exists(fullPath))
                return NotFound("Файл бэкапа не найден.");

            var bytes = System.IO.File.ReadAllBytes(fullPath);
            return File(bytes, "application/octet-stream", fileName);
        }

        [HttpGet]
        public IActionResult ListBackups()
        {
            try
            {
                var files = new DirectoryInfo(GetBackupFolder())
                    .GetFiles("sdo_*.db")
                    .OrderByDescending(f => f.CreationTime)
                    .Select(f => new
                    {
                        fileName = f.Name,
                        sizeMb = Math.Round(f.Length / 1024.0 / 1024.0, 2),
                        createdAt = f.CreationTime.ToString("dd.MM.yyyy HH:mm:ss")
                    })
                    .ToList();

                return Json(new { success = true, files });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
    }
}