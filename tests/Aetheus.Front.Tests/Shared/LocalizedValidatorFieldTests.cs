// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// Tests <see cref="LocalizedDataAnnotationsValidator.ValidateField"/> by rendering the
/// component inside an EditForm and triggering field-change events.
/// </summary>
public class LocalizedValidatorFieldTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    public LocalizedValidatorFieldTests()
    {
        BunitTestHelper.RegisterServices(this);
    }

    // ── Model helpers ──────────────────────────────────────────────────────────

    private class RequiredModel
    {
        [Required] public string Name { get; set; } = string.Empty;
    }

    private class StringLenModel
    {
        [StringLength(10, MinimumLength = 2)] public string Tag { get; set; } = "x";
    }

    private class MaxLenModel
    {
        [MaxLength(5)] public string Code { get; set; } = string.Empty;
    }

    private class RangeModel
    {
        [Range(1, 100)] public int Age { get; set; } = 50;
    }

    private class EmailModel
    {
        [EmailAddress] public string Email { get; set; } = string.Empty;
    }

    private class UrlModel
    {
        [Url] public string Website { get; set; } = string.Empty;
    }

    private sealed class ModelLevelValidation : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            yield return new ValidationResult("Model-level failure");
        }
    }

    // ── ValidateAll triggered by Validate() ────────────────────────────────────

    [Fact]
    public async Task ValidateAll_Required_AddsMessage()
    {
        var model = new RequiredModel { Name = string.Empty };
        var editContext = new EditContext(model);

        var instance = CreateValidator(editContext);
        await editContext.ValidateAsync(Xunit.TestContext.Current.CancellationToken);

        var msgs = editContext.GetValidationMessages(editContext.Field(nameof(RequiredModel.Name))).ToList();
        Assert.NotEmpty(msgs);
    }

    [Fact]
    public async Task ValidateAll_Valid_NoMessages()
    {
        var model = new RequiredModel { Name = "Alice" };
        var editContext = new EditContext(model);

        CreateValidator(editContext);
        await editContext.ValidateAsync(Xunit.TestContext.Current.CancellationToken);

        var msgs = editContext.GetValidationMessages(editContext.Field(nameof(RequiredModel.Name))).ToList();
        Assert.Empty(msgs);
    }

    [Fact]
    public async Task ValidateAll_ModelLevelError_IsPreservedInValidationSummary()
    {
        var model = new ModelLevelValidation();
        var editContext = new EditContext(model);
        CreateValidator(editContext);

        await editContext.ValidateAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Contains("Model-level failure", editContext.GetValidationMessages());
    }

    // ── ValidateField via OnFieldChanged ──────────────────────────────────────

    [Fact]
    public void ValidateField_Required_EmptyValue_AddsMessage()
    {
        var model = new RequiredModel { Name = string.Empty };
        var editContext = new EditContext(model);

        var validator = CreateValidator(editContext);
        var field = editContext.Field(nameof(RequiredModel.Name));
        editContext.NotifyFieldChanged(field);

        var msgs = editContext.GetValidationMessages(field).ToList();
        Assert.NotEmpty(msgs);
        Assert.Contains("Validation_Required", msgs[0]);
    }

    [Fact]
    public void ValidateField_Required_ValueSet_ClearsMessages()
    {
        var model = new RequiredModel { Name = string.Empty };
        var editContext = new EditContext(model);

        CreateValidator(editContext);

        // First trigger a validation error
        var field = editContext.Field(nameof(RequiredModel.Name));
        editContext.NotifyFieldChanged(field);
        Assert.NotEmpty(editContext.GetValidationMessages(field));

        // Now set a value and re-trigger
        model.Name = "Bob";
        editContext.NotifyFieldChanged(field);
        Assert.Empty(editContext.GetValidationMessages(field));
    }

    [Fact]
    public void ValidateField_StringLength_TooShort_AddsMessage()
    {
        var model = new StringLenModel { Tag = "x" }; // min is 2
        var editContext = new EditContext(model);

        CreateValidator(editContext);
        var field = editContext.Field(nameof(StringLenModel.Tag));
        editContext.NotifyFieldChanged(field);

        var msgs = editContext.GetValidationMessages(field).ToList();
        Assert.NotEmpty(msgs);
        Assert.Contains("Validation_StringLength", msgs[0]);
    }

    [Fact]
    public void ValidateField_StringLength_TooLong_AddsMessage()
    {
        var model = new StringLenModel { Tag = new string('a', 20) }; // max is 10
        var editContext = new EditContext(model);

        CreateValidator(editContext);
        var field = editContext.Field(nameof(StringLenModel.Tag));
        editContext.NotifyFieldChanged(field);

        var msgs = editContext.GetValidationMessages(field).ToList();
        Assert.NotEmpty(msgs);
    }

    [Fact]
    public void ValidateField_Range_OutOfRange_AddsMessage()
    {
        var model = new RangeModel { Age = 200 }; // max is 100
        var editContext = new EditContext(model);

        CreateValidator(editContext);
        var field = editContext.Field(nameof(RangeModel.Age));
        editContext.NotifyFieldChanged(field);

        var msgs = editContext.GetValidationMessages(field).ToList();
        Assert.NotEmpty(msgs);
        Assert.Contains("Validation_Range", msgs[0]);
    }

    [Fact]
    public void ValidateField_Email_Invalid_AddsMessage()
    {
        var model = new EmailModel { Email = "not-an-email" };
        var editContext = new EditContext(model);

        CreateValidator(editContext);
        var field = editContext.Field(nameof(EmailModel.Email));
        editContext.NotifyFieldChanged(field);

        var msgs = editContext.GetValidationMessages(field).ToList();
        Assert.NotEmpty(msgs);
        Assert.Contains("Validation_Email", msgs[0]);
    }

    [Fact]
    public void ValidateField_Url_Invalid_AddsMessage()
    {
        var model = new UrlModel { Website = "not-a-url" };
        var editContext = new EditContext(model);

        CreateValidator(editContext);
        var field = editContext.Field(nameof(UrlModel.Website));
        editContext.NotifyFieldChanged(field);

        var msgs = editContext.GetValidationMessages(field).ToList();
        Assert.NotEmpty(msgs);
        Assert.Contains("Validation_Url", msgs[0]);
    }

    [Fact]
    public void ValidateField_Email_Valid_NoMessage()
    {
        var model = new EmailModel { Email = "user@example.com" };
        var editContext = new EditContext(model);

        CreateValidator(editContext);
        var field = editContext.Field(nameof(EmailModel.Email));
        editContext.NotifyFieldChanged(field);

        var msgs = editContext.GetValidationMessages(field).ToList();
        Assert.Empty(msgs);
    }

    [Fact]
    public void Dispose_UnsubscribesFromEditContext()
    {
        var model = new RequiredModel();
        var editContext = new EditContext(model);

        var validator = CreateValidator(editContext);
        validator.Dispose();

        var field = editContext.Field(nameof(RequiredModel.Name));
        editContext.NotifyFieldChanged(field);

        Assert.Empty(editContext.GetValidationMessages(field));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static LocalizedDataAnnotationsValidator CreateValidator(EditContext editContext)
    {
        var instance = (LocalizedDataAnnotationsValidator)
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(LocalizedDataAnnotationsValidator));

        var locMock = Substitute.For<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>();

        locMock[Arg.Any<string>()].Returns(ci =>
        {
            var key = (string)ci[0];
            return new LocalizedString(key, key, true); // ResourceNotFound=true → return key as-is for LocalizeFieldName
        });
        locMock[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci =>
        {
            var key = (string)ci[0];
            var args = (object[])ci[1];
            return new LocalizedString(key, string.Format(key, args));
        });

        typeof(LocalizedDataAnnotationsValidator).GetProperty("L", Priv)!.SetValue(instance, locMock);

        // Wire up via OnInitialized - inject CurrentEditContext first
        typeof(LocalizedDataAnnotationsValidator)
            .GetProperty("CurrentEditContext", Priv)!
            .SetValue(instance, editContext);

        // Call OnInitialized to register the handlers
        var onInit = typeof(LocalizedDataAnnotationsValidator)
            .GetMethod("OnInitialized", BindingFlags.NonPublic | BindingFlags.Instance)!;
        onInit.Invoke(instance, []);

        return instance;
    }
}
