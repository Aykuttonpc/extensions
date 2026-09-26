// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#if NET9_0_OR_GREATER
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.Buffering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Buffering;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Test;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;
using PerRequestLogBuffer = Microsoft.Extensions.Diagnostics.Buffering.PerRequestLogBuffer;

namespace Microsoft.AspNetCore.Diagnostics.Logging.Test;

public class PerIncomingRequestLoggingBuilderExtensionsTests
{
    [Fact]
    public void WhenLogLevelProvided_RegistersInDI()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(builder =>
        {
            builder.AddPerIncomingRequestBuffer(LogLevel.Warning);
        });

        ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
        var buffer = serviceProvider.GetService<PerRequestLogBuffer>();

        Assert.NotNull(buffer);
        Assert.IsAssignableFrom<PerRequestLogBufferManager>(buffer);
    }

    [Fact]
    public void WhenArgumentNull_Throws()
    {
        ILoggingBuilder? builder = null;
        IConfiguration? configuration = null;

        Assert.Throws<ArgumentNullException>(() => builder!.AddPerIncomingRequestBuffer(LogLevel.Warning));
        Assert.Throws<ArgumentNullException>(() => builder!.AddPerIncomingRequestBuffer(configuration!));
    }

    [Fact]
    public void WhenIConfigurationProvided_RegistersInDI()
    {
        List<LogBufferingFilterRule> expectedData =
        [
            new(categoryName: "Program.MyLogger", logLevel: LogLevel.Information, eventId: 1, eventName: "number one"),
            new(logLevel: LogLevel.Information),
        ];
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddJsonFile("appsettings.json");
        IConfigurationRoot configuration = configBuilder.Build();
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(b => b.AddPerIncomingRequestBuffer(configuration));
        ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
        var options = serviceProvider.GetService<IOptionsMonitor<PerRequestLogBufferingOptions>>();
        Assert.NotNull(options);
        Assert.NotNull(options.CurrentValue);
        Assert.Equivalent(expectedData, options.CurrentValue.Rules);
    }

    [Fact]
    public void WhenConfigurationActionProvided_RegistersInDI()
    {
        List<LogBufferingFilterRule> expectedData =
        [
            new(categoryName: "Program.MyLogger", logLevel: LogLevel.Information, eventId: 1, eventName: "number one"),
            new(logLevel: LogLevel.Information),
        ];
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(b => b.AddPerIncomingRequestBuffer(options =>
        {
            options.Rules.Add(new LogBufferingFilterRule(categoryName: "Program.MyLogger",
                logLevel: LogLevel.Information, eventId: 1, eventName: "number one"));
            options.Rules.Add(new LogBufferingFilterRule(logLevel: LogLevel.Information));
        }));
        ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
        var options = serviceProvider.GetService<IOptionsMonitor<PerRequestLogBufferingOptions>>();
        Assert.NotNull(options);
        Assert.NotNull(options.CurrentValue);
        Assert.Equivalent(expectedData, options.CurrentValue.Rules);
    }

    [Fact]
    public async Task WhenConfigUpdated_PicksUpConfigChanges()
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
    ""PerIncomingRequestLogBuffering"": {
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
        using IHost host = await FakeHost.CreateBuilder()
            .ConfigureWebHost(builder => builder
                .UseTestServer()
                .ConfigureServices(x => x.AddRouting())
                .ConfigureLogging(loggingBuilder => loggingBuilder
                    .AddPerIncomingRequestBuffer(config))
                .Configure(app => app.UseRouting()))
            .StartAsync();

        IOptionsMonitor<PerRequestLogBufferingOptions>? options = host.Services.GetService<IOptionsMonitor<PerRequestLogBufferingOptions>>();
        Assert.NotNull(options);
        Assert.NotNull(options.CurrentValue);
        Assert.Equivalent(initialData, options.CurrentValue.Rules);

        jsonConfig =
@"
{
    ""PerIncomingRequestLogBuffering"": {
     ""Rules"": [
       {
         ""LogLevel"": ""Information"",
       },
     ]
    }
}
";
        config.Reload();

        var bufferManager = host.Services.GetRequiredService<PerRequestLogBuffer>() as PerRequestLogBufferManager;
        Assert.NotNull(bufferManager);
        Assert.Equivalent(updatedData, bufferManager.Options.CurrentValue.Rules, strict: true);

        await host.StopAsync();
    }

    [Fact]
    public void CategorySpecificRule_AppliesAfterOtherCategoryLoggedAtSameLevel()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(builder =>
        {
            builder.AddFakeLogging();
            builder.AddPerIncomingRequestBuffer(options =>
            {
                options.AutoFlushDuration = TimeSpan.Zero;
                options.Rules.Add(new LogBufferingFilterRule(categoryName: "Program.BufferedLogger"));
            });
        });
        using ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
        FakeLogCollector collector = serviceProvider.GetFakeLogCollector();
        ILoggerFactory loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        using IServiceScope request = serviceProvider.CreateScope();
        serviceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { RequestServices = request.ServiceProvider };

        loggerFactory.CreateLogger("Program.OtherLogger").LogInformation("not buffered");
        loggerFactory.CreateLogger("Program.BufferedLogger").LogInformation("buffered");

        Assert.Equal(["not buffered"], collector.GetSnapshot().Select(record => record.Message));
    }

    [Fact]
    public void CategorySpecificRule_DoesNotApplyToOtherCategoryLoggedAtSameLevel()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(builder =>
        {
            builder.AddFakeLogging();
            builder.AddPerIncomingRequestBuffer(options =>
            {
                options.AutoFlushDuration = TimeSpan.Zero;
                options.Rules.Add(new LogBufferingFilterRule(categoryName: "Program.BufferedLogger"));
            });
        });
        using ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
        FakeLogCollector collector = serviceProvider.GetFakeLogCollector();
        ILoggerFactory loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        using IServiceScope request = serviceProvider.CreateScope();
        serviceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { RequestServices = request.ServiceProvider };

        loggerFactory.CreateLogger("Program.BufferedLogger").LogInformation("buffered");
        loggerFactory.CreateLogger("Program.OtherLogger").LogInformation("not buffered");

        Assert.Equal(["not buffered"], collector.GetSnapshot().Select(record => record.Message));
    }

    [Fact]
    public void WhenConfigUpdated_NextRequestUsesNewRules()
    {
        string jsonConfig =
            @"
{
    ""PerIncomingRequestLogBuffering"": {
     ""Rules"": [
       {
         ""LogLevel"": ""Information"",
       },
     ]
    }
}
";

        using ConfigurationRoot config = TestConfiguration.Create(() => jsonConfig);
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(builder =>
        {
            builder.AddFakeLogging();
            builder.AddPerIncomingRequestBuffer(config);
        });
        using ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
        FakeLogCollector collector = serviceProvider.GetFakeLogCollector();
        IHttpContextAccessor httpContextAccessor = serviceProvider.GetRequiredService<IHttpContextAccessor>();
        ILogger logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Program.MyLogger");

        using (IServiceScope request = serviceProvider.CreateScope())
        {
            httpContextAccessor.HttpContext = new DefaultHttpContext { RequestServices = request.ServiceProvider };
            logger.LogInformation("buffered");
        }

        Assert.Equal(0, collector.Count);

        jsonConfig =
@"
{
    ""PerIncomingRequestLogBuffering"": {
     ""Rules"": [
       {
         ""LogLevel"": ""Debug"",
       },
     ]
    }
}
";
        config.Reload();

        using (IServiceScope request = serviceProvider.CreateScope())
        {
            httpContextAccessor.HttpContext = new DefaultHttpContext { RequestServices = request.ServiceProvider };
            logger.LogInformation("not buffered");
        }

        Assert.Equal(["not buffered"], collector.GetSnapshot().Select(record => record.Message));
    }

    [Fact]
    public void PerRequestRules_AreNotAffectedByGlobalRules()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(builder =>
        {
            builder.AddFakeLogging();
            builder.AddPerIncomingRequestBuffer(options =>
            {
                options.AutoFlushDuration = TimeSpan.Zero;
                options.Rules.Add(new LogBufferingFilterRule(logLevel: LogLevel.Information));
            });
            builder.Services.Configure<GlobalLogBufferingOptions>(options =>
            {
                options.AutoFlushDuration = TimeSpan.Zero;
                options.Rules = [new LogBufferingFilterRule(logLevel: LogLevel.Debug)];
            });
        });
        using ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
        FakeLogCollector collector = serviceProvider.GetFakeLogCollector();
        ILogger logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Program.MyLogger");

        // outside of an incoming request the global rules apply:
        logger.LogInformation("not buffered");

        using IServiceScope request = serviceProvider.CreateScope();
        serviceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { RequestServices = request.ServiceProvider };

        // within an incoming request the per-request rules apply:
        logger.LogInformation("buffered");

        Assert.Equal(["not buffered"], collector.GetSnapshot().Select(record => record.Message));
    }

    [Fact]
    public void WhenOtherRequestEndsWhileSelectingRule_DoesNotThrow()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(builder =>
        {
            builder.AddFakeLogging();
            builder.AddPerIncomingRequestBuffer(options =>
            {
                options.Rules.Add(new LogBufferingFilterRule(logLevel: LogLevel.Information, attributes: [new("region", "westus2")]));
                options.Rules.Add(new LogBufferingFilterRule(logLevel: LogLevel.Information));
            });
        });
        using ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
        FakeLogCollector collector = serviceProvider.GetFakeLogCollector();
        IHttpContextAccessor httpContextAccessor = serviceProvider.GetRequiredService<IHttpContextAccessor>();
        ILogger logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Program.MyLogger");
        using IServiceScope request = serviceProvider.CreateScope();
        using IServiceScope otherRequest = serviceProvider.CreateScope();

        httpContextAccessor.HttpContext = new DefaultHttpContext { RequestServices = otherRequest.ServiceProvider };
        logger.LogInformation("other request");

        // the other request ends while the rules are being matched against the log record attributes:
        httpContextAccessor.HttpContext = new DefaultHttpContext { RequestServices = request.ServiceProvider };
        var state = new CallbackState(otherRequest.Dispose);

        Exception? exception = Record.Exception(() => logger.Log(LogLevel.Information, 0, state, null, (_, _) => "request"));

        // the record is buffered by the attribute-less rule:
        Assert.Null(exception);
        Assert.Equal(0, collector.Count);

        serviceProvider.GetRequiredService<PerRequestLogBuffer>().Flush();

        Assert.Equal(["request"], collector.GetSnapshot().Select(record => record.Message));
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
