using FluentAssertions;
using VirtualStore.Domain.Enums;
using Xunit;

namespace VirtualStore.UnitTests.Domain;

public class UserRolesTests
{
    [Fact]
    public void EnsureValid_Null_Throws_ArgumentNullException()
    {
        var act = () => UserRoles.EnsureValid(null);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EnsureValid_Empty_Throws_ArgumentException()
    {
        var act = () => UserRoles.EnsureValid(Array.Empty<UserRole>());
        act.Should().Throw<ArgumentException>().WithMessage("*At least one role*");
    }

    [Fact]
    public void EnsureValid_Undefined_Value_Throws_ArgumentOutOfRangeException()
    {
        var act = () => UserRoles.EnsureValid(new[] { (UserRole)99 });
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void EnsureValid_Mixed_Valid_And_Undefined_Throws()
    {
        var act = () => UserRoles.EnsureValid(new[] { UserRole.Customer, (UserRole)7 });
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void EnsureValid_Duplicates_Are_Deduplicated()
    {
        var result = UserRoles.EnsureValid(new[] { UserRole.Customer, UserRole.Customer, UserRole.Admin });
        result.Should().BeEquivalentTo(new[] { UserRole.Customer, UserRole.Admin });
        result.Should().HaveCount(2);
    }

    [Fact]
    public void EnsureValid_Single_Role_Returns_Singleton()
    {
        var result = UserRoles.EnsureValid(new[] { UserRole.Manager });
        result.Should().ContainSingle().Which.Should().Be(UserRole.Manager);
    }

    [Fact]
    public void EnsureValid_All_Defined_Roles_Pass()
    {
        var result = UserRoles.EnsureValid(new[] { UserRole.Customer, UserRole.Manager, UserRole.Admin });
        result.Should().HaveCount(3);
    }
}
