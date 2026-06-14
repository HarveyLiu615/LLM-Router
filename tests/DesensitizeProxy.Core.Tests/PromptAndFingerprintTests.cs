using DesensitizeProxy.Core.Llm;
using DesensitizeProxy.Core.Models;

namespace DesensitizeProxy.Core.Tests;

public sealed class PromptAndFingerprintTests
{
    [Fact]
    public void PromptLoader_UsesConfiguredPromptAndRefreshesOnOptionsChange()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            var first = Path.Combine(temp.FullName, "first.md");
            var second = Path.Combine(temp.FullName, "second.md");
            File.WriteAllText(first, "first prompt");
            File.WriteAllText(second, "second prompt");
            var config = new PrivacyConfig { PromptPath = first };
            var options = new TestOptionsMonitor<PrivacyConfig>(config);
            var loader = new PromptLoader(new TestHostEnvironment(), options);

            Assert.Equal("first prompt", loader.Load());

            options.Set(new PrivacyConfig { PromptPath = second });

            Assert.Equal("second prompt", loader.Load());
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void ConfigFingerprint_ChangesWhenPromptOrRedactionConfigChanges()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            var prompt = Path.Combine(temp.FullName, "prompt.md");
            File.WriteAllText(prompt, "first prompt");
            var config = new PrivacyConfig { PromptPath = prompt };
            var options = new TestOptionsMonitor<PrivacyConfig>(config);
            var loader = new PromptLoader(new TestHostEnvironment(), options);
            var fingerprint = new ConfigFingerprintProvider(options, loader);
            var first = fingerprint.Current;

            File.WriteAllText(prompt, "second prompt");
            options.Set(config);
            var second = fingerprint.Current;

            var updated = new PrivacyConfig { PromptPath = prompt };
            updated.Redaction.Email = false;
            options.Set(updated);
            var third = fingerprint.Current;

            Assert.NotEqual(first, second);
            Assert.NotEqual(second, third);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}
