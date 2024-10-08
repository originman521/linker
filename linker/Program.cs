﻿using linker.libs;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using linker.startup;
using linker.config;

namespace linker
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Run(args);
            await Helper.Await().ConfigureAwait(false);
        }

        public static void Run(string[] args)
        {
            Init();

            //初始化配置文件
            FileConfig config = new FileConfig();

            LoggerHelper.Instance.Warning($"current version : {config.Data.Version}");
            LoggerHelper.Instance.Warning($"linker env is docker : {Environment.GetEnvironmentVariable("SNLTTY_LINKER_IS_DOCKER")}");
            LoggerHelper.Instance.Warning($"linker env os : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            StartupTransfer.Init(config, assemblies);

            //依赖注入
            ServiceProvider serviceProvider = null;
            ServiceCollection serviceCollection = new ServiceCollection();
            //注入
            serviceCollection.AddSingleton((e) => serviceProvider);
            serviceCollection.AddSingleton((a) => config);
            StartupTransfer.Add(serviceCollection, config, assemblies);

            //运行
            serviceProvider = serviceCollection.BuildServiceProvider();
            StartupTransfer.Use(serviceProvider, config, assemblies);

            GCHelper.FlushMemory();
        }

        private static void Init()
        {
            //全局异常
            AppDomain.CurrentDomain.UnhandledException += (a, b) =>
            {
                LoggerHelper.Instance.Error(b.ExceptionObject + "");
            };
            //线程数
            ThreadPool.SetMinThreads(1024, 1024);
            ThreadPool.SetMaxThreads(65535, 65535);

            //日志输出
            LoggerConsole();
        }

        private static void LoggerConsole()
        {
            if (Directory.Exists("logs") == false)
            {
                Directory.CreateDirectory("logs");
            }
            LoggerHelper.Instance.OnLogger += (model) =>
            {
                ConsoleColor currentForeColor = Console.ForegroundColor;
                switch (model.Type)
                {
                    case LoggerTypes.DEBUG:
                        Console.ForegroundColor = ConsoleColor.Blue;
                        break;
                    case LoggerTypes.INFO:
                        Console.ForegroundColor = ConsoleColor.White;
                        break;
                    case LoggerTypes.WARNING:
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        break;
                    case LoggerTypes.ERROR:
                        Console.ForegroundColor = ConsoleColor.Red;
                        break;
                    default:
                        break;
                }
                string line = $"[{model.Type,-7}][{model.Time:yyyy-MM-dd HH:mm:ss}]:{model.Content}";
                Console.WriteLine(line);
                Console.ForegroundColor = currentForeColor;
                try
                {
                    using StreamWriter sw = File.AppendText(Path.Combine("logs", $"{DateTime.Now:yyyy-MM-dd}.log"));
                    sw.WriteLine(line);
                    sw.Flush();
                    sw.Close();
                    sw.Dispose();
                }
                catch (Exception)
                {
                }
            };
            Task.Run(async () =>
            {
                while (true)
                {
                    string[] files = Directory.GetFiles("logs").OrderBy(c => c).ToArray();
                    for (int i = 0; i < files.Length - 7; i++)
                    {
                        try
                        {
                            File.Delete(files[i]);
                        }
                        catch (Exception)
                        {
                        }
                    }
                    await Task.Delay(60 * 1000);
                }
            });
        }

    }

}