// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#if NET9_0_OR_GREATER
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Test;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Microsoft.Extensions.Diagnostics.Buffering.Test;

public class GlobalBufferLoggerBuilderExtensionsTests
{
    [Fact]
    public void WithLogLevel_RegistersInDI()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(builder =>
        {
            builder.AddGlobalBuffer(LogLevel.Warning);
        });

        ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
        var bufferManager = serviceProvider.GetService<GlobalLogBuffer>();

        Assert.NotNull(bufferManager);
        Assert.IsAssignableFrom<GlobalLogBufferManager>(bufferManager);
    }

    [Fact]
    public void WhenArgumentNull_Throws()
    {
        ILoggingBuilder? builder = null;
        IConfiguration? configuration = null;

        Assert.Throws<ArgumentNullException>(() => builder!.AddGlobalBuffer(LogLevel.Warning));
        Assert.Throws<ArgumentNullException>(() => builder!.AddGlobalBuffer(configuration!));
    }

    [Fact]
    public void WithConfiguration_RegistersInDI()
    {
        List<LogBufferingFilterRule> expectedData =
        [
            new ("Program.MyLogger",  LogLevel.Information, 1, "number one", [new("region", "westus2"), new ("priority", 1)]),
            new (logLevel: LogLevel.Information),
        ];
        ConfigurationBuilder configBuilder = new ConfigurationBuilder();
        configBuilder.AddJsonFile("appsettings.json");
        IConfigurationRoot configuration = configBuilder.Build();
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(builder =>
        {
            builder.AddGlobalBuffer(configuration);
            builder.Services.Configure<GlobalLogBufferingOptions>(options =>
            {
                options.MaxLogRecordSizeInBytes = 33;
            });
        });
        ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
        var options = serviceProvider.GetService<IOptionsMonitor<GlobalLogBufferingOptions>>();
        Assert.NotNull(options);
        Assert.NotNull(options.CurrentValue);
        Assert.Equal(33, options.CurrentValue.MaxLogRecordSizeInBytes); // value comes from the Configure<GlobalLogBufferingOptions>()  call
        Assert.Equal(1000, options.CurrentValue.MaxBufferSizeInBytes); // value comes from appsettings.json
        Assert.Equal(TimeSpan.FromSeconds(30), options.CurrentValue.AutoFlushDuration); // value comes from default
        Assert.Equivalent(expectedData, options.CurrentValue.Rules);
    }

    [Fact]
    public void WhenConfigUpdated_PicksUpConfigChanges()
    {
        List<LogBufferingFilterRule> initialData =
        [
            new(categoryName: "Program.MyLogger", logLevel: LogLevel.Information, eventId: 1, eventName: "number one"),
            new(logLevel : LogLevel.Information),
        ];
        List<LogBufferingFilterRule> updatedData =
        [
            new(logLevel: LogLevel.Information),
        ];
        string jsonConfig =
            @"
{
    ""GlobalLogBuffering"": {
     ""Rules"": [
       {
         ""CategoryName"": ""Program.MyLogger"",
         ""LogLevel"": ""Information"",
         ""EventId"": 1,
         ""EventName"": ""number one"",
       },
       {
         ""LogLevel"": ""Information"",
       },
     ]
    }
}
";

        using ConfigurationRoot config = TestConfiguration.Create(() => jsonConfig);

        using ExtendedLoggerTests.Provider provider = new ExtendedLoggerTests.Provider();
        using ILoggerFactory factory = Utils.CreateLoggerFactory(
            builder =>
            {
                builder.AddProvider(provider);
                builder.AddGlobalBuffer(config);
            });
        ILogger logger = factory.CreateLogger("Program.MyLogger");
        Utils.DisposingLoggerFactory dlf = (Utils.DisposingLoggerFactory)factory;
        var bufferManager = dlf.ServiceProvider.GetRequiredService<GlobalLogBuffer>() as GlobalLogBufferManager;

        IOptionsMonitor<GlobalLogBufferingOptions>? options = dlf.ServiceProvider.GetService<IOptionsMonitor<GlobalLogBufferingOptions>>();
        Assert.NotNull(options);
        Assert.NotNull(options.CurrentValue);
        Assert.Equivalent(initialData, options.CurrentValue.Rules);

        // this is just to trigger creating an internal buffer:
        logger.LogInformation(new EventId(1, "number one"), null);

        jsonConfig =
@"
{
    ""GlobalLogBuffering"": {
     ""Rules"": [
       {
         ""LogLevel"": ""Information"",
       },
     ]
    }
}
";
        config.Reload();

        Assert.NotNull(bufferManager);
        Assert.NotEmpty(bufferManager.Buffers);
        foreach (GlobalBuffer buffer in bufferManager.Buffers.Values)
        {
            Assert.Equivalent(updatedData, buffer.LastKnownGoodFilterRules, strict: true);
        }
    }

    [Fact]
    public void WhenConfigUpdated_AppliesNewRulesToSubsequentLogs()
    {
        string jsonConfig =
            @"
{
    ""GlobalLogBuffering"": {
     ""Rules"": [
       {
         ""LogLevel"": ""Information"",
       },
     ]
    }
}
";

        using ConfigurationRoot config = TestConfiguration.Create(() => jsonConfig);
        using ILoggerFactory factory = Utils.CreateLoggerFactory(
            builder =>
            {
                builder.AddFakeLogging();
                builder.AddGlobalBuffer(config);
            });
        Utils.DisposingLoggerFactory dlf = (Utils.DisposingLoggerFactory)factory;
        FakeLogCollector collector = dlf.ServiceProvider.GetFakeLogCollector();
        ILogger logger = factory.CreateLogger("Program.MyLogger");

        logger.LogInformation("buffered");
        Assert.Equal(0, collector.Count);

        jsonConfig =
@"
{
    ""GlobalLogBuffering"": {
     ""Rules"": [
       {
         ""LogLevel"": ""Debug"",
       },
     ]
    }
}
";
        config.Reload();

        logger.LogInformation("not buffered");
        Assert.Equal(["not buffered"], collector.GetSnapshot().Select(record => record.Message));
    }

    [Fact]
    public void WhenConfigUpdatedWhileSelectingRule_DoesNotThrow()
    {
        string jsonConfig =
            @"
{
    ""GlobalLogBuffering"": {
     ""Rules"": [
       {
         ""LogLevel"": ""Information"",
         ""Attributes"": [
           {
             ""key"": ""region"",
             ""value"": ""westus2""
           }
         ]
       },
       {
         ""LogLevel"": ""Information"",
       },
     ]
    }
}
";

        using ConfigurationRoot config = TestConfiguration.Create(() => jsonConfig);
        using ILoggerFactory factory = Utils.CreateLoggerFactory(
            builder =>
            {
                builder.AddFakeLogging();
                builder.AddGlobalBuffer(config);
            });
        Utils.DisposingLoggerFactory dlf = (Utils.DisposingLoggerFactory)factory;
        FakeLogCollector collector = dlf.ServiceProvider.GetFakeLogCollector();
        ILogger logger = factory.CreateLogger("Program.MyLogger");

        // the options are reloaded while the rules are being matched against the log record attributes:
        var state = new CallbackState(() =>
        {
            jsonConfig =
@"
{
    ""GlobalLogBuffering"": {
     ""Rules"": [
       {
         ""LogLevel"": ""Warning"",
       },
     ]
    }
}
";
            config.Reload();
        });

        Exception? exception = Record.Exception(() => logger.Log(LogLevel.Information, 0, state, null, (_, _) => "message"));

        // the record is buffered by the attribute-less rule which was in effect when logging started:
        Assert.Null(exception);
        Assert.Equal(0, collector.Count);

        dlf.ServiceProvider.GetRequiredService<GlobalLogBuffer>().Flush();

        Assert.Equal(["message"], collector.GetSnapshot().Select(record => record.Message));
    }

    [Fact]
    public void CategorySpecificRule_AppliesAfterOtherCategoryLoggedAtSameLevel()
    {
        using ILoggerFactory factory = Utils.CreateLoggerFactory(
            builder =>
            {
                builder.AddFakeLogging();
                builder.AddGlobalBuffer(options =>
                {
                    options.AutoFlushDuration = TimeSpan.Zero;
                    options.Rules.Add(new LogBufferingFilterRule(categoryName: "Program.BufferedLogger"));
                });
            });
        Utils.DisposingLoggerFactory dlf = (Utils.DisposingLoggerFactory)factory;
        FakeLogCollector collector = dlf.ServiceProvider.GetFakeLogCollector();

        factory.CreateLogger("Program.OtherLogger").LogInformation("not buffered");
        factory.CreateLogger("Program.BufferedLogger").LogInformation("buffered");

        Assert.Equal(["not buffered"], collector.GetSnapshot().Select(record => record.Message));

        dlf.ServiceProvider.GetRequiredService<GlobalLogBuffer>().Flush();

        Assert.Equal(["not buffered", "buffered"], collector.GetSnapshot().Select(record => record.Message));
    }

    [Fact]
    public void CategorySpecificRule_DoesNotApplyToOtherCategoryLoggedAtSameLevel()
    {
        using ILoggerFactory factory = Utils.CreateLoggerFactory(
            builder =>
            {
                builder.AddFakeLogging();
                builder.AddGlobalBuffer(options =>
                {
                    options.AutoFlushDuration = TimeSpan.Zero;
                    options.Rules.Add(new LogBufferingFilterRule(categoryName: "Program.BufferedLogger"));
                });
            });
        Utils.DisposingLoggerFactory dlf = (Utils.DisposingLoggerFactory)factory;
        FakeLogCollector collector = dlf.ServiceProvider.GetFakeLogCollector();

        factory.CreateLogger("Program.BufferedLogger").LogInformation("buffered");
        factory.CreateLogger("Program.OtherLogger").LogInformation("not buffered");

        Assert.Equal(["not buffered"], collector.GetSnapshot().Select(record => record.Message));
    }

    private sealed class CallbackState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private Action? _onFirstRead;

        public CallbackState(Action onFirstRead)
        {
            _onFirstRead = onFirstRead;
        }

        public int Count => 1;

        public KeyValuePair<string, object?> this[int index]
        {
            get
            {
                Action? onFirstRead = _onFirstRead;
                _onFirstRead = null;
                onFirstRead?.Invoke();

                return new("region", "eastus");
            }
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            yield return this[0];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
#endif
