// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Settings;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class SettingsControllerTests
{
    private readonly ISettingsService _serviceMock = Substitute.For<ISettingsService>();
    private readonly SettingsController _sut;

    public SettingsControllerTests()
    {
        _sut = new SettingsController(_serviceMock, Substitute.For<IEmailSenderSettingsService>());
    }

    [Fact]
    public async Task GetSettings_ReturnsOk()
    {
        var settings = new List<AppSettingDto> { new() { Key = "k", Value = "v" } };
        _serviceMock.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var result = await _sut.GetSettings(TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<List<AppSettingDto>>(ok.Value);
        Assert.Single(data);
    }

    [Fact]
    public async Task UpdateSetting_ReturnsOk()
    {
        var result = await _sut.UpdateSetting("theme", new AppSettingDto { Value = "dark" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
        await _serviceMock.Received(1).UpdateSettingAsync("theme", "dark", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSecrets_ReturnsOk()
    {
        var secrets = new List<SecretDto> { new() { Id = 1, Key = "API_KEY" } };
        _serviceMock.GetSecretsAsync(Arg.Any<CancellationToken>()).Returns(secrets);

        var result = await _sut.GetSecrets(TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<List<SecretDto>>(ok.Value);
    }

    [Fact]
    public async Task CreateSecret_ReturnsOk()
    {
        var request = new CreateSecretRequest { Key = "K", Value = "V" };
        var dto = new SecretDto { Id = 1, Key = "K" };
        _serviceMock.CreateSecretAsync(request, Arg.Any<CancellationToken>()).Returns(dto);

        var result = await _sut.CreateSecret(request, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<SecretDto>(ok.Value);
    }

    [Fact]
    public async Task DeleteSecret_Found_ReturnsNoContent()
    {
        _serviceMock.DeleteSecretAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeleteSecret(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteSecret_NotFound_Returns404()
    {
        _serviceMock.DeleteSecretAsync(99, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.DeleteSecret(99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }
}
