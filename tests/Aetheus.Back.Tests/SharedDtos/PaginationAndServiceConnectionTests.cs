// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Tests.SharedDtos;

/// <summary>
/// Tests for PaginationRequest.Normalize(), PaginatedResult computed properties,
/// and pagination request subtypes.
/// </summary>
public class PaginationAndServiceConnectionTests
{
    // --- PaginationRequest.Normalize ---

    [Fact]
    public void Normalize_DefaultValues_Returns1And25()
    {
        var req = new PaginationRequest();
        var (page, pageSize) = req.Normalize();
        Assert.Equal(1, page);
        Assert.Equal(25, pageSize);
    }

    [Fact]
    public void Normalize_NegativePage_ClampsTo1()
    {
        var req = new PaginationRequest { Page = -5, PageSize = 10 };
        var (page, _) = req.Normalize();
        Assert.Equal(1, page);
    }

    [Fact]
    public void Normalize_ZeroPage_ClampsTo1()
    {
        var req = new PaginationRequest { Page = 0, PageSize = 10 };
        var (page, _) = req.Normalize();
        Assert.Equal(1, page);
    }

    [Fact]
    public void Normalize_PageSizeExceedsMax_ClampedTo200()
    {
        var req = new PaginationRequest { Page = 1, PageSize = 500 };
        var (_, pageSize) = req.Normalize();
        Assert.Equal(200, pageSize);
    }

    [Fact]
    public void Normalize_PageSizeZero_ClampedTo1()
    {
        var req = new PaginationRequest { Page = 1, PageSize = 0 };
        var (_, pageSize) = req.Normalize();
        Assert.Equal(1, pageSize);
    }

    [Fact]
    public void Normalize_ValidValues_PassThrough()
    {
        var req = new PaginationRequest { Page = 3, PageSize = 50 };
        var (page, pageSize) = req.Normalize();
        Assert.Equal(3, page);
        Assert.Equal(50, pageSize);
    }

    // --- PaginatedResult<T> computed properties ---

    [Fact]
    public void PaginatedResult_TotalPages_CalculatesCorrectly()
    {
        var result = new PaginatedResult<string> { TotalCount = 100, PageSize = 25 };
        Assert.Equal(4, result.TotalPages);
    }

    [Fact]
    public void PaginatedResult_TotalPages_ZeroPageSize_ReturnsZero()
    {
        var result = new PaginatedResult<string> { TotalCount = 100, PageSize = 0 };
        Assert.Equal(0, result.TotalPages);
    }

    [Fact]
    public void PaginatedResult_HasPrevious_Page1_False()
    {
        var result = new PaginatedResult<string> { Page = 1, TotalCount = 100, PageSize = 25 };
        Assert.False(result.HasPrevious);
    }

    [Fact]
    public void PaginatedResult_HasPrevious_Page2_True()
    {
        var result = new PaginatedResult<string> { Page = 2, TotalCount = 100, PageSize = 25 };
        Assert.True(result.HasPrevious);
    }

    [Fact]
    public void PaginatedResult_HasNext_LastPage_False()
    {
        var result = new PaginatedResult<string> { Page = 4, TotalCount = 100, PageSize = 25 };
        Assert.False(result.HasNext);
    }

    [Fact]
    public void PaginatedResult_HasNext_MiddlePage_True()
    {
        var result = new PaginatedResult<string> { Page = 2, TotalCount = 100, PageSize = 25 };
        Assert.True(result.HasNext);
    }

    // --- ServiceConnectionDetailDto ---

    [Fact]
    public void ServiceConnectionDetailDto_Defaults()
    {
        var dto = new ServiceConnectionDetailDto();
        Assert.Equal(0, dto.Id);
        Assert.Equal(string.Empty, dto.Name);
        Assert.Null(dto.Description);
        Assert.Equal("{}", dto.ConfigurationJson);
        Assert.Null(dto.Url);
        Assert.Null(dto.ProjectId);
        Assert.Null(dto.ProjectName);
    }
}
