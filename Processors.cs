using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Policy;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.ClrPrivate;

namespace FusionParse
{

    class ThreadProcessor
    {
        int threadId;

        Process process;

        FusionBindMessage prevMsg;

        double attemptedNIUseTS;
        Dictionary<double, AttemptedNILoadDisambiguation> attemptedNILoadResults;

        double duplicateDownloadTS;
        Dictionary<double, bool> duplicateDownloadPopAssembly;

        string failureContext = null;   // Name of the dependency being validated

        Stack<Assembly> assemblyLoadsInProgress;        // IL binding / NI binding / NI loading in progress
        internal List<Assembly> assemblyLoadsCompletedOnThread;  // Assembly load events fired

        Assembly topLevelNIBeingLoaded; // in presense of nested NI loads, this tracks the first NI that started it all

        Stack<Assembly> niBindsInProgress;
        Stack<Assembly> niLoadsInProgress;

        // The below variable is to fill a very specific gap
        Assembly mostRecentlyLoadedAssembly, mostRecentlyLoadedNestedAssembly;


        // Sometimes failure to bind to ngen image results in duplicate messages, messing up the logic, hence a hack
        bool hackIgnoreNativeBindWarnings;

        const long ASSEMBLYLOADLOOKAHEADMSEC = 1000;
        internal ThreadProcessor(int id)
        {
            threadId = id;
            process = null;
            niBindsInProgress = new Stack<Assembly>();
            niLoadsInProgress = new Stack<Assembly>();
            assemblyLoadsInProgress = new Stack<Assembly>();
            attemptedNILoadResults = new Dictionary<double, AttemptedNILoadDisambiguation>();
            duplicateDownloadPopAssembly = new Dictionary<double, bool>();
            hackIgnoreNativeBindWarnings = false;
            assemblyLoadsCompletedOnThread = new List<Assembly>();
            mostRecentlyLoadedAssembly = null;
        }

        internal int ThreadId
            { get { return threadId; } }

        internal void ProcessAssemblyLoadEventPhase1(AssemblyLoadUnloadTraceData data)
        {
            if (!Process.processes.TryGetValue(data.ProcessID, out process))
            {
                process = new Process(data.ProcessName, data.ProcessID);
                Process.processes.Add(data.ProcessID, process);
            }

            Assembly asm = new Assembly(data.FullyQualifiedAssemblyName);
            asm.assemblyID = data.AssemblyID;
            AppDomain ad = process.GetAppDomain(data.AppDomainID);
            if (ad != null)
            {
                asm.appDomain = ad.name;
                asm.appDomainID = data.AppDomainID;
                ad.AddAssembly(asm.assemblyID, asm);
            }
            else
            {
                asm.appDomain = data.AppDomainID.ToString();
                asm.appDomainID = data.AppDomainID;
                process.AddAssembly(data.AssemblyID, asm);
            }

        }

        internal void ProcessAssemblyLoadEventPhase2(AssemblyLoadUnloadTraceData data)
        {
            Assembly asm = null;
            if (((data.AssemblyFlags & AssemblyFlags.Native) != AssemblyFlags.Native) && ((data.AssemblyFlags & AssemblyFlags.Dynamic) != AssemblyFlags.Dynamic))
            {
                if (!Process.processes.TryGetValue(data.ProcessID, out process))
                {
                    process = new Process(data.ProcessName, data.ProcessID);
                    Process.processes.Add(data.ProcessID, process);
                }

                if (mostRecentlyLoadedNestedAssembly != null)
                {
                    if (mostRecentlyLoadedNestedAssembly.name.Contains(Assembly.GetSimpleName(data.FullyQualifiedAssemblyName)))
                    {
                        asm = mostRecentlyLoadedNestedAssembly;
                        mostRecentlyLoadedNestedAssembly = null;
                    }
                }
                if (asm == null && mostRecentlyLoadedAssembly != null)
                {
                    if (mostRecentlyLoadedAssembly.name.Contains(Assembly.GetSimpleName(data.FullyQualifiedAssemblyName)))
                    {
                        asm = mostRecentlyLoadedAssembly;
                        mostRecentlyLoadedAssembly = null;
                    }
                }
                if (asm == null)
                {
                    return;
                }

                if (asm.failureReason == NgenBindFailureReason.Uninitialized &&
                    asm.abandonedDueToDuplicateDownload)
                {
                    foreach(KeyValuePair<int, ThreadProcessor> kvp in Program.ThreadProcessors)
                    {
                        if (kvp.Value == this)
                            continue;

                        if (kvp.Value.mostRecentlyLoadedAssembly != null &&
                            kvp.Value.mostRecentlyLoadedAssembly.name == asm.name &&
                            kvp.Value.mostRecentlyLoadedAssembly.failureReason != NgenBindFailureReason.Uninitialized)
                        {
                            asm = kvp.Value.mostRecentlyLoadedAssembly;
                            break;
                        }
                    }
                }

                NgenBindFailures nbf = new NgenBindFailures();
                nbf.failureReason = asm.failureReason;
                nbf.bindResults = asm.bindResults;
                nbf.numCandidateNIs = asm.numCandidateNIs;
                nbf.moduleName = data.FullyQualifiedAssemblyName;
                nbf.appDomain = process.GetAppDomain(data.AppDomainID).name;
                Debug.Assert(nbf.failureReason != NgenBindFailureReason.Uninitialized);
                process.bindFailures.Add(nbf);

            }

        }

        internal void ProcessModuleLoad(ModuleLoadUnloadTraceData data)
        {
            Process process;
            if (!Process.processes.TryGetValue(data.ProcessID, out process))
            {
                process= new Process(data.ProcessName, data.ProcessID);
                Process.processes.Add(data.ProcessID, process);
            }

            // this method is not in use and is never called
        }

