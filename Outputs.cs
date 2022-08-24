using Microsoft.Diagnostics.Tracing.AutomatedAnalysis;
using Microsoft.Diagnostics.Tracing.Parsers.AspNet;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;

namespace FusionParse
{
    internal class NgenBindFailures
    {
        internal NgenBindFailures()
        {
            bindResults = null;
            numCandidateNIs = 0;
            failureReason = NgenBindFailureReason.Uninitialized;
            this.moduleName = null;
            this.appDomain = null;
            this.process = null;
        }

        internal List<BindResults> bindResults;
        internal int numCandidateNIs;
        internal NgenBindFailureReason failureReason;
        internal string moduleName;
        internal string appDomain;
        internal string process;
    }

    internal struct BindResults
    {
        internal NgenBindFailureReason reason;
        internal string bindFailureContext;

        public BindResults(NgenBindFailureReason r)
        {
            reason = r;
            bindFailureContext = null;
        }
    }

    class AttemptedNILoadDisambiguation 
    {
        internal double              attemptedNILoadTimestamp;
        internal string                 attemptedNILoad;
        internal FusionBindMessage   nextMessage;
        internal string              callingAssemblyForNextLoad;
        internal string              nextLoadAssembly;
        internal string              rejectingNIBecauseOf;

        public AttemptedNILoadDisambiguation(double ts, string attemptedNILoad)
        {
            attemptedNILoadTimestamp = ts;
            this.attemptedNILoad = attemptedNILoad;
        }
    }
    class Assembly
    {
        internal long assemblyID;
        internal double assemblyLoadTime;
        internal string niPath;
        internal string name;
        internal string alternateName;  // Servicehub.ThreadedWaitDialog.exe is servicehub.host.clt.x86 to the runtime

        internal long appDomainID;
        internal string appDomain;

        internal bool loadFromLoad;
        internal bool ijwLoad;
        internal bool needsFullyQualifiedName;  // indicates this assembly started in LoadFrom context, and moved to Load context, but we dont know the fully qualified name

        internal bool niBindInProgress;
        internal bool niLoadInProgress;

        internal bool niBindCompleted;

        internal bool windowsRuntimeTypeLoadInProgress;

        internal bool beingLoadedAsANativeDependency;

        internal FusionBindMessage niBindEndMessage;

        internal bool nativeDependencyFound;
        internal int nativeDependencyLevel;
        internal string nativeDependencyName;
        internal Dictionary<string, bool> nativeDependencies;

        internal List<BindResults> bindResults;
        internal int numCandidateNIs;
        internal NgenBindFailureReason failureReason;

        internal bool abandonedDueToDuplicateDownload;

        public Assembly(string name)
        {
            this.name = name.ToLower();
            failureReason = NgenBindFailureReason.Uninitialized;
            nativeDependencies = new Dictionary<string, bool>();

            if (name.EndsWith("servicehub.threadedwaitdialog.exe"))
                alternateName = "servicehub.host.clr.x86";
        }

        static char[] delimiter = { '\\' };
        public static string GetDllNameFromPath(string NiName, string extension = ".ni.dll")
        {
            string[] splits = NiName.Split(delimiter);
            string dllName = splits[splits.Length - 1];
            string simpleName = dllName.Substring(0, dllName.Length - extension.Length).ToLower();
            return simpleName;
        }

        public static string GetSimpleName(string name)
        {
            int length = name.ToLower().IndexOf(", version");
            if (length >= 0)
                return name.Substring(0, length).ToLower();

            return GetDllNameFromPath(name, ".dll");
        }
        public void StartNgenBind()
        {
            niBindInProgress = true;
        }

        public void EndNgenBind(bool success)
        {
            niBindInProgress = false;
            niBindCompleted = true;

            Debug.Assert(failureReason == NgenBindFailureReason.Uninitialized ||
                failureReason == NgenBindFailureReason.NoNativeImage ||
                failureReason == NgenBindFailureReason.OptedOutOfNI ||
                failureReason == NgenBindFailureReason.MultipleErrors ||
                failureReason == NgenBindFailureReason.NIDependencySignature);

            if (!success && numCandidateNIs == 0 && failureReason == NgenBindFailureReason.Uninitialized)
            {
                this.failureReason = NgenBindFailureReason.NoNativeImage;
            }
        }

        public void StartValidatingNICandidate()
        {
            if (bindResults == null)
                bindResults = new List<BindResults>(1);
            bindResults.Add(new BindResults(NgenBindFailureReason.Uninitialized));
            numCandidateNIs++;
        }
        
