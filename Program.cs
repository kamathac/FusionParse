using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.ClrPrivate;

namespace FusionParse
{
    internal class Program
    {
        internal static Dictionary<int, ThreadProcessor> ThreadProcessors = new Dictionary<int, ThreadProcessor>();
        static ThreadProcessor prevThdProc = null;
        static string limitToExe = "devenv";
        static void Main(string[] args)
        {
            const string etl = "c:\\dumps\\fusiongen\\debugging-Warmup-1.merged.etl";
            using (var source = new ETWTraceEventSource(etl))
            {
                var clrMainParser = new ClrTraceEventParser(source);
                var clrPrivateParser = new ClrPrivateTraceEventParser(source);

                //clrMainParser.LoaderModuleLoad += ProcessModuleLoad;
                clrMainParser.LoaderAssemblyLoad += ProcessAssemblyLoadEventPhase1;
                clrMainParser.LoaderAppDomainLoad += ProcessAppDomainLoad;
                clrPrivateParser.BindingFusionMessage += ProcessFusionMessagePhase1;

                source.Process();
            }

            using (var source = new ETWTraceEventSource(etl))
            {
                var clrMainParser = new ClrTraceEventParser(source);
                var clrPrivateParser = new ClrPrivateTraceEventParser(source);

                clrPrivateParser.BindingFusionMessage += ProcessFusionMessage;
                clrMainParser.LoaderAssemblyLoad += ProcessAssemblyLoadEventPhase2;

                source.Process();
            }

            Process.PrintHtmlOutput();
        }

        static void ProcessFusionMessagePhase1(FusionMessageTraceData data)
        {
            if (limitToExe != null && data.ProcessName != limitToExe)
                return;

            ThreadProcessor thd = GetThread(data.ThreadID);

            //Console.WriteLine("{0}, {1}, {2}, {3}", data.TimeStampRelativeMSec, data.ProcessName, data.ThreadID, data.Message);
            thd.ProcessFusionMessagePhase1(data);

        }

        static void ProcessFusionMessage(FusionMessageTraceData data)
        {
            if (limitToExe != null && data.ProcessName != limitToExe)
                return;

            ThreadProcessor thd = GetThread(data.ThreadID);

            Console.WriteLine("{0}, {1}, {2}, {3}", data.TimeStampRelativeMSec, data.ProcessName, data.ThreadID, data.Message);
            thd.ProcessFusionMessage(data);

        }

        static void ProcessAppDomainLoad(AppDomainLoadUnloadTraceData data)
        {
            if (limitToExe != null && data.ProcessName != limitToExe)
                return;

            Process process;
            if (!Process.processes.TryGetValue(data.ProcessID, out process))
            {
                process = new Process(data.ProcessName, data.ProcessID);
                Process.processes.Add(data.ProcessID, process);
            }

            AppDomain ad = process.AddAppDomain(data.AppDomainID, data.AppDomainName);

            foreach (KeyValuePair<long, Assembly> entry in process.assembliesByID)
            {
                if (entry.Value.appDomain == data.AppDomainID.ToString())
                {
                    entry.Value.appDomain = ad.name;
                    ad.AddAssembly(entry.Value.assemblyID, entry.Value);
                    process.assembliesByID.Remove(entry.Value.assemblyID);
                    return;
                }
            }

        }
        static void ProcessModuleLoad(ModuleLoadUnloadTraceData data)
        {
            if (limitToExe != null && data.ProcessName != limitToExe)
                return;

            ThreadProcessor thd = GetThread(data.ThreadID);

            //Console.WriteLine("MODULE LOAD {0}, {1}, {2}, {3}, {4}", data.TimeStampRelativeMSec, data.ProcessName, data.ThreadID, data.ModuleNativePath, data.ModuleILPath);
            thd.ProcessModuleLoad(data);
        }
        private static ThreadProcessor GetThread(int ThreadID)
        {
            ThreadProcessor thd = prevThdProc;
            if (prevThdProc == null || ThreadID != prevThdProc.ThreadId)
            {
                if (ThreadProcessors.TryGetValue(ThreadID, out thd) == false)
                {
                    thd = new ThreadProcessor(ThreadID);
                    ThreadProcessors.Add(ThreadID, thd);
                }

                prevThdProc = thd;
            }

            return thd;
        }

        static void ProcessAssemblyLoadEventPhase1(AssemblyLoadUnloadTraceData data)
        {
            if (limitToExe != null && data.ProcessName != limitToExe)
                return;

            ThreadProcessor thd = GetThread(data.ThreadID);

            //Console.WriteLine("ASSEMBLY LOAD {0}, {1}, {2}, {3}", data.TimeStampRelativeMSec, data.ProcessName, data.ThreadID, data.FullyQualifiedAssemblyName);
            thd.ProcessAssemblyLoadEventPhase1(data);
        }

        static void ProcessAssemblyLoadEventPhase2(AssemblyLoadUnloadTraceData data)
        {
            if (limitToExe != null && data.ProcessName != limitToExe)
                return;

            ThreadProcessor thd = GetThread(data.ThreadID);

            Console.WriteLine("ASSEMBLY LOAD {0}, {1}, {2}, {3}", data.TimeStampRelativeMSec, data.ProcessName, data.ThreadID, data.FullyQualifiedAssemblyName);
            thd.ProcessAssemblyLoadEventPhase2(data);
        }


    }
}
