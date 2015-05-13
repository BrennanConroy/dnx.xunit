using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Framework.Runtime;
using Microsoft.Framework.TestAdapter;
using Xunit.Abstractions;
using VsTestCase = Microsoft.Framework.TestAdapter.Test;

namespace Xunit.Runner.Dnx
{
    public class Program
    {
#pragma warning disable 0649
        volatile bool cancel;
#pragma warning restore 0649
        bool failed;
        readonly ConcurrentDictionary<string, ExecutionSummary> completionMessages = new ConcurrentDictionary<string, ExecutionSummary>();

        private readonly IApplicationEnvironment _appEnv;
        private readonly IServiceProvider _services;

        public Program(IApplicationEnvironment appEnv, IServiceProvider services)
        {
            _appEnv = appEnv;
            _services = services;
        }

        [STAThread]
        public int Main(string[] args)
        {
            args = Enumerable.Repeat(_appEnv.ApplicationName + ".dll", 1).Concat(args).ToArray();

            try
            {
                if (args.Length == 0 || args.Any(arg => arg == "-?"))
                {
                    PrintHeader();
                    PrintUsage();
                    return 1;
                }

#if !DNXCORE50
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                Console.CancelKeyPress += (sender, e) =>
                {
                    if (!cancel)
                    {
                        Console.WriteLine("Canceling... (Press Ctrl+C again to terminate)");
                        cancel = true;
                        e.Cancel = true;
                    }
                };
#endif

                var defaultDirectory = Directory.GetCurrentDirectory();
                if (!defaultDirectory.EndsWith(new String(new[] { Path.DirectorySeparatorChar })))
                    defaultDirectory += Path.DirectorySeparatorChar;

                var commandLine = CommandLine.Parse(args);

#if !DNXCORE50
                if (commandLine.Debug)
                    Debugger.Launch();
#else
                if (commandLine.Debug)
                {
                    Console.WriteLine("Debug support is not available in DNX Core.");
                    return -1;
                }
#endif

                if (!commandLine.NoLogo)
                    PrintHeader();

                var failCount = RunProject(defaultDirectory, commandLine.Project, commandLine.Quiet, commandLine.TeamCity,
                                           commandLine.ParallelizeAssemblies, commandLine.ParallelizeTestCollections,
                                           commandLine.MaxParallelThreads, commandLine.DiagnosticMessages,
                                           commandLine.DesignTime, commandLine.List, commandLine.DesignTimeTestUniqueNames);

                if (commandLine.Wait)
                {
                    Console.WriteLine();

                    Console.Write("Press ENTER to continue...");
                    Console.ReadLine();

                    Console.WriteLine();
                }

                return failCount;
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine("error: {0}", ex.Message);
                return 1;
            }
            catch (BadImageFormatException ex)
            {
                Console.WriteLine("{0}", ex.Message);
                return 1;
            }
            finally
            {
                Console.ResetColor();
            }
        }

#if !DNXCORE50
        static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;

            if (ex != null)
                Console.WriteLine(ex.ToString());
            else
                Console.WriteLine("Error of unknown type thrown in application domain");

            Environment.Exit(1);
        }
#endif

        void PrintHeader()
        {
            var framework = _appEnv.RuntimeFramework;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("xUnit.net DNX test runner ({0}-bit {1} {2})", IntPtr.Size * 8, framework.Identifier, framework.Version);
            Console.WriteLine("Copyright (C) 2015 Outercurve Foundation.");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Gray;
        }