        public void NativeDependencyFound(int level, string dependency, bool exceptionalCase = false)
        {
            Debug.Assert(exceptionalCase || niBindInProgress == true);
            //Debug.Assert((nativeDependencyFound == false && nativeDependencyName == null) || nativeDependencyName == dependency);
            nativeDependencyFound = true;
            nativeDependencyLevel = level;
            nativeDependencyName = dependency;
            if (dependency != null)
                nativeDependencies[dependency] = true;
        }

        public void ResetNativeDependencyFound()
        {
            nativeDependencyName = null;
            nativeDependencyFound = false;
            nativeDependencyLevel = 0;
        }
        public void SetNIBindOrLoadError(NgenBindFailureReason reason, string context)
        {
            failureReason = reason;
        }

        public void SetNICandidateError(NgenBindFailureReason reason, string context)
        {
            Debug.Assert(numCandidateNIs > 0);
            BindResults br = bindResults[numCandidateNIs - 1];
            br.reason = reason;
            br.bindFailureContext = context;
            bindResults[numCandidateNIs - 1] = br;
            failureReason = NgenBindFailureReason.MultipleErrors;
        }


        public void StartNgenLoad()
        {
            Debug.Assert(!niBindInProgress);
            niLoadInProgress = true;
        }

        public void StartWindowsRuntimeTypeLoad()
        {
            windowsRuntimeTypeLoadInProgress = true;
        }

        public void EndWindowsRuntimeTypeLoad()
        {
            Debug.Assert(windowsRuntimeTypeLoadInProgress);
            windowsRuntimeTypeLoadInProgress = false;
        }
    }

    class AppDomain
    {
        internal long appDomainID;
        internal string name;
        internal Dictionary<long, Assembly> assembliesByID;

        public AppDomain(long appDomainID, string name)
        {
            this.appDomainID = appDomainID;
            this.name = name;
            assembliesByID = new Dictionary<long, Assembly>();
        }

        internal void AddAssembly(long assemblyID, Assembly asm)
        {
            Debug.Assert(!assembliesByID.ContainsKey(assemblyID));
            assembliesByID[assemblyID] = asm;
        }

        internal Assembly GetAssembly(long ID)
        {
            if (assembliesByID.ContainsKey(ID))
            {
                return assembliesByID[ID];
            }

            return null;
        }

    }

    class Process
    {
        internal static Dictionary<int, Process> processes = new Dictionary<int, Process>();

        string ProcessName;
        internal int ProcessId;
        internal List<NgenBindFailures> bindFailures;

        internal Dictionary<long, Assembly> assembliesByID;
        internal Dictionary<long, AppDomain> appDomainsByID;
        internal Process(string name, int id)
        {
            ProcessName = name;
            ProcessId = id;
            bindFailures = new List<NgenBindFailures>();
            assembliesByID = new Dictionary<long, Assembly>();
            appDomainsByID = new Dictionary<long , AppDomain>();
        }

        
       internal void AddAssembly(long assemblyID, Assembly asm)
        {
            Debug.Assert(!assembliesByID.ContainsKey(assemblyID));
            assembliesByID[assemblyID] = asm;
        }

        internal AppDomain AddAppDomain(long ID, string name)
        {
            AppDomain ad = new AppDomain(ID, name);
            appDomainsByID[ID] = ad;
            return ad;
        }

        internal AppDomain GetAppDomain(long ID)
        {

            AppDomain ad;
            if (appDomainsByID.TryGetValue(ID, out ad))
                return ad;

            return ad;
        }

        internal Assembly GetAssembly(long ID)
        {
            if (assembliesByID.ContainsKey(ID))
            {
                return assembliesByID[ID];
            }

            return null;
        }

        internal static void PrintHtmlOutput()
        {
            //string fileName = Environment.GetEnvironmentVariable("TEMP") + "\\FusionParse_" + DateTime.Now.ToShortDateString();
            string fileName = "c:\\dumps\\fusiongen\\FusionPaseOutput.html";
            FileStream fs = new FileStream(fileName, FileMode.Create);
            StreamWriter sw = new StreamWriter(fs);

            StartDocument(sw, "Ngen Bind Errors");

            StartTable(sw, "Processes");
            WriteRow(sw, new string[] { "PID", "Process Name" }, true);
            int procCounter = 1;
            foreach (Process proc in processes.Values)
            {
                string s = "<a href=\"#section" + procCounter++ + "\" >" + proc.ProcessId.ToString() + "</a>";
                WriteRow(sw, new string[] {s, proc.ProcessName });
            }
            EndTable(sw);

            procCounter = 1;
            foreach (Process proc in processes.Values)
            {
                StartTable(sw, "Process: " + proc.ProcessName + " (" + proc.ProcessId + ")", procCounter++);
                WriteRow(sw, new string[] { "Assembly", "AppDomain", "Reason" }, true);

                foreach (NgenBindFailures b in proc.bindFailures)
                {
                    if (b.failureReason != NgenBindFailureReason.MultipleErrors)
                        WriteRow(sw, new string[] {b.moduleName, b.appDomain, GetMessage(b.failureReason, null) });
                    else
                    {
                        int index = 0;
                        WriteRow(sw, new string[] { b.moduleName, b.appDomain, GetMessage(b.failureReason, null) });
                        foreach (BindResults br in b.bindResults)
                        {
                            index++;
                            WriteRow(sw, new string[] { "Native Image # " + index, "", GetMessage(br.reason, br.bindFailureContext) });
                        }
                    }
                }
                EndTable(sw);
            }
            EndDocument(sw);
            sw.Close();
            fs.Close();
        }

