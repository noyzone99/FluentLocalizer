using System.Globalization;

namespace FluentLocalizer.Test;

public class TranslationOptionsTests
{
    [Fact]
    public void CreateArgumentSet_merges_defaults_and_runtime_arguments()
    {
        TranslationOptions options = new()
        {
            DefaultArguments = new Dictionary<string, object?>
            {
                ["name"] = "Ada",
                ["city"] = "London"
            }
        };

        var arguments = options.CreateArgumentSet(new Dictionary<string, object?>
        {
            ["name"] = "Grace"
        });

        Assert.Equal("Grace", arguments["name"]);
        Assert.Equal("London", arguments["city"]);
        Assert.Equal(2, arguments.Count);
    }

    [Fact]
    public void CreateMissingKeyException_uses_custom_factory_when_available()
    {
        TranslationOptions options = new()
        {
            MissingKeyExceptionFactory = (key, culture) => new TranslationException(key, culture, $"custom {key}")
        };

        var exception = Assert.IsType<TranslationException>(options.CreateMissingKeyException("welcome", new CultureInfo("fr-FR")));

        Assert.Equal("custom welcome", exception.Message);
        Assert.Equal("welcome", exception.Key);
        Assert.Equal("fr-FR", exception.Culture?.Name);
    }

    [Fact]
    public void CreateFormattingException_uses_custom_factory_when_available()
    {
        TranslationOptions options = new()
        {
            FormattingErrorExceptionFactory = (key, culture) => new TranslationException(key, culture, $"formatting {key}")
        };

        var exception = Assert.IsType<TranslationException>(options.CreateFormattingException("welcome", new CultureInfo("it-IT"), new InvalidOperationException("boom")));

        Assert.Equal("formatting welcome", exception.Message);
        Assert.Equal("welcome", exception.Key);
        Assert.Equal("it-IT", exception.Culture?.Name);
    }

    [Fact]
    public void Replacing_option_references_during_resolution_does_not_interrupt_readers()
    {
        var options = new TranslationOptions();
        var arguments = new Dictionary<string, object?> { ["name"] = "Ada" };
        var customException = new InvalidOperationException("Custom policy");
        Func<string, CultureInfo?, Exception> factory = (_, _) => customException;
        using var updating = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var writer = new Thread(() =>
        {
            updating.Set();
            while (!stop.IsCancellationRequested)
            {
                options.DefaultArguments = arguments;
                options.MissingKeyExceptionFactory = factory;
                options.FormattingErrorExceptionFactory = factory;
                Thread.SpinWait(20);
                options.DefaultArguments = null;
                options.MissingKeyExceptionFactory = null;
                options.FormattingErrorExceptionFactory = null;
                Thread.SpinWait(20);
            }
        });
        writer.Start();

        try
        {
            updating.Wait(TestContext.Current.CancellationToken);
            for (var index = 0; index < 100_000; index++)
            {
                options.CreateArgumentSet(null);
                options.CreateMissingKeyException("welcome", null);
                options.CreateFormattingException("welcome", null, null);
            }
        }
        finally
        {
            stop.Cancel();
            writer.Join();
        }
    }
}