        static void PrintUsage()
        {
            Console.WriteLine("usage: xunit.runner.dnx <assemblyFile> [assemblyFile...] [options]");
            Console.WriteLine();
            Console.WriteLine("Valid options:");
            Console.WriteLine("  -parallel option       : set parallelization based on option");
            Console.WriteLine("                         :   none - turn off all parallelization");
            Console.WriteLine("                         :   collections - only parallelize collections");
            Console.WriteLine("                         :   assemblies - only parallelize assemblies");
            Console.WriteLine("                         :   all - parallelize collections and assemblies");
            Console.WriteLine("  -maxthreads count      : maximum thread count for collection parallelization");
            Console.WriteLine("                         :   default   - run with default (1 thread per CPU thread)");
            Console.WriteLine("                         :   unlimited - run with unbounded thread count");
            Console.WriteLine("                         :   (number)  - limit task thread pool size to 'count'");
            Console.WriteLine("  -noshadow              : do not shadow copy assemblies");
            Console.WriteLine("  -teamcity              : forces TeamCity mode (normally auto-detected)");
            Console.WriteLine("  -nologo                : do not show the copyright message");
            Console.WriteLine("  -quiet                 : do not show progress messages");
            Console.WriteLine("  -wait                  : wait for input after completion");
            Console.WriteLine("  -diagnostics           : enable diagnostics messages for all test assemblies");
#if !DNXCORE50
            Console.WriteLine("  -debug                 : launch the debugger to debug the tests");
#endif
            Console.WriteLine("  -trait \"name=value\"    : only run tests with matching name/value traits");
            Console.WriteLine("                         : if specified more than once, acts as an OR operation");
            Console.WriteLine("  -notrait \"name=value\"  : do not run tests with matching name/value traits");
            Console.WriteLine("                         : if specified more than once, acts as an AND operation");
            Console.WriteLine("  -method \"name\"         : run a given test method (should be fully specified;");
            Console.WriteLine("                         : i.e., 'MyNamespace.MyClass.MyTestMethod')");
            Console.WriteLine("                         : if specified more than once, acts as an OR operation");
            Console.WriteLine("  -class \"name\"          : run all methods in a given test class (should be fully");
            Console.WriteLine("                         : specified; i.e., 'MyNamespace.MyClass')");
            Console.WriteLine("                         : if specified more than once, acts as an OR operation");

            foreach (var transform in TransformFactory.AvailableTransforms)
                Console.WriteLine("  {0} : {1}",
                                  String.Format("-{0} <filename>", transform.CommandLine).PadRight(22).Substring(0, 22),
                                  transform.Description);
        }