        internal static void StartDocument(StreamWriter sw, string title)
        {
            sw.Write("<!DOCTYPE html> <html> <body>");
            sw.Write("<h1> {0} </h1>", title);
        }

        internal static void EndDocument(StreamWriter sw)
        {
            sw.Write("</body></html>");
        }

        internal static void StartTable(StreamWriter sw, string tableTitle, int id = -1)
        {
            string idtag = id == -1 ? "" : "id = \"section" + id + "\"";
            sw.Write("<h3 {0}> {1} </h3>", idtag, tableTitle);
            sw.Write("<table>");
        }

        internal static void EndTable(StreamWriter sw)
        {
            sw.Write("</table>");
        }

        internal static void WriteRow(StreamWriter sw, string[] contents, bool header = false)
        {
            string element = header ? "th" : "td";
            sw.Write("<tr>");
            foreach (string s in contents)
            {
                sw.Write("<{0}> {1} </{2}>", element, s, element);
            }
            sw.Write("</tr>");
        }
        internal static void PrintOutput()
        {
            foreach (Process proc in processes.Values)
            {
                Console.WriteLine("PROCESS: {0}", proc.ProcessName);
                foreach (NgenBindFailures b in proc.bindFailures)
                {
                    if (b.failureReason != NgenBindFailureReason.MultipleErrors)
                        Console.WriteLine("AppDomain = {0}, Assembly = {1}, Reason = {2}", null, b.moduleName, GetMessage(b.failureReason, null));
                    else
                    {
                        int index = 0;
                        Console.WriteLine("AppDomain = {0}, Assembly = {1}, Reason = {2}", null, b.moduleName, GetMessage(b.failureReason, null));
                        foreach (BindResults br in b.bindResults)
                        {
                            index++;
                            Console.WriteLine("\tNative Image # {0}, Reason = {1}", index, GetMessage(br.reason, br.bindFailureContext));
                        }
                }

                }
            }
        }

        internal static string GetMessage(NgenBindFailureReason reason, string context)
        {
            string message = null;

            switch (reason)
            {
                case NgenBindFailureReason.MultipleErrors:
                    message = "Multiple native images were found for this assembly. Find the bind failure reasons below";
                    break;

                case NgenBindFailureReason.NoNativeImage:
                    message = "No native image was found for this assembly. Either it was never ngened or ngen failed to generate an image";
                    break;

                case NgenBindFailureReason.LoadFrom:
                    message = "This assembly was loaded in LoadFrom context. Native images are not loaded for such assemblies";
                    break;

                case NgenBindFailureReason.DependencyNotFound:
                    message = "Dependency " + context + " was expected but could not be found";
                    break;

                case NgenBindFailureReason.DomainNeutralCannotShare:
                    message = "An ngen image of an assembly which is not loaded as domain-neutral cannot be used in multiple appdomains";
                    break;

                case NgenBindFailureReason.MissingDependencyFound:
                    message = "Dependency " + context + " was not found when native image was created, but is found at runtime";
                    break;

                case NgenBindFailureReason.NIDependencySignature:
                    message = "Dependency " + context + " has a signature different than one seen during native image generation";
                    break;

                case NgenBindFailureReason.NIDependencyNotNative:
                    message = "Dependency " + context + " is not native. No idea what this means";
                    break;

                case NgenBindFailureReason.NIDependencyIdentityMismatch:
                    message = "Native image dependency " + context + " has a different identity than expected";
                    break;

                case NgenBindFailureReason.NIDependencyVersionDifferent:
                    message = "Dependency " + context + " has a different version or identity than expected";
                    break;

                case NgenBindFailureReason.IJWBind:
                    message = "This is an IJW (C++ compiled as managed) assembly. Ngen images are not supported for such assemblies";
                    break;

                case NgenBindFailureReason.OptedOutOfNI:
                    message = "This executable has opted out of using the NI image for this assembly";
                    break;

                default:
                    Debug.Assert(false);
                    break;
            }

            return message;
        }
    }

}