using FluentAssertions;
using VirtualStore.Application.Common;
using Xunit;

namespace VirtualStore.UnitTests.Common;

public class PagedResultTests
{
    [Fact]
    public void Defaults_Are_Page1_Size20()
    {
        var result = new PagedResult<int>();
        result.PageNumber.Should().Be(1);
        result.PageSize.Should().Be(20);
        result.TotalCount.Should().Be(0);
        result.TotalPages.Should().Be(0);
        result.HasPrevious.Should().BeFalse();
        result.HasNext.Should().BeFalse();
        result.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void PageNumber_Below_1_Clamps_To_1(int page)
    {
        var result = new PagedResult<int> { PageNumber = page };
        result.PageNumber.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void PageSize_NonPositive_Resets_To_20(int size)
    {
        var result = new PagedResult<int> { PageSize = size };
        result.PageSize.Should().Be(20);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(1000, 100)]
    public void PageSize_Above_100_Clamps_To_100(int input, int expected)
    {
        var result = new PagedResult<int> { PageSize = input };
        result.PageSize.Should().Be(expected);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-999)]
    public void TotalCount_Negative_Clamps_To_0(int total)
    {
        var result = new PagedResult<int> { TotalCount = total };
        result.TotalCount.Should().Be(0);
    }

    [Theory]
    [InlineData(0, 20, 0)]
    [InlineData(1, 20, 1)]
    [InlineData(20, 20, 1)]
    [InlineData(21, 20, 2)]
    [InlineData(100, 10, 10)]
    [InlineData(101, 10, 11)]
    public void TotalPages_Is_Ceiling_Of_TotalDivSize(int total, int size, int expectedPages)
    {
        var result = new PagedResult<int> { TotalCount = total, PageSize = size };
        result.TotalPages.Should().Be(expectedPages);
    }

    [Fact]
    public void Items_Null_Assigns_Empty_List()
    {
        var result = new PagedResult<int> { Items = null! };
        result.Items.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void HasPrevious_True_When_Page_Greater_Than_1()
    {
        var result = new PagedResult<int> { PageNumber = 2, TotalCount = 50, PageSize = 20 };
        result.HasPrevious.Should().BeTrue();
    }

    [Theory]
    [InlineData(1, 50, 20, true)]
    [InlineData(2, 50, 20, true)]
    [InlineData(3, 50, 20, false)]
    [InlineData(1, 0, 20, false)]
    public void HasNext_Follows_TotalPages(int page, int total, int size, bool expected)
    {
        var result = new PagedResult<int> { PageNumber = page, TotalCount = total, PageSize = size };
        result.HasNext.Should().Be(expected);
    }
}