        int RunProject(string defaultDirectory,
                       XunitProject project,
                       bool quiet,
                       bool teamcity,
                       bool? parallelizeAssemblies,
                       bool? parallelizeTestCollections,
                       int? maxThreadCount,
                       bool diagnosticMessages,
                       bool designTime,
                       bool list,
                       IReadOnlyList<string> designTimeFullyQualifiedNames)
        {
            XElement assembliesElement = null;
            var xmlTransformers = TransformFactory.GetXmlTransformers(project);
            var needsXml = xmlTransformers.Count > 0;
            var consoleLock = new object();

            if (!parallelizeAssemblies.HasValue)
                parallelizeAssemblies = project.All(assembly => assembly.Configuration.ParallelizeAssemblyOrDefault);

            if (needsXml)
                assembliesElement = new XElement("assemblies");

            var originalWorkingFolder = Directory.GetCurrentDirectory();

            using (AssemblyHelper.SubscribeResolve())
            {
                var clockTime = Stopwatch.StartNew();

                if (parallelizeAssemblies.GetValueOrDefault())
                {
                    var tasks = project.Assemblies.Select(assembly => TaskRun(() => ExecuteAssembly(consoleLock, defaultDirectory, assembly, quiet, needsXml, teamcity, parallelizeTestCollections, maxThreadCount, diagnosticMessages, project.Filters, designTime, list, designTimeFullyQualifiedNames)));
                    var results = Task.WhenAll(tasks).GetAwaiter().GetResult();
                    foreach (var assemblyElement in results.Where(result => result != null))
                        assembliesElement.Add(assemblyElement);
                }
                else
                {
                    foreach (var assembly in project.Assemblies)
                    {
                        var assemblyElement = ExecuteAssembly(consoleLock, defaultDirectory, assembly, quiet, needsXml, teamcity, parallelizeTestCollections, maxThreadCount, diagnosticMessages, project.Filters, designTime, list, designTimeFullyQualifiedNames);
                        if (assemblyElement != null)
                            assembliesElement.Add(assemblyElement);
                    }
                }

                clockTime.Stop();

                if (completionMessages.Count > 0)
                {
                    if (!quiet)
                    {
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.WriteLine();
                        Console.WriteLine("=== TEST EXECUTION SUMMARY ===");
                        Console.ForegroundColor = ConsoleColor.Gray;
                    }

                    var totalTestsRun = completionMessages.Values.Sum(summary => summary.Total);
                    var totalTestsFailed = completionMessages.Values.Sum(summary => summary.Failed);
                    var totalTestsSkipped = completionMessages.Values.Sum(summary => summary.Skipped);
                    var totalTime = completionMessages.Values.Sum(summary => summary.Time).ToString("0.000s");
                    var totalErrors = completionMessages.Values.Sum(summary => summary.Errors);
                    var longestAssemblyName = completionMessages.Keys.Max(key => key.Length);
                    var longestTotal = totalTestsRun.ToString().Length;
                    var longestFailed = totalTestsFailed.ToString().Length;
                    var longestSkipped = totalTestsSkipped.ToString().Length;
                    var longestTime = totalTime.Length;
                    var longestErrors = totalErrors.ToString().Length;

                    foreach (var message in completionMessages.OrderBy(m => m.Key))
                    {
                        if (message.Value.Total == 0)
                        {
                            Console.Write("   {0}  ", message.Key.PadRight(longestAssemblyName));
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine("Total: {0}", "0".PadLeft(longestTotal));
                            Console.ForegroundColor = ConsoleColor.Gray;
                        }
                        else
                            Console.WriteLine("   {0}  Total: {1}, Errors: {2}, Failed: {3}, Skipped: {4}, Time: {5}",
                                              message.Key.PadRight(longestAssemblyName),
                                              message.Value.Total.ToString().PadLeft(longestTotal),
                                              message.Value.Errors.ToString().PadLeft(longestErrors),
                                              message.Value.Failed.ToString().PadLeft(longestFailed),
                                              message.Value.Skipped.ToString().PadLeft(longestSkipped),
                                              message.Value.Time.ToString("0.000s").PadLeft(longestTime));

                    }

                    if (completionMessages.Count > 1)
                        Console.WriteLine("   {0}         {1}          {2}          {3}           {4}        {5}" + Environment.NewLine +
                                          "           {6} {7}          {8}          {9}           {10}        {11} ({12})",
                                          " ".PadRight(longestAssemblyName),
                                          "-".PadRight(longestTotal, '-'),
                                          "-".PadRight(longestErrors, '-'),
                                          "-".PadRight(longestFailed, '-'),
                                          "-".PadRight(longestSkipped, '-'),
                                          "-".PadRight(longestTime, '-'),
                                          "GRAND TOTAL:".PadLeft(longestAssemblyName),
                                          totalTestsRun,
                                          totalErrors,
                                          totalTestsFailed,
                                          totalTestsSkipped,
                                          totalTime,
                                          clockTime.Elapsed.TotalSeconds.ToString("0.000s"));
                }
            }

            Directory.SetCurrentDirectory(originalWorkingFolder);

            foreach (var transformer in xmlTransformers)
                transformer(assembliesElement);

            return failed ? 1 : completionMessages.Values.Sum(summary => summary.Failed);
        }

        TestMessageVisitor<ITestAssemblyFinished> CreateVisitor(object consoleLock, bool quiet, string defaultDirectory, XElement assemblyElement, bool teamCity)
        {
            if (teamCity)
                return new TeamCityVisitor(assemblyElement, () => cancel);

            return new StandardOutputVisitor(consoleLock, quiet, defaultDirectory, assemblyElement, () => cancel, completionMessages);
        }

