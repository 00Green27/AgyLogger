namespace AgyLogger.Cli.Tests.Cli;

using System;
using System.CommandLine;
using System.IO;
using System.Security.Cryptography.X509Certificates;

using AgyLogger.Cli.Cli;
using AgyLogger.Cli.Services.Proxy;

using Xunit;

public sealed class CaCommandHandlerTests : IDisposable
{
    private readonly string _tempCaDir;
    private const string IsolatedStore = "AgyLoggerTestStore";

    public CaCommandHandlerTests()
    {
        _tempCaDir = Path.Combine(Path.GetTempPath(), "agy_ca_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempCaDir);
    }

    public void Dispose()
    {
        // Cleanup store certificates
        try
        {
            using var store = new X509Store(IsolatedStore, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            var matches = store.Certificates.Find(X509FindType.FindBySubjectName, CertificateAuthority.DefaultCaCommonName, false);
            foreach (var cert in matches)
            {
                store.Remove(cert);
                cert.Dispose();
            }
        }
        catch { }

        // Cleanup temporary directory
        if (Directory.Exists(_tempCaDir))
        {
            try { Directory.Delete(_tempCaDir, true); } catch { }
        }
    }

    [Fact]
    public void CaCommand_BuildsValidCommandHierarchy()
    {
        var command = CaCommandHandler.CreateCommand();

        Assert.Equal("ca", command.Name);
        Assert.Contains(command.Subcommands, c => c.Name == "status");
        Assert.Contains(command.Subcommands, c => c.Name == "trust");
        Assert.Contains(command.Subcommands, c => c.Name == "untrust");
        Assert.Contains(command.Subcommands, c => c.Name == "export");
    }

    [Fact]
    public void CaCommand_Status_ReportsNotInitializedWhenNoCa()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = CaCommandHandler.ExecuteStatus(_tempCaDir, IsolatedStore, stdout, stderr);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("Status:          Not Initialized", output);
        Assert.Contains("PFX File:        Not found", output);
    }

    [Fact]
    public void CaCommand_Status_ReportsInitializedWhenCaExists()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        // Initialize CA in temp directory
        using (var ca = CertificateAuthority.GetOrCreateDefault(_tempCaDir, IsolatedStore, StoreLocation.CurrentUser))
        {
            Assert.NotNull(ca);
        }

        var exitCode = CaCommandHandler.ExecuteStatus(_tempCaDir, IsolatedStore, stdout, stderr);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("Status:          Initialized", output);
        Assert.Contains(CertificateAuthority.DefaultCaCommonName, output);
        Assert.Contains("Thumbprint:", output);
    }

    [Fact]
    public void CaCommand_Trust_InstallsToIsolatedStoreAndIsIdempotent()
    {
        using var stdout1 = new StringWriter();
        using var stderr1 = new StringWriter();

        var exitCode1 = CaCommandHandler.ExecuteTrust(_tempCaDir, IsolatedStore, stdout1, stderr1);
        Assert.Equal(0, exitCode1);
        Assert.Contains("Successfully installed", stdout1.ToString());

        using var stdout2 = new StringWriter();
        using var stderr2 = new StringWriter();

        var exitCode2 = CaCommandHandler.ExecuteTrust(_tempCaDir, IsolatedStore, stdout2, stderr2);
        Assert.Equal(0, exitCode2);
        Assert.Contains("already installed and trusted", stdout2.ToString());
    }

    [Fact]
    public void CaCommand_Untrust_RemovesFromIsolatedStore()
    {
        // First install to isolated store
        CaCommandHandler.ExecuteTrust(_tempCaDir, IsolatedStore);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = CaCommandHandler.ExecuteUntrust(_tempCaDir, IsolatedStore, stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Successfully removed", stdout.ToString());
    }

    [Fact]
    public void CaCommand_Export_DefaultAndCustomPaths()
    {
        var exportPath = Path.Combine(_tempCaDir, "custom_export.crt");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = CaCommandHandler.ExecuteExport(exportPath, _tempCaDir, stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(exportPath));

        var pemText = File.ReadAllText(exportPath);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pemText);
        Assert.Contains("-----END CERTIFICATE-----", pemText);
    }

    [Fact]
    public void CaCommand_CommandLineParsing_ValidatesOptionsAndAliases()
    {
        var root = CommandLineBuilder.BuildRootCommand();

        var parseResult1 = root.Parse($"ca export --output \"{_tempCaDir}/test.crt\"");
        Assert.Empty(parseResult1.Errors);

        var parseResult2 = root.Parse($"ca export -o \"{_tempCaDir}/test.crt\"");
        Assert.Empty(parseResult2.Errors);

        var parseResult3 = root.Parse($"ca export --out \"{_tempCaDir}/test.crt\"");
        Assert.Empty(parseResult3.Errors);

        var parseResult4 = root.Parse($"ca export \"{_tempCaDir}/test.crt\"");
        Assert.Empty(parseResult4.Errors);
    }
}