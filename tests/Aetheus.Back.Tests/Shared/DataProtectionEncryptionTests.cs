// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aetheus.Back.Tests;

/// <summary>R-1: the Data Protection key ring is encrypted wherever it is persisted. A host that starts without the
/// container entrypoint (a local run, a binary install) used to persist its first key "in unencrypted form" and log
/// <c>XmlKeyManager</c> warning 35 at every start.</summary>
public sealed class DataProtectionEncryptionTests
{
    [Fact]
    public void AHostWithoutAnExistingCertificate_CreatesItAndEncryptsTheKeyRingWithoutWarning()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aetheus-dp-{Guid.NewGuid():N}");
        try
        {
            var warnings = new List<string>();
            var builder = WebApplication.CreateBuilder();
            builder.Configuration["DataProtection:KeyPath"] = directory;
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new WarningCollector(warnings));
            builder.AddAetheusDataProtection();
            using var app = builder.Build();

            var protectedText = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("secret");

            Assert.True(File.Exists(Path.Combine(directory, DataProtectionCertificate.FileName)), "The certificate is created in the key directory.");
            var keyFile = Assert.Single(Directory.GetFiles(directory, "key-*.xml"));
            var xml = File.ReadAllText(keyFile);
            Assert.Contains("encryptedSecret", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("<masterKey", xml, StringComparison.Ordinal);
            Assert.Equal("secret", app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedText));
            Assert.Empty(warnings);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheCertificate_IsCreatedOnce_AndEveryHostStartingTogetherLoadsTheSameOne()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aetheus-dp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var thumbprints = Enumerable.Range(0, 8).AsParallel().WithDegreeOfParallelism(8)
                .Select(_ => { using var certificate = DataProtectionCertificate.LoadOrCreate(directory); return certificate.Thumbprint; })
                .ToList();

            Assert.Single(thumbprints.Distinct());
            Assert.Equal([DataProtectionCertificate.FileName], Directory.GetFiles(directory).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class WarningCollector(List<string> lines) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) lock (lines) lines.Add(formatter(state, exception));
        }

        public void Dispose()
        {
        }
    }
}
