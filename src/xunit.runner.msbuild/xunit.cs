using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;
using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace Xunit.Runner.MSBuild
{
    public class xunit : MSBuildTask, ICancelableTask
    {
        volatile bool cancel;
        readonly ConcurrentDictionary<string, ExecutionSummary> completionMessages = new ConcurrentDictionary<string, ExecutionSummary>();
        XunitFilters filters;

        public xunit()
        {
            ParallelizeTestCollections = true;
            ShadowCopy = true;
            TeamCity = Environment.GetEnvironmentVariable("TEAMCITY_PROJECT_NAME") != null;
        }

        [Required]
        public ITaskItem[] Assemblies { get; set; }

        public string ExcludeTraits { get; set; }

        [Output]
        public int ExitCode { get; protected set; }

        public ITaskItem Html { get; set; }

        public string IncludeTraits { get; set; }

        public int MaxParallelThreads { get; set; }

        protected bool NeedsXml
        {
            get { return Xml != null || XmlV1 != null || Html != null; }
        }

        public bool ParallelizeAssemblies { get; set; }

        public bool ParallelizeTestCollections { get; set; }

        public bool ShadowCopy { get; set; }

        public bool TeamCity { get; set; }

        protected XunitFilters Filters
        {
            get
            {
                if (filters == null)
                {
                    var traitParser = new TraitParser(msg => Log.LogWarning(msg));
                    filters = new XunitFilters();
                    traitParser.Parse(IncludeTraits, filters.IncludedTraits);
                    traitParser.Parse(ExcludeTraits, filters.ExcludedTraits);
                }

                return filters;
            }
        }

        public bool Verbose { get; set; }

        public string WorkingFolder { get; set; }

        public ITaskItem Xml { get; set; }

        public ITaskItem XmlV1 { get; set; }

        public void Cancel()
        {
            cancel = true;
        }

        protected virtual IFrontController CreateFrontController(string assemblyFilename, string configFileName)
        {
            return new XunitFrontController(assemblyFilename, configFileName, ShadowCopy);
        }

        protected virtual XmlTestExecutionVisitor CreateVisitor(string assemblyFileName, XElement assemblyElement)
        {
            if (TeamCity)
                return new TeamCityVisitor(Log, assemblyElement, () => cancel);

            return new StandardOutputVisitor(Log, assemblyElement, Verbose, () => cancel, completionMessages);
        }

        public override bool Execute()
        {
            RemotingUtility.CleanUpRegisteredChannels();
            XElement assembliesElement = null;
            var environment = String.Format("{0}-bit .NET {1}", IntPtr.Size * 8, Environment.Version);

            if (NeedsXml)
                assembliesElement = new XElement("assemblies");

            string originalWorkingFolder = Directory.GetCurrentDirectory();

            using (AssemblyHelper.SubscribeResolve())
            {
                Log.LogMessage(MessageImportance.High, "xUnit.net MSBuild runner ({0})", environment);

                var testAssemblyPaths = Assemblies.Select(assembly =>
                {
                    string assemblyFileName = assembly.GetMetadata("FullPath");
                    string configFileName = assembly.GetMetadata("ConfigFile");
                    if (configFileName != null && configFileName.Length == 0)
                        configFileName = null;

                    return Tuple.Create(assemblyFileName, configFileName);
                }).ToList();

                if (WorkingFolder != null)
                    Directory.SetCurrentDirectory(WorkingFolder);

                if (ParallelizeAssemblies)
                {
                    var tasks = testAssemblyPaths.Select(path => Task.Run(() => ExecuteAssembly(path.Item1, path.Item2)));
                    var results = Task.WhenAll(tasks).GetAwaiter().GetResult();
                    foreach (var assemblyElement in results.Where(result => result != null))
                        assembliesElement.Add(assemblyElement);
                }
                else
                {
                    foreach (var path in testAssemblyPaths)
                    {
                        var assemblyElement = ExecuteAssembly(path.Item1, path.Item2);
                        if (assemblyElement != null)
                            assembliesElement.Add(assemblyElement);
                    }
                }

                if (completionMessages.Count > 0)
                {
                    Log.LogMessage(MessageImportance.High, "=== TEST EXECUTION SUMMARY ===");
                    int longestAssemblyName = completionMessages.Keys.Max(key => key.Length);
                    int longestTotal = completionMessages.Values.Max(summary => summary.Total.ToString().Length);
                    int longestFailed = completionMessages.Values.Max(summary => summary.Failed.ToString().Length);
                    int longestSkipped = completionMessages.Values.Max(summary => summary.Skipped.ToString().Length);
                    int longestTime = completionMessages.Values.Max(summary => summary.Time.ToString("0.000s").Length);

                    foreach (var message in completionMessages.OrderBy(m => m.Key))
                        Log.LogMessage(MessageImportance.High,
                                       "   {0}  Total: {1}, Failed: {2}, Skipped: {3}, Time: {4}",
                                       message.Key.PadRight(longestAssemblyName),
                                       message.Value.Total.ToString().PadLeft(longestTotal),
                                       message.Value.Failed.ToString().PadLeft(longestFailed),
                                       message.Value.Skipped.ToString().PadLeft(longestSkipped),
                                       message.Value.Time.ToString("0.000s").PadLeft(longestTime));

                    if (completionMessages.Count > 1)
                        Log.LogMessage(MessageImportance.High,
                                       "   {0}         {1}          {2}           {3}        {4}" + Environment.NewLine +
                                       "           {5} {6}          {7}           {8}        {9}",
                                       " ".PadRight(longestAssemblyName),
                                       "-".PadRight(longestTotal, '-'),
                                       "-".PadRight(longestFailed, '-'),
                                       "-".PadRight(longestSkipped, '-'),
                                       "-".PadRight(longestTime, '-'),
                                       "GRAND TOTAL:".PadLeft(longestAssemblyName),
                                       completionMessages.Values.Sum(summary => summary.Total),
                                       completionMessages.Values.Sum(summary => summary.Failed),
                                       completionMessages.Values.Sum(summary => summary.Skipped),
                                       completionMessages.Values.Sum(summary => summary.Time).ToString("0.000s"));
                }
            }

            Directory.SetCurrentDirectory(WorkingFolder ?? originalWorkingFolder);

            if (NeedsXml)
            {
                if (Xml != null)
                    assembliesElement.Save(Xml.GetMetadata("FullPath"));

                if (XmlV1 != null)
                    Transform("xUnit1.xslt", assembliesElement, XmlV1);

                if (Html != null)
                    Transform("HTML.xslt", assembliesElement, Html);
            }

            return ExitCode == 0;
        }

        XElement CreateAssemblyXElement()
        {
            return NeedsXml ? new XElement("assembly") : null;
        }

        protected virtual XElement ExecuteAssembly(string assemblyFileName, string configFileName)
        {
            if (cancel)
                return null;

            var assemblyElement = CreateAssemblyXElement();

            try
            {
                using (var controller = CreateFrontController(assemblyFileName, configFileName))
                using (var discoveryVisitor = new TestDiscoveryVisitor())
                {
                    controller.Find(includeSourceInformation: false, messageSink: discoveryVisitor, options: new XunitDiscoveryOptions());
                    discoveryVisitor.Finished.WaitOne();

                    using (var resultsVisitor = CreateVisitor(assemblyFileName, assemblyElement))
                    {
                        var executionOptions = new XunitExecutionOptions
                        {
                            DisableParallelization = !ParallelizeTestCollections,
                            MaxParallelThreads = MaxParallelThreads
                        };

                        controller.RunTests(discoveryVisitor.TestCases.Where(Filters.Filter).ToList(), resultsVisitor, executionOptions);
                        resultsVisitor.Finished.WaitOne();

                        if (resultsVisitor.Failed != 0)
                            ExitCode = 1;
                    }
                }
            }
            catch (Exception ex)
            {
                var e = ex;

                while (e != null)
                {
                    Log.LogError("{0}: {1}", e.GetType().FullName, e.Message);

                    foreach (string stackLine in e.StackTrace.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries))
                        Log.LogError(stackLine);

                    e = e.InnerException;
                }

                ExitCode = -1;
            }

            return assemblyElement;
        }

        void Transform(string resourceName, XNode xml, ITaskItem outputFile)
        {
            var xmlTransform = new XslCompiledTransform();

            using (var writer = XmlWriter.Create(outputFile.GetMetadata("FullPath"), new XmlWriterSettings { Indent = true }))
            using (var xsltReader = XmlReader.Create(typeof(xunit).Assembly.GetManifestResourceStream("Xunit.Runner.MSBuild." + resourceName)))
            using (var xmlReader = xml.CreateReader())
            {
                xmlTransform.Load(xsltReader);
                xmlTransform.Transform(xmlReader, writer);
            }
        }
    }
}
