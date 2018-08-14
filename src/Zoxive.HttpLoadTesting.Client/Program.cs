﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Profiling;
using Zoxive.HttpLoadTesting.Client.Domain.Database;
using Zoxive.HttpLoadTesting.Client.Domain.HttpStatusResult.Factories;
using Zoxive.HttpLoadTesting.Client.Domain.HttpStatusResult.Repositories;
using Zoxive.HttpLoadTesting.Client.Domain.Iteration.Repositories;
using Zoxive.HttpLoadTesting.Client.Framework;
using Zoxive.HttpLoadTesting.Framework.Core;
using Zoxive.HttpLoadTesting.Framework.Model;

namespace Zoxive.HttpLoadTesting.Client
{
    public class Program
    {
        internal static Task StartAsync
        (
            ILoadTestExecution loadTestExecution,
            IReadOnlyList<ISchedule> schedules,
            IHttpStatusResultService httpStatusResultService,
            CancellationToken cancellationToken,
            ClientOptions options
        )
        {
            var host = new WebHostBuilder()
                .UseKestrel()
                .UseContentRoot(Directory.GetCurrentDirectory())
                .UseStartup<Startup>()
                .UseUrls("http://localhost:5000")
                .ConfigureServices(services =>
                {
                    ConfigureServices(services, httpStatusResultService, options.DatabaseFile);

                    services.AddSingleton<ICancelTokenReference>(new CancelTokenReference(cancellationToken));

                    services.AddSingleton(loadTestExecution);
                    services.AddSingleton(schedules);
                    services.AddSingleton(options);
                })
                .Build();

            var stopWebUi = new CancellationTokenSource();

            cancellationToken.Register(Cancel(stopWebUi));

            return host.RunAsync(stopWebUi.Token);
        }

        private static Action Cancel(CancellationTokenSource cancellationSource)
        {
            return () =>
            {
                Console.CancelKeyPress += ((sender, cancelEventArgs) =>
                {
                    cancellationSource.Cancel();
                    cancelEventArgs.Cancel = true;
                });

                var previous = Console.ForegroundColor;

                Console.ForegroundColor = ConsoleColor.Red;

                Console.WriteLine("CANCELING");
                Console.WriteLine("Press ctrl+c again to stop the webui");

                Console.ForegroundColor = previous;
            };
        }

        private static void ConfigureServices(IServiceCollection services, IHttpStatusResultService httpStatusResultService, string databaseFile)
        {
            services.AddScoped<IDbReader>(ioc => new Db(GetConnection(databaseFile)));

            services.AddSingleton(provider => httpStatusResultService ?? new HttpStatusResultNullService());
            services.AddSingleton<IIterationResultRepository>(CreateIterationResultRepository(databaseFile, out var dbWriter));
            services.AddSingleton<IDbWriter>(dbWriter);

            services.AddSingleton<IHttpStatusResultStatisticsFactory, HttpStatusResultStatisticsFactory>();
            services.AddScoped<IHttpStatusResultRepository, HttpStatusResultRepository>();

            services.AddSingleton<IHostedService, ExecuteTestsService>();

            services.AddSingleton<ISimpleTransaction, SimpleTransaction>();

            services.AddSingleton<ISaveIterationQueue, SaveIterationQueueQueue>();
            services.AddSingleton<IHostedService, SaveIterationResultBackgroundService>(ioc =>
            {
                var transaction = ioc.GetRequiredService<ISimpleTransaction>();
                var repo = ioc.GetRequiredService<IIterationResultRepository>();
                var queue = ioc.GetRequiredService<ISaveIterationQueue>();
                return new SaveIterationResultBackgroundService(transaction, repo, queue, "File");
            });

            Domain.GraphStats.ConfigureGraphStats.ConfigureServices(services);
        }

        public static IterationResultRepository CreateIterationResultRepository(string databaseFile, out IDbWriter fileDb)
        {
            var connection = GetConnection(databaseFile);
            fileDb = new Db(connection);

            var fileResultRepository = new IterationResultRepository(fileDb);
            DbInitializer.Initialize(fileDb);

            connection.Execute("PRAGMA journal_mode = MEMORY;");

            return fileResultRepository;
        }

        private static DbConnection GetConnection(string databaseFile)
        {
            var connection = new SqliteConnection($"Data Source={databaseFile};cache=shared");

            return new StackExchange.Profiling.Data.ProfiledDbConnection(connection, MiniProfiler.Current);
        }
    }

    public class CancelTokenReference : ICancelTokenReference
    {
        public CancellationToken Token { get; }

        public CancelTokenReference(CancellationToken token)
        {
            Token = token;
        }
    }

    public interface ICancelTokenReference
    {
        CancellationToken Token { get; }
    }

    public class Db : IDbWriter, IDbReader
    {
        public Db(IDbConnection connection)
        {
            Connection = connection;
        }

        public IDbConnection Connection { get; }
    }

    public interface IDbWriter
    {
        IDbConnection Connection { get; }
    }

    public interface IDbReader
    {
        IDbConnection Connection { get; }
    }
}
