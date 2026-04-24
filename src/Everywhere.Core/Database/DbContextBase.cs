using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Everywhere.Database;

[method: DynamicDependency(DynamicallyAccessedMemberTypes.AllConstructors, typeof(DateTimeOffsetToTicksConverter))]
public abstract class DbContextBase(DbContextOptions options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder
            .Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToTicksConverter>();
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.AllConstructors)]
    private class DateTimeOffsetToTicksConverter() : ValueConverter<DateTimeOffset, long>(v => v.Ticks, v => new DateTimeOffset(v, TimeSpan.Zero));
}