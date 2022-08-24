﻿
namespace FusionParse
{
 
    enum ProcessingState
    {
        Uninitialized,
        NoAssemblyBindInProgress,
        LoaderPhase,
        NgenPhaseLevel1,
        NgenPhaseLevel2,
        NgenUsePhase
    }

    enum NgenBindFailureReason
    {
        Uninitialized,
        MultipleErrors,
        NoNativeImage,
        LoadFrom,
        DependencyNotFound,
        MissingDependencyFound,
        NIDependencySignature,
        NIDependencyTimestamp,
        NIDependencyIdentityMismatch,
        NIDependencyNotNative,
        NIDependencyVersionDifferent,
        DomainNeutralCannotShare,
        OptedOutOfNI,
        IJWBind,
    }
    enum AssemblyNIState
    {
        Uninitialized,
        NgenBindInProgress,
        NgenBindFailed,
        NgenBindSucceeded,
        NgenLoaded
    }

    enum LoaderScope
    {
        Load,
        LoadFrom
    }

    enum DependencyKind
    {
        ILDependency,
        NativeImageDependency
    }

    enum FusionBindMessage
    {
        BeginILBind,
        LogExeBind,
        PreBindStateInfo,
        LogDisplayName,
        LogAppBase,
        LogInitialPrivatePath,
        LogDynamicBase,
        LogCacheBase,
        LogAppName,
        CallingAssembly,
        Spacer,
        LogBindStartsInDefaultContext,
        LogNoAppConfig,
        LogUsingAppConfig,
        LogUsingHostConfig,
        LogUsingMachineConfig,
        LogRedirectFound,
        LogPostPolicyReference,
        LogGACLookupSuccessful,
        LogGACLookupFailed,
        LogAttemptingDownload,
        LogAssemblyDownloadSuccessful,
        LogEnteringSetupPhase,
        LogAssemblyNameIs,
        LogBindToNISucceeded,
        LogBindingSucceeds,
        LogILLoadedFrom,
        LogLoadedInDefaultContext,
        BeginNativeImageBind,
        LogStartValidating,
        LogLevelxILValidation,
        LogLevel1NIValidation,
        LogLevel2NIValidation,
        DependencyName,
        NativeImageHasCorrectVersion,
        AttemptingToUseNI,
        NativeImageSuccessfullyUsed,
        LogValidationSucceeded,
        ENDOperationSucceeded,
        ENDIncorrectFunction,
        WRNNoMatchingNI,
        WRNCannotLoadIL,
        WRNNIWillNotBeProbedLoadFrom,
        WRNTimestampDoesNotMatch,
        LogDownloadAppConfig,
        LogFoundAppConfig,
        LogPrivatePathHint,
        LogUsingCodebase,
        LogWhereRefBind,
        LogBindStartsInLoadFromContext,
        LogAllProbingFailed,
        LogReapplyWhereRef,
        LogAssemblyLoadedInLoadFromContext,
        LogProcessorArchLocked,
        LogLoadFromMatchesLoad,
        LogPostPolicyProbeAgain,
        LogSwitchLoadFromToLoad,
        LogSameFailedbind,
        ERRUnrecoverablePredownloadError,
        BeginWindowsTypeBind,
        WRNSignatureDoesNotMatch,
        WRNPartialBindingInfo,
        WRNAssemblyName,
        WRNAPartialBindOccurs,
        LogPolicyNotBeingApplied,
        LogPartialAssemblyBindSucceeded,
        LogLoadFromDoesNotMatch,
        WRNMissingDependencyFound,
        WRNAssemblyNameMismatch,
        ERRAssemblyReferenceMismatch,
        ERRRunFromSourceFailed,
        ERRFailedToCompleteSetup,
        RejectingNIDependencyIdentity,
        WRNAssemblyResolvedToDifferentVersion,
        WRNOptedOutOfNI,
        ZAPNINotDomainNeutral,
        DiscardingNI,
        RejectingNIDependencyNotNative,
        LogDuplicateDownload,
        LogVersionRedirectFound,
        WRNAssemblyNameMismatchMinorVersion,
        WRNAssemblyNameMismatchMajorVersion,
        LogIJWExplicitBind,
        LogIJWFileNotFound,
        LogInspectionOnlyBind,
        IJWBindReturnedSameManifest,
        Found,
        LogConfigurationNotFound,
    }
}
