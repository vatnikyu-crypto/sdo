using Microsoft.EntityFrameworkCore;
using SdoApp.Models;
using System;

namespace SdoApp.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<CourseMaterial> CourseMaterials => Set<CourseMaterial>();
    public DbSet<StudentSubmission> StudentSubmissions => Set<StudentSubmission>();
    public DbSet<TestQuestion> TestQuestions => Set<TestQuestion>();
    public DbSet<TestAnswerOption> TestAnswerOptions => Set<TestAnswerOption>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupStudent> GroupStudents => Set<GroupStudent>();
    public DbSet<GroupCourseConfig> GroupCourseConfigs => Set<GroupCourseConfig>();
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 1. Создаем тестовую компанию
        modelBuilder.Entity<Company>().HasData(
            new Company { Id = 1, Name = "ООО ТехноСфера", Inn = "7700000000" }
        );

        // 2. Добавляем 3 пользователей с разными ролями
        modelBuilder.Entity<User>().HasData(
            // ЗАПИСЬ 1: Главный Админ
            new User
            {
                Id = 1,
                Login = "superadmin",
                Password = "123", // Упростим пароли для удобства тестов
                LastName = "Иванов",
                FirstName = "Иван",
                MiddleName = "Иванович",
                BirthDate = new DateTime(1990, 5, 15),
                Snils = "123-456-789 01",
                Gender = Gender.Муж,
                Department = "Центральный офис",
                Role = UserRole.Главный_Админ,
                CompanyId = 1
            },
            // ЗАПИСЬ 2: Обычный Админ
            new User
            {
                Id = 2,
                Login = "admin",
                Password = "123",
                LastName = "Петрова",
                FirstName = "Анна",
                MiddleName = "Сергеевна",
                BirthDate = new DateTime(1993, 8, 22),
                Snils = "987-654-321 00",
                Gender = Gender.Жен,
                Department = "Отдел кадров",
                Role = UserRole.Админ,
                CompanyId = 1
            },
            // ЗАПИСЬ 3: Слушатель (Обучающийся)
            new User
            {
                Id = 3,
                Login = "student",
                Password = "123",
                LastName = "Сидоров",
                FirstName = "Алексей",
                MiddleName = "Николаевич",
                BirthDate = new DateTime(2001, 11, 3),
                Snils = "456-123-789 55",
                Gender = Gender.Муж,
                Department = "Цех сборки №2",
                Role = UserRole.Обучающийся,
                CompanyId = 1
            }
        );
    }
}