        bool trackBindInfoPostAttemptToLoadNI = false;
        string attemptedNILoad;
        internal void ProcessFusionMessagePhase1(FusionMessageTraceData data)
        {
            string parameter;
            FusionBindMessage msg = GetFusionMessage(data.Message, out parameter);

            if (prevMsg == FusionBindMessage.AttemptingToUseNI)
            {
                attemptedNILoadResults[attemptedNIUseTS].nextMessage = msg;

                Debug.Assert(msg == FusionBindMessage.NativeImageSuccessfullyUsed ||
                    msg == FusionBindMessage.ZAPNINotDomainNeutral ||
                    msg == FusionBindMessage.RejectingNIDependencyNotNative ||
                    msg == FusionBindMessage.PreBindStateInfo ||
                    msg == FusionBindMessage.LogIJWExplicitBind);

                if (msg == FusionBindMessage.PreBindStateInfo)
                {
                    trackBindInfoPostAttemptToLoadNI = true;
                }

                if (msg == FusionBindMessage.RejectingNIDependencyNotNative)
                {
                    attemptedNILoadResults[attemptedNIUseTS].rejectingNIBecauseOf = parameter.ToLower();
                }

            }

            if (msg == FusionBindMessage.AttemptingToUseNI)
            {
                attemptedNIUseTS = data.TimeStampRelativeMSec;
                attemptedNILoad = parameter;
                AttemptedNILoadDisambiguation ani = new AttemptedNILoadDisambiguation(attemptedNIUseTS, parameter);
                attemptedNILoadResults[attemptedNIUseTS] = ani;
            }

            if (trackBindInfoPostAttemptToLoadNI && msg == FusionBindMessage.LogDisplayName)
            {
                this.attemptedNILoadResults[attemptedNIUseTS].nextLoadAssembly = SanitizeAssemblyName(parameter);
            }

            if (trackBindInfoPostAttemptToLoadNI && msg == FusionBindMessage.CallingAssembly)
            {
                trackBindInfoPostAttemptToLoadNI = false;
                if (parameter != "(Unknown)")
                    this.attemptedNILoadResults[attemptedNIUseTS].callingAssemblyForNextLoad = parameter.ToLower();
            }

            if (msg == FusionBindMessage.LogDuplicateDownload)
            {
                duplicateDownloadTS = data.TimeStampRelativeMSec;
            }

            if (prevMsg == FusionBindMessage.LogDuplicateDownload)
            {
                Debug.Assert(duplicateDownloadTS != 0);
                if (msg == FusionBindMessage.PreBindStateInfo)
                    duplicateDownloadPopAssembly[duplicateDownloadTS] = true;
            }

            prevMsg = msg;

        }

