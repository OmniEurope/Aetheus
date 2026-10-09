// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Cron;
using Aetheus.Back.Components.SystemLogs;
using Aetheus.Back.Configuration;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Services;

public class ConfigAndControllerTests
{
    // --- Config classes ---

    [Fact]
    public void AuthOptions_Defaults()
    {
        var opt = new AuthOptions();
        Assert.Equal("Auth", AuthOptions.SectionName);
        Assert.Null(opt.JwtKey);
        Assert.Null(opt.JwtIssuer);
        Assert.Null(opt.JwtAudience);
        Assert.Equal(30, opt.AccessTokenMinutes);
        Assert.Equal(14, opt.RefreshTokenDays);
        Assert.Null(opt.AdminUser);
        Assert.Null(opt.AdminPassword);
        Assert.Null(opt.EncryptionKey);
        Assert.Null(opt.EncryptionSalt);
    }

    [Fact]
    public void AuthOptions_CanSet()
    {
        var opt = new AuthOptions
        {
            JwtKey = "key",
            JwtIssuer = "iss",
            JwtAudience = "aud",
            AccessTokenMinutes = 60,
            RefreshTokenDays = 7,
            AdminUser = "admin",
            AdminPassword = "pass",
            EncryptionKey = "ekey",
            EncryptionSalt = "esalt"
        };
        Assert.Equal("key", opt.JwtKey);
        Assert.Equal(60, opt.AccessTokenMinutes);
    }

    [Fact]
    public void AgentTokenOptions_Defaults()
    {
        var opt = new AgentTokenOptions();
        Assert.Equal("Auth:AgentToken", AgentTokenOptions.SectionName);
        Assert.Equal(30, opt.RotationDays);
        Assert.Equal(60, opt.GracePeriodMinutes);
    }

    [Fact]
    public void AgentTokenOptions_CanSet()
    {
        var opt = new AgentTokenOptions { RotationDays = 90, GracePeriodMinutes = 120 };
        Assert.Equal(90, opt.RotationDays);
    }

    [Fact]
    public void EncryptionOptions_Defaults()
    {
        var opt = new EncryptionOptions();
        Assert.Equal("Auth", EncryptionOptions.SectionName);
        Assert.Null(opt.EncryptionKey);
        Assert.Null(opt.EncryptionSalt);
    }

    [Fact]
    public void EncryptionOptions_CanSet()
    {
        var opt = new EncryptionOptions { EncryptionKey = "k", EncryptionSalt = "s" };
        Assert.Equal("k", opt.EncryptionKey);
    }

    // --- SystemLogsController ---

    [Fact]
    public async Task SystemLogsController_GetLogFiles_ReturnsOk()
    {
        var mockService = Substitute.For<ISystemLogService>();
        mockService.GetLogFilesAsync(Arg.Any<CancellationToken>())
            .Returns([new SystemLogFileDto("syslog", 1024, DateTime.UtcNow)]);

        var controller = new SystemLogsController(mockService, Substitute.For<Aetheus.Back.Components.Settings.ISettingsService>());
        var result = await controller.GetLogFiles(TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var files = Assert.IsType<List<SystemLogFileDto>>(ok.Value);
        Assert.Single(files);
    }

    [Fact]
    public async Task SystemLogsController_GetLogEntries_ClampsPageSize()
    {
        var mockService = Substitute.For<ISystemLogService>();
        mockService.GetLogEntriesAsync(null, null, null, null, null, 1, 200, Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<SystemLogEntryDto> { Items = [], TotalCount = 0 });

        var controller = new SystemLogsController(mockService, Substitute.For<Aetheus.Back.Components.Settings.ISettingsService>());
        var result = await controller.GetLogEntries(null, null, null, null, null, 0, 999, ct: TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
        // Verifies page=Max(1,0)=1 and pageSize=Clamp(999,1,200)=200 were passed
        await mockService.Received(1).GetLogEntriesAsync(null, null, null, null, null, 1, 200, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SystemLogsController_PurgeOldLogs_ReturnsCount()
    {
        var mockService = Substitute.For<ISystemLogService>();
        mockService.PurgeOldLogsAsync(30, Arg.Any<CancellationToken>())
            .Returns(42);

        var controller = new SystemLogsController(mockService, Substitute.For<Aetheus.Back.Components.Settings.ISettingsService>());
        var result = await controller.PurgeOldLogs(30, ct: TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(42, ok.Value);
    }

    [Fact]
    public async Task SystemLogsController_DownloadLogFile_ReturnsFile()
    {
        var mockService = Substitute.For<ISystemLogService>();
        mockService.DownloadLogFileAsync("auth.log", Arg.Any<CancellationToken>())
            .Returns(new MemoryStream([1, 2, 3]));

        var controller = new SystemLogsController(mockService, Substitute.For<Aetheus.Back.Components.Settings.ISettingsService>());
        var result = await controller.DownloadLogFile("auth.log", TestContext.Current.CancellationToken);

        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/octet-stream", fileResult.ContentType);
        Assert.Equal("auth.log", fileResult.FileDownloadName);
        Assert.True(fileResult.EnableRangeProcessing);
    }
}
