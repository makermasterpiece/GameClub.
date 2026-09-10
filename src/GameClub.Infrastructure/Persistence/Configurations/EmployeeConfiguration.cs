using GameClub.Domain.Employees;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameClub.Infrastructure.Persistence.Configurations;

public sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> b)
    {
        b.ToTable("employees");
        b.HasKey(e => e.Id);
        b.Property(e => e.Username).HasMaxLength(Employee.MaximumUsernameLength).IsRequired();
        b.Property(e => e.NormalizedUsername).HasMaxLength(Employee.MaximumUsernameLength).IsRequired();
        b.HasIndex(e => e.NormalizedUsername).IsUnique();
        b.Property(e => e.PasswordHash).HasMaxLength(1024).IsRequired();
        b.Property(e => e.Role).HasConversion<string>().HasMaxLength(20);
        b.Property(e => e.SecurityStamp).IsConcurrencyToken();
    }
}
