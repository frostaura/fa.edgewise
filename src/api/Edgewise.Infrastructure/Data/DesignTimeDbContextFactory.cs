using Edgewise.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Edgewise.Infrastructure.Data;

/// <summary>Used only by the dotnet-ef tooling to build the model for migrations.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<EdgewiseDbContext>
{
    public EdgewiseDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<EdgewiseDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=edgewise_design;Username=edgewise;Password=edgewise")
            .Options;
        return new EdgewiseDbContext(options, new DesignTimeCurrentUser());
    }

    private sealed class DesignTimeCurrentUser : ICurrentUser
    {
        public Guid? UserId => null;
    }
}