        internal void ProcessFusionMessage(FusionMessageTraceData data)
        {

            string parameter = null;
            BindResults br;
            Assembly asmTemp = null;
            Assembly assemblyLoadInProgress = null;
            Assembly assemblyBindInProgress = null;
            Assembly NiLoadInProgress = null;

            if (!Process.processes.TryGetValue(data.ProcessID, out process))
            {
                process = new Process(data.ProcessName, data.ProcessID);
                Process.processes.Add(data.ProcessID, process);
            }

            FusionBindMessage msg = GetFusionMessage(data.Message, out parameter);

            //if (data.TimeStampRelativeMSec > 29475.766 && data.TimeStampRelativeMSec < 29475.768)
            //    Debug.Assert(false);                    


            switch (msg)
            {

                case FusionBindMessage.BeginILBind:
                    Debug.Assert(assemblyLoadsInProgress.Count == 0);
                    assemblyLoadInProgress = new Assembly("mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                    assemblyLoadsInProgress.Push(assemblyLoadInProgress);
                    mostRecentlyLoadedAssembly = assemblyLoadInProgress;

                    break;

                case FusionBindMessage.LogExeBind:
                    Debug.Assert(assemblyLoadsInProgress.Count == 0);
                    assemblyLoadInProgress = new Assembly(parameter);
                    assemblyLoadsInProgress.Push(assemblyLoadInProgress);
                    mostRecentlyLoadedAssembly = assemblyLoadInProgress;
                    break;

                case FusionBindMessage.LogDisplayName:

                    string asmName = SanitizeAssemblyName(parameter);

                    if (assemblyLoadsInProgress.Count == 0)
                    {
                        assemblyLoadInProgress = new Assembly(asmName);
                        assemblyLoadsInProgress.Push(assemblyLoadInProgress);

                        mostRecentlyLoadedAssembly = assemblyLoadInProgress;
                        break;
                    }
                    else
                        assemblyLoadInProgress = assemblyLoadsInProgress.Peek();

                    if (assemblyLoadInProgress.nativeDependencyFound)
                    {
                        Debug.Assert(assemblyLoadInProgress.nativeDependencyName == null ||
                            assemblyLoadInProgress.nativeDependencyName == asmName);
                        assemblyLoadInProgress.ResetNativeDependencyFound();
                        assemblyLoadInProgress = new Assembly(asmName);

                        assemblyLoadInProgress.beingLoadedAsANativeDependency = true;
                        assemblyLoadsInProgress.Push(assemblyLoadInProgress);

                        //niBindsInProgress.Push(asm);
                    }
                    break;

                case FusionBindMessage.BeginNativeImageBind:
                    if (prevMsg == msg)
                        break;

                    if (niBindsInProgress.Count == 0 && hackIgnoreNativeBindWarnings)
                        break;

                    assemblyLoadInProgress = assemblyLoadsInProgress.Peek();
                    Debug.Assert(assemblyLoadInProgress != null);
                    assemblyLoadInProgress.StartNgenBind();
                    niBindsInProgress.Push(assemblyLoadInProgress);

                    // If this assembly has been loaded already, then the assembly can be showing the failureReason from that
                    // earlier load. But if the current load has reached "beginnativeimagebind" stage, then this is likely
                    // a new appdomain, and we should not carry forward the same failureReason and should reset it
                    assemblyLoadInProgress.failureReason = NgenBindFailureReason.Uninitialized;
                    break;

                case FusionBindMessage.LogStartValidating:
                    if (prevMsg == FusionBindMessage.LogStartValidating)
                        break;

                    assemblyBindInProgress = niBindsInProgress.Peek();
                    Debug.Assert(assemblyBindInProgress != null);
                    //Debug.Assert(assemblyBindInProgress.numCandidateNIs == 0 || assemblyBindInProgress.bindResults[0].reason != NgenBindFailureReason.Uninitialized);
                    // There are some weird cases where a new validation cycle starts in the middle of a previous one, with no indication of
                    // what happened to the previous one. We just take this in our stride and act as if the validation in progress is continuing
                    if (assemblyBindInProgress.bindResults == null ||
                        assemblyBindInProgress.bindResults[0].reason != NgenBindFailureReason.Uninitialized)
                        assemblyBindInProgress.StartValidatingNICandidate();

                    break;

                case FusionBindMessage.LogLevel2NIValidation:
                    Debug.Assert(assemblyLoadsInProgress.Count != 0);
                    Debug.Assert(niBindsInProgress.Count != 0);
                    assemblyBindInProgress = niBindsInProgress.Peek();
                    assemblyLoadInProgress = assemblyLoadsInProgress.Peek();

                    if (assemblyBindInProgress.nativeDependencyFound && assemblyBindInProgress.nativeDependencyLevel == 1)
                    {
                        // We transitioned from Level 1 to Level 2. This means a nested NI bind has begun, without an explicit
                        // "LOG: Display name", "Begin ngen bind" messages. So we have to do what we would have done when ngen bind begins
                        string nestedNIBindAssembly = assemblyBindInProgress.nativeDependencyName;
                        assemblyBindInProgress.ResetNativeDependencyFound();

                        // Create a new assembly on stacks to indicate start of new load/bind
                        //Assembly asm = process.GetAssembly(nestedNIBindAssembly);
                        //if (asm == null)
                        {
                            assemblyLoadInProgress = new Assembly(nestedNIBindAssembly);
                        //    process.AddAssembly(nestedNIBindAssembly, asm);
                        }
                        assemblyLoadInProgress.beingLoadedAsANativeDependency = true;
                        assemblyLoadInProgress.niBindInProgress = true;
                        assemblyLoadInProgress.niBindEndMessage = FusionBindMessage.NativeImageHasCorrectVersion;

                        assemblyLoadsInProgress.Push(assemblyLoadInProgress);

                        niBindsInProgress.Push(assemblyLoadInProgress);
                        assemblyBindInProgress = assemblyLoadInProgress;

                    }

                    assemblyBindInProgress.NativeDependencyFound(2, parameter.ToLower());

                    // Any previous dependency that was being validated can be considered as success, since a new one has been found
                    failureContext = null;
                    break;

                case FusionBindMessage.NativeImageHasCorrectVersion:
                    Debug.Assert(niBindsInProgress.Count != 0);
                    assemblyBindInProgress = niBindsInProgress.Peek();
                    if (assemblyBindInProgress.niBindEndMessage  == FusionBindMessage.NativeImageHasCorrectVersion)
                    {
                        assemblyBindInProgress = niBindsInProgress.Pop();
                        assemblyBindInProgress.EndNgenBind(true);
                        assemblyBindInProgress.ResetNativeDependencyFound();
                    }
                    break;

                case FusionBindMessage.LogLevel1NIValidation:
                    Debug.Assert(assemblyLoadsInProgress.Count != 0);
                    Debug.Assert(niBindsInProgress.Count != 0);
                    assemblyBindInProgress = niBindsInProgress.Peek();
                    assemblyLoadInProgress = assemblyLoadsInProgress.Peek();

                    assemblyBindInProgress.NativeDependencyFound(1, parameter.ToLower());

                    // Any previous dependency that was being validated can be considered as success, since a new one has been found
                    failureContext = null;
                    break;

                case FusionBindMessage.LogLevelxILValidation:
                    Debug.Assert(assemblyLoadsInProgress.Count != 0);
                    Debug.Assert(niBindsInProgress.Count != 0);
                    assemblyBindInProgress = niBindsInProgress.Peek();
                    assemblyLoadInProgress = assemblyLoadsInProgress.Peek();

                    // if previously native image dependency was found, and we encounter the next dependency msg, it measn the 
                    // previous one was successfully validated
                    if (assemblyBindInProgress.nativeDependencyFound)
                        assemblyBindInProgress.ResetNativeDependencyFound();

                    // Any previous dependency that was being validated can be considered as success, since a new one has been found
                    failureContext = null;
                    break;

                case FusionBindMessage.ENDOperationSucceeded:
                    if (prevMsg == FusionBindMessage.ENDOperationSucceeded)
                        break;

                    if (niBindsInProgress.Count == 0)
                        break;

                    if (niBindsInProgress.Peek().windowsRuntimeTypeLoadInProgress)
                    {
                        niBindsInProgress.Peek().EndWindowsRuntimeTypeLoad();
                        break;
                    }

                    Debug.Assert(niBindsInProgress.Peek().niBindEndMessage != FusionBindMessage.NativeImageHasCorrectVersion);

                    assemblyBindInProgress = niBindsInProgress.Pop();
                    assemblyBindInProgress.EndNgenBind(true);

                    if (assemblyBindInProgress.beingLoadedAsANativeDependency)
                    {
                        assemblyBindInProgress.beingLoadedAsANativeDependency = false;
                        Debug.Assert(assemblyLoadsInProgress.Peek() == assemblyBindInProgress);
                        assemblyLoadsInProgress.Pop();
                    }
                    
                    break;

                    // This is the trickiest message, because it can be very ambiguous how to interpret the following
                    // messages. This is because under some circumstances, there can be no message indicating status of
                    // this attempted load (this can happen if this is the second load of the same NI within an appdomain)
                case FusionBindMessage.AttemptingToUseNI:
                    Debug.Assert(niBindsInProgress.Count == 0);

                    assemblyLoadInProgress = FindAssemblyWhoseNIIsBeingLoaded(parameter);
                    Debug.Assert(assemblyLoadInProgress != null);
                    Debug.Assert(assemblyLoadInProgress.niBindInProgress == false);

                    if (assemblyLoadsInProgress.Count > 1 &&
                        topLevelNIBeingLoaded == null)
                        topLevelNIBeingLoaded = FindTheBaseAssembly();

                    FusionBindMessage nextMsg = FusionBindMessage.BeginILBind;
                    string nextBindCallingAssembly = null;
                    if (attemptedNILoadResults.ContainsKey(data.TimeStampRelativeMSec))
                    {
                        nextMsg = attemptedNILoadResults[data.TimeStampRelativeMSec].nextMessage;
                        nextBindCallingAssembly = attemptedNILoadResults[data.TimeStampRelativeMSec].callingAssemblyForNextLoad;
                    }

                    if (nextMsg == FusionBindMessage.ZAPNINotDomainNeutral)
                    {
                        // If next message is this, it always refers to the current load. Start a load, and let the processing
                        // of the ZAPNINot.. message handle it naturally
                        assemblyLoadInProgress.StartNgenLoad();
                        niLoadsInProgress.Push(assemblyLoadInProgress);
                    }
                    else if (nextMsg == FusionBindMessage.RejectingNIDependencyNotNative)
                    {
                        // IN THE PRESENCE OF NESTED NI LOADS, WE ASSUME THIS ERROR IS ALWAYS FOR THE OUTER NI
                        if (assemblyLoadsInProgress.Count > 1 && topLevelNIBeingLoaded != assemblyLoadInProgress)
                            ThisAssemblyIsDoneLoading(assemblyLoadInProgress);
                        else
                        {
                            assemblyLoadInProgress.StartNgenLoad();
                            niLoadsInProgress.Push(assemblyLoadInProgress);
                        }

                    }
                    else if (nextMsg == FusionBindMessage.PreBindStateInfo)
                    {
                        if (assemblyLoadsInProgress.Count == 1)
                        {
                            if (nextBindCallingAssembly != null &&
                                assemblyLoadInProgress.name == nextBindCallingAssembly &&
                                assemblyLoadInProgress.niBindCompleted == true &&
                                assemblyLoadInProgress.nativeDependencies.ContainsKey(attemptedNILoadResults[data.TimeStampRelativeMSec].nextLoadAssembly))
                            {
                                assemblyLoadInProgress.StartNgenLoad();
                                niLoadsInProgress.Push(assemblyLoadInProgress);
                            }
                            else
                            {
                                ThisAssemblyIsDoneLoading(assemblyLoadInProgress);
                            }
                        }
                        else
                        {
                            if (nextBindCallingAssembly != null &&
                                (topLevelNIBeingLoaded.name == nextBindCallingAssembly ||
                                Assembly.GetSimpleName(topLevelNIBeingLoaded.name) == Assembly.GetSimpleName(nextBindCallingAssembly)) &&   // normalize for paths in exe case
                                topLevelNIBeingLoaded.niBindCompleted == true &&
                                topLevelNIBeingLoaded.nativeDependencies.ContainsKey(attemptedNILoadResults[data.TimeStampRelativeMSec].nextLoadAssembly))
                            {
                                if (topLevelNIBeingLoaded == assemblyLoadInProgress)
                                {
                                    assemblyLoadInProgress.StartNgenLoad();
                                    niLoadsInProgress.Push(assemblyLoadInProgress);
                                }
                                else
                                {
                                    ThisAssemblyIsDoneLoading(assemblyLoadInProgress);
                                }
                            }
                            else
                            {
                                ThisAssemblyIsDoneLoading(assemblyLoadInProgress);
                            }
                        }
                    }
                    else
                    {
                        ThisAssemblyIsDoneLoading(assemblyLoadInProgress);
                    }
                    break;

                case FusionBindMessage.PreBindStateInfo:
                    // Reset some state
                    hackIgnoreNativeBindWarnings = false;
                     
                    // Account for a weird case where a native dependency starts getting validated, even though we didnt see it as part of NI validation
                    if (prevMsg == FusionBindMessage.AttemptingToUseNI && 
                        assemblyLoadsInProgress.Count == 1 &&
                        assemblyLoadsInProgress.Peek().niBindCompleted)
                    {
                        Debug.Assert(niLoadsInProgress.Count != 0);
                        assemblyLoadsInProgress.Peek().NativeDependencyFound(1, null, true);
                    }

                    if (prevMsg == FusionBindMessage.NativeImageSuccessfullyUsed &&
                        assemblyLoadsInProgress.Count == 1 &&
                        niLoadsInProgress.Count == 1)
                    {
                        assemblyLoadsInProgress.Pop();
                        niLoadsInProgress.Pop();
                        topLevelNIBeingLoaded = null;
                    }
                    if ((prevMsg == FusionBindMessage.LogLoadedInDefaultContext ||
                        prevMsg == FusionBindMessage.LogBindToNISucceeded) && 
                        assemblyLoadsInProgress.Count > 0)
                    {
                        int loadsInProgress = assemblyLoadsInProgress.Count;
                        Debug.Assert(loadsInProgress > 0);
                        for (int i = 0; i < loadsInProgress; i++)
                        {
                            Debug.Assert(assemblyLoadsInProgress.Peek().niLoadInProgress == false);
                            Debug.Assert(assemblyLoadsInProgress.Peek().niBindInProgress == false);
                            assemblyLoadsInProgress.Pop();
                        }

                    }
                    break;

                case FusionBindMessage.DependencyName:
                    Debug.Assert(niBindsInProgress.Count > 0);
                    assemblyBindInProgress = niBindsInProgress.Peek();
                    if (assemblyBindInProgress.nativeDependencyFound &&
                        assemblyBindInProgress.nativeDependencyName != parameter.ToLower())
                    {
                        // There are instances where a dependency starts at "System, Version=4.0", but DependencyNAme says "System, Version=2.0"
                        assemblyBindInProgress.nativeDependencies[parameter.ToLower()] = true;
                    }
                    failureContext = parameter;

                    break;

                case FusionBindMessage.LogSwitchLoadFromToLoad:
                    Debug.Assert(assemblyLoadsInProgress.Count != 0);
                    assemblyLoadInProgress = assemblyLoadsInProgress.Peek();
                    assemblyLoadInProgress.loadFromLoad = false;
                    assemblyLoadInProgress.failureReason = NgenBindFailureReason.Uninitialized;

                    break;

                case FusionBindMessage.LogPostPolicyReference:
                    Debug.Assert(assemblyLoadsInProgress.Count != 0);
                    assemblyLoadInProgress = assemblyLoadsInProgress.Peek();
                    if (assemblyLoadInProgress.needsFullyQualifiedName)
                    {
                        assemblyLoadsInProgress.Pop();

                        Assembly asm = new Assembly(parameter);
                        asm.needsFullyQualifiedName = false;
                        asm.loadFromLoad = assemblyLoadInProgress.loadFromLoad;
                        asm.ijwLoad = assemblyLoadInProgress.ijwLoad;
                        asm.failureReason = assemblyLoadInProgress.failureReason;
                        assemblyLoadsInProgress.Push(asm);
                    }
                    break;

                case FusionBindMessage.ENDIncorrectFunction:
                    if (niBindsInProgress.Count == 0 && hackIgnoreNativeBindWarnings)
                        break;

                    Debug.Assert(niBindsInProgress.Count != 0);

                    if (niBindsInProgress.Peek().niBindEndMessage != FusionBindMessage.NativeImageHasCorrectVersion)
                    {
                        assemblyBindInProgress = niBindsInProgress.Pop();
                        assemblyBindInProgress.EndNgenBind(false);
      
                        Debug.Assert(assemblyLoadsInProgress.Peek() == assemblyBindInProgress);
                        assemblyLoadsInProgress.Pop();
                    }

                    break;

                case FusionBindMessage.WRNMissingDependencyFound:
                    if (prevMsg == msg)
                        break;

                    Debug.Assert(niBindsInProgress.Count != 0);
                    Debug.Assert(failureContext != null);
                    assemblyBindInProgress = niBindsInProgress.Peek();
                    assemblyBindInProgress.SetNICandidateError(NgenBindFailureReason.MissingDependencyFound, failureContext);
                    failureContext = null;

                    break;

                case FusionBindMessage.WRNNoMatchingNI:
                    
                    if (niBindsInProgress.Count == 0 && !hackIgnoreNativeBindWarnings)
                        hackIgnoreNativeBindWarnings = true;
                    else
                        hackIgnoreNativeBindWarnings = false;
                    
                    break;

                case FusionBindMessage.LogWhereRefBind:
                    Debug.Assert(assemblyLoadsInProgress.Count == 0);
                    assemblyLoadInProgress = new Assembly(parameter);
                    assemblyLoadInProgress.loadFromLoad = true;
                    assemblyLoadInProgress.needsFullyQualifiedName = true;  // Setting this property for all loadFrom assemblies. Review need for separate variable
                    assemblyLoadsInProgress.Push(assemblyLoadInProgress);
                    mostRecentlyLoadedAssembly = assemblyLoadInProgress;
                    break;

                case FusionBindMessage.LogAllProbingFailed:
                case FusionBindMessage.LogAssemblyLoadedInLoadFromContext:
                case FusionBindMessage.ERRUnrecoverablePredownloadError:
                    Debug.Assert(assemblyLoadsInProgress.Count == 1);
                    // If error encountered while loading dependency, then no further action. If top level assembly has this error, it needs to be popped off the stack
                    if (!assemblyLoadsInProgress.Peek().niBindInProgress)
                    {
                        assemblyLoadInProgress = assemblyLoadsInProgress.Pop();
                    }
                    break;

                case FusionBindMessage.WRNNIWillNotBeProbedLoadFrom:
                    if (prevMsg == msg)
                        break;

                    Debug.Assert(niBindsInProgress.Count == 0);
                    Debug.Assert(assemblyLoadsInProgress.Count != 0);
                    assemblyLoadInProgress = assemblyLoadsInProgress.Peek();
                    assemblyLoadInProgress.SetNIBindOrLoadError(NgenBindFailureReason.LoadFrom, null);

                    break;

                case FusionBindMessage.WRNTimestampDoesNotMatch:
                    if (prevMsg == msg)
                        break;

                    assemblyBindInProgress = niBindsInProgress.Peek();
                    Debug.Assert(assemblyBindInProgress != null);
                    
                    if (assemblyBindInProgress.bindResults == null ||
                        assemblyBindInProgress.bindResults[assemblyBindInProgress.numCandidateNIs - 1].reason != NgenBindFailureReason.Uninitialized)
                        assemblyBindInProgress.StartValidatingNICandidate();

                    break;

                case FusionBindMessage.WRNSignatureDoesNotMatch:
                    if (prevMsg == msg)
                        break;

                    Debug.Assert(niBindsInProgress.Count != 0);
                    assemblyBindInProgress = niBindsInProgress.Peek();
                    // Is this an error with with the candidate NI image itself or one of its dependencies? 
                    if (assemblyBindInProgress.numCandidateNIs == 0)
                    {
                        Debug.Assert(false); // dont think we'll hit this. Confirm and delete code
                        assemblyBindInProgress.SetNIBindOrLoadError(NgenBindFailureReason.NIDependencySignature, null);
                    }
                    else
                    {
                        assemblyBindInProgress.SetNICandidateError(NgenBindFailureReason.NIDependencySignature, failureContext);
                        failureContext = null;
                    }

                    break;

                case FusionBindMessage.RejectingNIDependencyIdentity:
                    Debug.Assert(niBindsInProgress.Count != 0);
                    Debug.Assert(failureContext != null);
                    assemblyBindInProgress = niBindsInProgress.Peek();
                    assemblyBindInProgress.SetNICandidateError(NgenBindFailureReason.NIDependencyIdentityMismatch, failureContext);
                    failureContext = null;
                    break;

                case FusionBindMessage.WRNOptedOutOfNI:
                    Debug.Assert(assemblyLoadsInProgress.Count != 0);
                    assemblyLoadInProgress = assemblyLoadsInProgress.Peek();
                    assemblyLoadInProgress.SetNIBindOrLoadError(NgenBindFailureReason.OptedOutOfNI, null);
                    break;

                case FusionBindMessage.ZAPNINotDomainNeutral:
                    Debug.Assert(assemblyLoadsInProgress.Count != 0);
                    Debug.Assert(niLoadsInProgress.Count > 0);
                    NiLoadInProgress = niLoadsInProgress.Pop();
                    NiLoadInProgress.SetNIBindOrLoadError(NgenBindFailureReason.DomainNeutralCannotShare, null);
                    // In the presence of nested loads, if the outer NI load fails, then both inner and outer assembly loads are "done". No more fusion messages
                    // if only the inner NI load fails, then outer NI load follows next
                    if (assemblyLoadsInProgress.Count > 1)
                    {
                        if (assemblyLoadsInProgress.Peek() != NiLoadInProgress) // the top of assembly load stack is not same as top of ni load stack. This happens when this error message is for outer NI
                        {
                            assemblyLoadsInProgress.Clear();
                        }
                        else
                        {
                            mostRecentlyLoadedNestedAssembly = NiLoadInProgress;
                            assemblyLoadInProgress = assemblyLoadsInProgress.Pop();
                            Debug.Assert(assemblyLoadInProgress == NiLoadInProgress);
                        }
                    }
                    else
                    {
                        assemblyLoadInProgress = assemblyLoadsInProgress.Pop();
                    }

                    break;

                case FusionBindMessage.WRNCannotLoadIL:
                    // Error encountered while validating a dependency
                    if (prevMsg == FusionBindMessage.WRNCannotLoadIL)
                        break;

                    Debug.Assert(niBindsInProgress.Count > 0);
                    Debug.Assert(failureContext != null);
                    assemblyBindInProgress = niBindsInProgress.Peek();
                    assemblyBindInProgress.SetNICandidateError(NgenBindFailureReason.DependencyNotFound, failureContext);
                    failureContext = null;
                    break;

                case FusionBindMessage.RejectingNIDependencyNotNative:
                    Debug.Assert(assemblyLoadsInProgress.Count != 0);
                    Debug.Assert(niLoadsInProgress.Count != 0);
                    NiLoadInProgress = niLoadsInProgress.Pop();
                    NiLoadInProgress.SetNIBindOrLoadError(NgenBindFailureReason.NIDependencyNotNative, parameter);

                    assemblyLoadInProgress = assemblyLoadsInProgress.Pop();
                    Debug.Assert(NiLoadInProgress == assemblyLoadInProgress);

                    break;

                case FusionBindMessage.WRNAssemblyResolvedToDifferentVersion:
                    if (prevMsg == msg)
                        break;

                    Debug.Assert(niBindsInProgress.Count != 0);
                    Debug.Assert(failureContext != null);
                    assemblyBindInProgress = niBindsInProgress.Peek();
                    assemblyBindInProgress.SetNICandidateError(NgenBindFailureReason.NIDependencyVersionDifferent, failureContext);
                    failureContext = null;
                    break;

                case FusionBindMessage.LogIJWExplicitBind:
                    {
                        Assembly asm = new Assembly(parameter);
                        asm.ijwLoad = true;
                        asm.SetNIBindOrLoadError(NgenBindFailureReason.IJWBind, null);
                        mostRecentlyLoadedAssembly = asm;
                    }
                    break;

                case FusionBindMessage.LogIJWFileNotFound:
                    if (assemblyLoadsInProgress.Count > 0)
                        assemblyLoadInProgress = assemblyLoadsInProgress.Peek();
                    else
                        assemblyLoadInProgress = mostRecentlyLoadedAssembly;

                    assemblyLoadInProgress.ijwLoad = true;
                    assemblyLoadInProgress.SetNIBindOrLoadError(NgenBindFailureReason.IJWBind, null);
                    break;

                case FusionBindMessage.NativeImageSuccessfullyUsed:
                    /*
                    Debug.Assert(niLoadsInProgress.Count != 0);
                    NiLoadInProgress = niLoadsInProgress.Pop();
                    NiLoadInProgress.niLoadInProgress = false;

                    Debug.Assert(assemblyLoadsInProgress.Peek() == NiLoadInProgress);
                    assemblyLoadInProgress = assemblyLoadsInProgress.Pop();
                    */
                    break;

                case FusionBindMessage.LogAssemblyNameIs:
                    if (assemblyLoadsInProgress.Count == 0)
                    {
                        Debug.Assert(niBindsInProgress.Count == 0);
                        Debug.Assert(mostRecentlyLoadedAssembly != null);
                        assemblyLoadsInProgress.Push(mostRecentlyLoadedAssembly);
                        hackIgnoreNativeBindWarnings = false;
                    }
                    else
                    {
                        assemblyLoadInProgress = assemblyLoadsInProgress.Peek();
                        if (assemblyLoadInProgress.needsFullyQualifiedName)
                        {
                            assemblyLoadsInProgress.Pop();

                            Assembly asm = new Assembly(parameter);
                            assemblyLoadInProgress.needsFullyQualifiedName = false;
                            asm.loadFromLoad = assemblyLoadInProgress.loadFromLoad;
                            asm.ijwLoad = assemblyLoadInProgress.ijwLoad;
                            asm.failureReason = assemblyLoadInProgress.failureReason;
                            assemblyLoadsInProgress.Push(asm);
                            //process.AddAssembly(parameter, asm);
                        }
                    }
                    break;

                case FusionBindMessage.WRNPartialBindingInfo:
                    Debug.Assert(assemblyLoadsInProgress.Count != 0);
                    assemblyLoadsInProgress.Peek().needsFullyQualifiedName = true;
                    break;

                case FusionBindMessage.BeginWindowsTypeBind:
                    Debug.Assert(niBindsInProgress.Count != 0);
                    assemblyBindInProgress = niBindsInProgress.Peek();
                    assemblyBindInProgress.StartWindowsRuntimeTypeLoad();
                    break;

                case FusionBindMessage.LogDuplicateDownload:
                    
                    Debug.Assert(assemblyLoadsInProgress.Count > 0);
                    if (duplicateDownloadPopAssembly.ContainsKey(data.TimeStampRelativeMSec))
                    {
                        // If we know that this message is followed by a "prebindinfo" message, we have to drop the current assembly off the stack
                        Assembly asm = assemblyLoadsInProgress.Pop();
                        asm.abandonedDueToDuplicateDownload = true;
                    }
                    
                    break;

                case FusionBindMessage.ERRFailedToCompleteSetup:
                    Debug.Assert(assemblyLoadsInProgress.Count > 0);
                    assemblyLoadsInProgress.Pop();
                    break;

                default:
                    break;

            }
            prevMsg = msg;

        }

        private static string SanitizeAssemblyName(string parameter)
        {
            // String processorArchitecture and "(FullySpecified)" or "(Partial)" that are appended (in that order) to some messages but not others
            int index = parameter.IndexOf("processorArchitecture");
            int count = 0;
            if (index == -1)
            {
                index = parameter.IndexOf("(Fully-specified)");
                if (index == -1)
                {
                    index = parameter.IndexOf("(Partial)");
                    if (index == -1)
                        index = parameter.Length;   // none of the literals were found. Take the whole assembly name
                    else
                        count = index - 2;  // (Partial) was found
                }
                else
                    count = index - 2;  // (Fully-specified) was found
            }
            else
                count = index - 2;  // processorArchitecture was found

            string asmName = parameter.Substring(0, count).ToLower();
            return asmName;
        }

        internal Assembly FindTheBaseAssembly()
        {
            Assembly baseAsm = null;
            foreach (Assembly asm in assemblyLoadsInProgress)
            {
                baseAsm = asm;
            }
            return baseAsm;
        }

        internal Assembly FindAssemblyWhoseNIIsBeingLoaded(string NiName)
        {
            string simpleName = Assembly.GetDllNameFromPath(NiName);

            foreach (Assembly asm in assemblyLoadsInProgress)
            {
                if (Assembly.GetSimpleName(asm.name) == simpleName)
                    return asm;

                if (asm.alternateName != null && asm.alternateName == simpleName)
                    return asm;
            }
            Debug.Assert(false);
            return null;
        }

        internal void ThisAssemblyIsDoneLoading(Assembly asmPopThis)
        {
            Stack<Assembly> temp = new Stack<Assembly>();

            while (true)
            {
                Debug.Assert(assemblyLoadsInProgress.Count != 0);
                Assembly asmOnTop = assemblyLoadsInProgress.Pop();
                if (asmOnTop != asmPopThis)
                {
                    temp.Push(asmOnTop);
                }
                else
                    break;
            }

            while (temp.Count > 0)
            {
                assemblyLoadsInProgress.Push(temp.Pop());
            }
        }

        internal FusionBindMessage GetFusionMessage(string Message, out string parameter)
        {
            parameter = null;

            int index;
            if ((index = Message.IndexOf("Start validating IL dependency ")) != -1)
            {
                parameter = Message.Substring(index + "Start validating IL dependency ".Length);
                parameter = parameter.Substring(0, parameter.Length - 1);
                return FusionBindMessage.LogLevelxILValidation;
            }

            if (Message.Contains("Dependency name:"))
            {
                parameter = Message.Substring("  Dependency name: ".Length);
                return FusionBindMessage.DependencyName;
            }

            if (Message.StartsWith("=== Pre-bind"))
                return FusionBindMessage.PreBindStateInfo;

            if (Message.StartsWith("LOG: DisplayName"))
            {
                parameter = Message.Substring(19);
                return FusionBindMessage.LogDisplayName;
            }

            if (Message.StartsWith("LOG: Appbase"))
                return FusionBindMessage.LogAppBase;

            if (Message.StartsWith("LOG: Initial PrivatePath"))
                return FusionBindMessage.LogInitialPrivatePath;

            if (Message.StartsWith("LOG: Private path hint found"))
                return FusionBindMessage.LogPrivatePathHint;

            if (Message.StartsWith("LOG: Dynamic Base"))
                return FusionBindMessage.LogDynamicBase;

            if (Message.StartsWith("LOG: Cache"))
                return FusionBindMessage.LogCacheBase;

            if (Message.StartsWith("LOG: AppName"))
                return FusionBindMessage.LogAppName;

            if (Message.StartsWith("Calling assembly"))
            {
                parameter = Message.Substring("Calling assembly : ".Length).TrimEnd(new char[] { '.' });
                return FusionBindMessage.CallingAssembly;
            }

            if (Message.StartsWith("==="))
                return FusionBindMessage.Spacer;

            if (Message.StartsWith("LOG: This bind starts in default"))
                return FusionBindMessage.LogBindStartsInDefaultContext;

            if (Message.StartsWith("LOG: No application"))
                return FusionBindMessage.LogNoAppConfig;

            if (Message.StartsWith("LOG: Using host"))
                return FusionBindMessage.LogUsingHostConfig;

            if (Message.StartsWith("LOG: Using machine configuration"))
                return FusionBindMessage.LogUsingMachineConfig;

            if (Message.StartsWith("LOG: Post-policy reference: "))
            {
                parameter = Message.Substring("LOG: Post-policy reference: ".Length);
                return FusionBindMessage.LogPostPolicyReference;
            }

            if (Message.StartsWith("LOG: GAC Lookup was unsuccessful"))
                return FusionBindMessage.LogGACLookupFailed;

            if (Message.StartsWith("LOG: Attempting download of new URL"))
                return FusionBindMessage.LogAttemptingDownload;

            if (Message.StartsWith("LOG: Assembly download was successful"))
            {
                parameter = Message.Substring("LOG: Assembly download was successful. Attempting setup of file: ".Length);
                return FusionBindMessage.LogAssemblyDownloadSuccessful;
            }

            if (Message.StartsWith("LOG: Entering run-from-source"))
                return FusionBindMessage.LogEnteringSetupPhase;

            if (Message.StartsWith("LOG: Assembly Name is: "))
            {
                parameter = Message.Substring("LOG: Assembly Name is: ".Length);
                return FusionBindMessage.LogAssemblyNameIs;
            }

            if (Message.Contains("BEGIN : Native image bind"))
                return FusionBindMessage.BeginNativeImageBind;

            if (Message.Contains("LOG: Bind to native image succeeded"))
                return FusionBindMessage.LogBindToNISucceeded;

            if (Message.Contains("WRN: No matching"))
                return FusionBindMessage.WRNNoMatchingNI;

            if (Message.StartsWith("LOG: Binding succeeds. Returns assembly from "))
            {
                parameter = Message.Substring("LOG: Binding succeeds.Returns assembly from ".Length);
                parameter = parameter.Substring(0, parameter.Length - 1).ToLower();   // Remove the trailing .
                return FusionBindMessage.LogBindingSucceeds;
            }

            if (Message.StartsWith("LOG: IL assembly loaded"))
            {
                parameter = Message.Substring("LOG: IL assembly loaded from ".Length);
                return FusionBindMessage.LogILLoadedFrom;
            }

            if (Message.StartsWith("LOG: Assembly is loaded in default"))
                return FusionBindMessage.LogLoadedInDefaultContext;

            if (Message.StartsWith("Attempting to use native image "))
            {
                parameter = Message.Substring("Attempting to use native image ".Length);
                parameter = parameter.Substring(0, parameter.Length - 1).ToLower();
                return FusionBindMessage.AttemptingToUseNI;
            }

            if (Message.StartsWith("LOG: Redirect"))
            {
                parameter = Message.Substring(Message.IndexOf("redirected to ") + "redirected to ".Length);
                parameter = parameter.Substring(0, parameter.Length - 1);   // Remove the trailing .
                return FusionBindMessage.LogRedirectFound;
            }

            if ((index = Message.IndexOf("Start validating native image dependency ")) != -1)
            {
                parameter = Message.Substring(index + "Start validating native image dependency ".Length);
                parameter = parameter.Substring(0, parameter.Length - 1);
                if (Message.Contains("Level 1"))
                    return FusionBindMessage.LogLevel1NIValidation;
                else if (Message.Contains("Level 2"))
                    return FusionBindMessage.LogLevel2NIValidation;
                else
                    Debug.Assert(false);
            }

            if (Message.StartsWith("Native image has correct"))
                return FusionBindMessage.NativeImageHasCorrectVersion;

            if (Message.Contains("LOG: Start validating"))
                return FusionBindMessage.LogStartValidating;

            if (Message.Contains("LOG: Validation of"))
                return FusionBindMessage.LogValidationSucceeded;
             
            if (Message.Contains("END   : The operation completed"))
                return FusionBindMessage.ENDOperationSucceeded;

            if (Message.StartsWith("BEGIN : IL image bind") )
                return FusionBindMessage.BeginILBind;

            if (Message.StartsWith("LOG: Found assembly by looking in the GAC"))
                return FusionBindMessage.LogGACLookupSuccessful;

            if (Message.StartsWith("Native image successfully used"))
            {
                return FusionBindMessage.NativeImageSuccessfullyUsed;
            }

            if (Message.StartsWith("LOG: EXE explicit bind"))
            {
                parameter = Message.Substring("LOG: EXE explicit bind. File path:".Length);
                parameter = parameter.Substring(0, parameter.Length - 1).ToLower();
                return FusionBindMessage.LogExeBind;
            }

            if (Message.Contains("END   : Incorrect function"))
                return FusionBindMessage.ENDIncorrectFunction;

            if (Message.Contains("WRN: Cannot load IL"))
                return FusionBindMessage.WRNCannotLoadIL;

            if (Message.StartsWith("LOG: Download of application configuration"))
                return FusionBindMessage.LogDownloadAppConfig;

            if (Message.StartsWith("LOG: Found application configuration"))
                return FusionBindMessage.LogFoundAppConfig;

            if (Message.StartsWith("LOG: Using application configuration"))
                return FusionBindMessage.LogUsingAppConfig;

            if (Message.StartsWith("LOG: Using codebase from policy file"))
                return FusionBindMessage.LogUsingCodebase;

            if (Message.StartsWith("LOG: Where-ref bind. Location = "))
            {
                parameter = Message.Substring("LOG: Where-ref bind. Location = ".Length);
                return FusionBindMessage.LogWhereRefBind;
            }

            if (Message.StartsWith("LOG: Where-ref bind Codebase matches"))
                return FusionBindMessage.LogLoadFromMatchesLoad;

            if (Message.StartsWith("LOG: The post-policy assembly reference"))
                return FusionBindMessage.LogPostPolicyProbeAgain;

            if (Message.StartsWith("LOG: Switch from LoadFrom context"))
                return FusionBindMessage.LogSwitchLoadFromToLoad;

            if (Message.StartsWith("LOG: This bind starts in LoadFrom"))
                return FusionBindMessage.LogBindStartsInLoadFromContext;

            if (Message.StartsWith("WRN: Native image will not be probed in LoadFrom"))
                return FusionBindMessage.WRNNIWillNotBeProbedLoadFrom;

            if (Message.StartsWith("LOG: All probing URLs attempted and failed"))
                return FusionBindMessage.LogAllProbingFailed;

            if (Message.Contains("WRN: Timestamp of the IL assembly"))
                return FusionBindMessage.WRNTimestampDoesNotMatch;

            if (Message.Contains("LOG: Re-apply policy for"))
                return FusionBindMessage.LogReapplyWhereRef;

            if (Message.Contains("LOG: Assembly is loaded in LoadFrom"))
                return FusionBindMessage.LogAssemblyLoadedInLoadFromContext;

            if (Message.Contains("LOG: ProcessorArchitecture is locked"))
                return FusionBindMessage.LogProcessorArchLocked;

            if (Message.StartsWith("LOG: The same bind was seen before, and was failed with"))
                return FusionBindMessage.LogSameFailedbind;

            if (Message.StartsWith("ERR: Unrecoverable error occurred during pre-download"))
                return FusionBindMessage.ERRUnrecoverablePredownloadError;

            if (Message.Contains("BEGIN : Windows Runtime Type"))
                return FusionBindMessage.BeginWindowsTypeBind;

            if (Message.Contains("WRN: Signature of the IL assembly"))
                return FusionBindMessage.WRNSignatureDoesNotMatch;

            if (Message.StartsWith("WRN: Partial binding information"))
                return FusionBindMessage.WRNPartialBindingInfo;

            if (Message.StartsWith("WRN: Assembly Name:"))
                return FusionBindMessage.WRNAssemblyName;

            if (Message.StartsWith("WRN: A partial bind occurs when"))
                return FusionBindMessage.WRNAPartialBindOccurs;

            if (Message.StartsWith("LOG: Policy not being applied"))
                return FusionBindMessage.LogPolicyNotBeingApplied;

            if (Message.StartsWith("LOG: A partially-specified assembly bind succeeded"))
                return FusionBindMessage.LogPartialAssemblyBindSucceeded;

            if (Message.StartsWith("LOG: Where-ref bind Codebase does not match"))
                return FusionBindMessage.LogLoadFromDoesNotMatch;

            if (Message.Contains("WRN: Dependency assembly was not found at ngen time"))
                return FusionBindMessage.WRNMissingDependencyFound;

            if (Message.Contains("WRN: Comparing the assembly name resulted in the mismatch: Build Number"))
                return FusionBindMessage.WRNAssemblyNameMismatch;

            if (Message.Contains("WRN: Comparing the assembly name resulted in the mismatch: Minor Version"))
                return FusionBindMessage.WRNAssemblyNameMismatchMinorVersion;

            if (Message.Contains("WRN: Comparing the assembly name resulted in the mismatch: Major Version"))
                return FusionBindMessage.WRNAssemblyNameMismatchMajorVersion;

            if (Message.Contains("ERR: The assembly reference did not match"))
                return FusionBindMessage.ERRAssemblyReferenceMismatch;

            if (Message.Contains("ERR: Run-from-source setup phase failed"))
                return FusionBindMessage.ERRRunFromSourceFailed;

            if (Message.Contains("ERR: Failed to complete setup"))
                return FusionBindMessage.ERRFailedToCompleteSetup;

            if (Message.Contains("Rejecting native image because native image dependency"))
                return FusionBindMessage.RejectingNIDependencyIdentity;

            if (Message.Contains("WRN: Assembly resolved to a different version or identity than expected"))
                return FusionBindMessage.WRNAssemblyResolvedToDifferentVersion;

            if (Message.Contains("WRN: The application has opted out of"))
                return FusionBindMessage.WRNOptedOutOfNI;

            if (Message.Contains("ZAP: An ngen image of an assembly which is not loaded as domain-neutral cannot be used in multiple appdomains"))
                return FusionBindMessage.ZAPNINotDomainNeutral;

            if (Message.Contains("Discarding native image"))
                return FusionBindMessage.DiscardingNI;

            if (Message.Contains("Rejecting native image because dependency") &&
                Message.Contains("is not native"))
            {
                int start = "Rejecting native image because dependency ".Length;
                int end = Message.IndexOf(" is not native");
                int parameterLength = end - start;
                parameter = Message.Substring(start, parameterLength);
                return FusionBindMessage.RejectingNIDependencyNotNative;
            }

            if (Message.StartsWith("LOG: Duplicate download found"))
                return FusionBindMessage.LogDuplicateDownload;

            if (Message.StartsWith("LOG: Version redirect found"))
                return FusionBindMessage.LogVersionRedirectFound;

            if (Message.StartsWith("LOG: IJW explicit bind"))
            {
                parameter = Message.Substring(Message.IndexOf("path:") + 5);
                return FusionBindMessage.LogIJWExplicitBind;
            }

            if (Message.StartsWith("LOG: IJW assembly bind returned file not found"))
                return FusionBindMessage.LogIJWFileNotFound;

            if (Message.StartsWith("LOG: This is an inspection only bind"))
                return FusionBindMessage.LogInspectionOnlyBind;

            if (Message.StartsWith("LOG: IJW assembly bind returned the same"))
                return FusionBindMessage.IJWBindReturnedSameManifest;

            if (Message.Contains("Found:"))
                return FusionBindMessage.Found;

            if (Message.StartsWith("LOG: Configuration file"))
                return FusionBindMessage.LogConfigurationNotFound;

            Debug.Assert(false);
            return FusionBindMessage.LogDisplayName;
        }
    }
}