        XElement ExecuteAssembly(object consoleLock,
                                 string defaultDirectory,
                                 XunitProjectAssembly assembly,
                                 bool quiet,
                                 bool needsXml,
                                 bool teamCity,
                                 bool? parallelizeTestCollections,
                                 int? maxThreadCount,
                                 bool diagnosticMessages,
                                 XunitFilters filters,
                                 bool designTime,
                                 bool listTestCases,
                                 IReadOnlyList<string> designTimeFullyQualifiedNames)
        {
            if (cancel)
                return null;

            var assemblyElement = needsXml ? new XElement("assembly") : null;

            try
            {
                if (diagnosticMessages)
                    assembly.Configuration.DiagnosticMessages = true;

                var discoveryOptions = TestFrameworkOptions.ForDiscovery(assembly.Configuration);
                var executionOptions = TestFrameworkOptions.ForExecution(assembly.Configuration);
                if (maxThreadCount.HasValue)
                    executionOptions.SetMaxParallelThreads(maxThreadCount);
                if (parallelizeTestCollections.HasValue)
                    executionOptions.SetDisableParallelization(!parallelizeTestCollections.GetValueOrDefault());

                var assemblyDisplayName = Path.GetFileNameWithoutExtension(assembly.AssemblyFilename);

                lock (consoleLock)
                {
                    if (assembly.Configuration.DiagnosticMessagesOrDefault)
                        Console.WriteLine("Discovering: {0} (method display = {1}, parallel test collections = {2}, max threads = {3})",
                                          assemblyDisplayName,
                                          discoveryOptions.GetMethodDisplayOrDefault(),
                                          !executionOptions.GetDisableParallelizationOrDefault(),
                                          executionOptions.GetMaxParallelThreadsOrDefault());
                    else if (!quiet)
                        Console.WriteLine("Discovering: {0}", assemblyDisplayName);
                }

                var diagnosticMessageVisitor = new DiagnosticMessageVisitor(consoleLock, assemblyDisplayName, assembly.Configuration.DiagnosticMessagesOrDefault);
                var sourceInformationProvider = new SourceInformationProviderAdapater(_services);

                using (var controller = new XunitFrontController(assembly.AssemblyFilename, assembly.ConfigFilename, assembly.ShadowCopy, diagnosticMessageSink: diagnosticMessageVisitor, sourceInformationProvider: sourceInformationProvider))
                using (var discoveryVisitor = new TestDiscoveryVisitor())
                {
                    var includeSourceInformation = designTime && listTestCases;

                    controller.Find(includeSourceInformation: includeSourceInformation, messageSink: discoveryVisitor, discoveryOptions: discoveryOptions);
                    discoveryVisitor.Finished.WaitOne();

                    IDictionary<ITestCase, VsTestCase> vsTestcases = null;
                    if (designTime)
                        vsTestcases = DesignTimeTestConverter.Convert(discoveryVisitor.TestCases);

                    if (!quiet)
                        lock (consoleLock)
                            Console.WriteLine("Discovered:  {0}", Path.GetFileNameWithoutExtension(assembly.AssemblyFilename));

                    if (listTestCases)
                    {
                        lock (consoleLock)
                        {
                            if (designTime)
                            {
                                var sink = (ITestDiscoverySink)_services.GetService(typeof(ITestDiscoverySink));

                                foreach (var testcase in vsTestcases.Values)
                                {
                                    if (sink != null)
                                        sink.SendTest(testcase);

                                    Console.WriteLine(testcase.FullyQualifiedName);
                                }
                            }
                            else
                            {
                                foreach (var testcase in discoveryVisitor.TestCases)
                                    Console.WriteLine(testcase.DisplayName);
                            }
                        }

                        return assemblyElement;
                    }

                    var resultsVisitor = CreateVisitor(consoleLock, quiet, defaultDirectory, assemblyElement, teamCity);

                    if (designTime)
                    {
                        var sink = (ITestExecutionSink)_services.GetService(typeof(ITestExecutionSink));
                        resultsVisitor = new DesignTimeExecutionVisitor(
                            sink,
                            vsTestcases,
                            resultsVisitor);
                    }

                    IList<ITestCase> filteredTestCases;
                    if (!designTime || designTimeFullyQualifiedNames.Count == 0)
                    {
                        filteredTestCases = discoveryVisitor.TestCases.Where(filters.Filter).ToList();
                    }
                    else
                    {
                        filteredTestCases = (from t in vsTestcases
                                             where designTimeFullyQualifiedNames.Contains(t.Value.FullyQualifiedName)
                                             select t.Key)
                                            .ToList();
                    }

                    if (filteredTestCases.Count == 0)
                        completionMessages.TryAdd(Path.GetFileName(assembly.AssemblyFilename), new ExecutionSummary());
                    else
                    {
                        controller.RunTests(filteredTestCases, resultsVisitor, executionOptions);
                        resultsVisitor.Finished.WaitOne();
                    }
                }
            }
            catch (Exception ex)
            {
                failed = true;

                var e = ex;
                while (e != null)
                {
                    Console.WriteLine("{0}: {1}", e.GetType().FullName, e.Message);
                    e = e.InnerException;
                }
            }

            return assemblyElement;
        }

        static Task<T> TaskRun<T>(Func<T> function)
        {
            var tcs = new TaskCompletionSource<T>();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    tcs.SetResult(function());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }
    }
}
