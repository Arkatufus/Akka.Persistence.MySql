﻿#I @"tools/FAKE/tools"
#r "FakeLib.dll"

open System
open System.IO
open System.Text

open Fake
open Fake.DotNetCli
open Fake.DocFxHelper

// Information about the project for Nuget and Assembly info files
let product = "Akka.Persistence.MySql"
let configuration = "Release"

// Metadata used when signing packages and DLLs
let signingName = "Akka.Persistence.MySql"
let signingDescription = "Akka.Persistence support for MySql"
let signingUrl = "https://github.com/akkadotnet/Akka.Persistence.MySql"

// Read release notes and version
let solutionFile = FindFirstMatchingFile "*.sln" (__SOURCE_DIRECTORY__ @@ "src") // dynamically look up the solution
let buildNumber = environVarOrDefault "BUILD_NUMBER" "0"
let hasTeamCity = (not (buildNumber = "0")) // check if we have the TeamCity environment variable for build # set
let preReleaseVersionSuffix = "beta" + (if (not (buildNumber = "0")) then (buildNumber) else DateTime.UtcNow.Ticks.ToString())

let releaseNotes =
    File.ReadLines (__SOURCE_DIRECTORY__ @@ "RELEASE_NOTES.md")
    |> ReleaseNotesHelper.parseReleaseNotes

let versionFromReleaseNotes =
    match releaseNotes.SemVer.PreRelease with
    | Some r -> r.Origin
    | None -> ""

let versionSuffix = 
    match (getBuildParam "nugetprerelease") with
    | "dev" -> preReleaseVersionSuffix
    | "" -> versionFromReleaseNotes
    | str -> str

// Directories
let toolsDir = __SOURCE_DIRECTORY__ @@ "tools"
let output = __SOURCE_DIRECTORY__  @@ "bin"
let outputTests = __SOURCE_DIRECTORY__ @@ "TestResults"
let outputPerfTests = __SOURCE_DIRECTORY__ @@ "PerfResults"
let outputNuGet = output @@ "nuget"

// Configuration values for tests
let testNetFrameworkVersion = "net471"
let testNetCoreVersion = "netcoreapp3.1"
let testNetVersion = "net7.0"

Target "Clean" (fun _ ->
    CleanDir output
    CleanDir outputTests
    CleanDir outputPerfTests
    CleanDir outputNuGet

    CleanDirs !! "./**/bin"
    CleanDirs !! "./**/obj"
)

Target "RestorePackages" (fun _ ->
    DotNetCli.Restore
        (fun p -> 
            { p with
                Project = "./src/Akka.Persistence.MySql.sln"
                NoCache = false })
)

Target "AssemblyInfo" (fun _ ->
    XmlPokeInnerText "./src/Directory.Build.props" "//Project/PropertyGroup/VersionPrefix" releaseNotes.AssemblyVersion
    XmlPokeInnerText "./src/Directory.Build.props" "//Project/PropertyGroup/PackageReleaseNotes" (releaseNotes.Notes |> String.concat "\n")
)

Target "Build" (fun _ ->
    let additionalArgs = if versionSuffix.Length > 0 then [sprintf "/p:VersionSuffix=%s" versionSuffix] else []
    DotNetCli.Build
        (fun p -> 
            { p with
                Project = solutionFile
                Configuration = configuration
                AdditionalArgs = additionalArgs})
)

//--------------------------------------------------------------------------------
// Tests targets 
//--------------------------------------------------------------------------------
module internal ResultHandling =
    let (|OK|Failure|) = function
        | 0 -> OK
        | x -> Failure x

    let buildErrorMessage = function
        | OK -> None
        | Failure errorCode ->
            Some (sprintf "xUnit2 reported an error (Error Code %d)" errorCode)

    let failBuildWithMessage = function
        | DontFailBuild -> traceError
        | _ -> (fun m -> raise(FailedTestsException m))

    let failBuildIfXUnitReportedError errorLevel =
        buildErrorMessage
        >> Option.iter (failBuildWithMessage errorLevel)

Target "RunTests" (fun _ ->
    let projects = 
        match (isWindows) with 
        | true -> !! "./src/**/*.Tests.csproj"
        | _ -> !! "./src/**/*.Tests.csproj" // if you need to filter specs for Linux vs. Windows, do it here

    let runSingleProject project =
        let arguments =
            match (hasTeamCity) with
            | true -> (sprintf "test -c Release --no-build --logger:trx --logger:\"console;verbosity=normal\" --framework %s --results-directory %s -- -parallel none -teamcity" testNetFrameworkVersion outputTests)
            | false -> (sprintf "test -c Release --no-build --logger:trx --logger:\"console;verbosity=normal\" --framework %s --results-directory %s -- -parallel none" testNetFrameworkVersion outputTests)

        let result = ExecProcess(fun info ->
            info.FileName <- "dotnet"
            info.WorkingDirectory <- (Directory.GetParent project).FullName
            info.Arguments <- arguments) (TimeSpan.FromMinutes 30.0) 
        
        ResultHandling.failBuildIfXUnitReportedError TestRunnerErrorLevel.Error result  

    CreateDir outputTests
    projects |> Seq.iter (log)
    projects |> Seq.iter (runSingleProject)
)

Target "RunTestsNet" (fun _ ->
    let projects = 
        match (isWindows) with 
        | true -> !! "./src/**/*.Tests.csproj"
        | _ -> !! "./src/**/*.Tests.csproj" // if you need to filter specs for Linux vs. Windows, do it here

    let runSingleProject project =
        let arguments =
            match (hasTeamCity) with
            | true -> (sprintf "test -c Release --no-build --logger:trx --logger:\"console;verbosity=normal\" --framework %s --results-directory %s -- -parallel none -teamcity" testNetVersion outputTests)
            | false -> (sprintf "test -c Release --no-build --logger:trx --logger:\"console;verbosity=normal\" --framework %s --results-directory %s -- -parallel none" testNetVersion outputTests)

        let result = ExecProcess(fun info ->
            info.FileName <- "dotnet"
            info.WorkingDirectory <- (Directory.GetParent project).FullName
            info.Arguments <- arguments) (TimeSpan.FromMinutes 30.0) 
        
        ResultHandling.failBuildIfXUnitReportedError TestRunnerErrorLevel.Error result  

    CreateDir outputTests
    projects |> Seq.iter (log)
    projects |> Seq.iter (runSingleProject)
)

Target "NBench" <| fun _ ->
    let projects = 
        match (isWindows) with 
        | true -> !! "./src/**/*.Tests.Performance.csproj"
        | _ -> !! "./src/**/*.Tests.Performance.csproj" // if you need to filter specs for Linux vs. Windows, do it here


    let runSingleProject project =
        let arguments =
            match (hasTeamCity) with
            | true -> (sprintf "nbench --nobuild --teamcity --concurrent true --trace true --output %s" (outputPerfTests))
            | false -> (sprintf "nbench --nobuild --concurrent true --trace true --output %s" (outputPerfTests))

        let result = ExecProcess(fun info ->
            info.FileName <- "dotnet"
            info.WorkingDirectory <- (Directory.GetParent project).FullName
            info.Arguments <- arguments) (TimeSpan.FromMinutes 30.0) 
        
        ResultHandling.failBuildIfXUnitReportedError TestRunnerErrorLevel.Error result
    
    projects |> Seq.iter runSingleProject

//--------------------------------------------------------------------------------
// Code signing targets
//--------------------------------------------------------------------------------
Target "SignPackages" (fun _ ->
    let canSign = hasBuildParam "SignClientSecret" && hasBuildParam "SignClientUser"
    if(canSign) then
        log "Signing information is available."
        
        let assemblies = !! (outputNuGet @@ "*.*upkg")

        let signPath =
            let globalTool = tryFindFileOnPath "SignClient.exe"
            match globalTool with
                | Some t -> t
                | None -> if isWindows then findToolInSubPath "SignClient.exe" "tools/signclient"
                          elif isMacOS then findToolInSubPath "SignClient" "tools/signclient"
                          else findToolInSubPath "SignClient" "tools/signclient"

        let signAssembly assembly =
            let args = StringBuilder()
                    |> append "sign"
                    |> append "--config"
                    |> append (__SOURCE_DIRECTORY__ @@ "appsettings.json") 
                    |> append "-i"
                    |> append assembly
                    |> append "-r"
                    |> append (getBuildParam "SignClientUser")
                    |> append "-s"
                    |> append (getBuildParam "SignClientSecret")
                    |> append "-n"
                    |> append signingName
                    |> append "-d"
                    |> append signingDescription
                    |> append "-u"
                    |> append signingUrl
                    |> toText

            let result = ExecProcess(fun info -> 
                info.FileName <- signPath
                info.WorkingDirectory <- __SOURCE_DIRECTORY__
                info.Arguments <- args) (System.TimeSpan.FromMinutes 5.0) (* Reasonably long-running task. *)
            if result <> 0 then failwithf "SignClient failed.%s" args

        assemblies |> Seq.iter (signAssembly)
    else
        log "SignClientSecret not available. Skipping signing"
)

//--------------------------------------------------------------------------------
// Nuget targets 
//--------------------------------------------------------------------------------

let overrideVersionSuffix (project:string) =
    match project with
    | _ -> versionSuffix // add additional matches to publish different versions for different projects in solution
Target "CreateNuget" (fun _ ->    
    CreateDir outputNuGet // need this to stop Azure pipelines copy stage from error-ing out
    let projects = !! "src/**/*.csproj" 
                   -- "src/**/*Tests.csproj" // Don't publish unit tests
                   -- "src/**/*Tests*.csproj"

    let runSingleProject project =
        DotNetCli.Pack
            (fun p -> 
                { p with
                    Project = project
                    Configuration = configuration
                    AdditionalArgs = ["--include-symbols"]
                    VersionSuffix = versionSuffix
                    OutputPath = "\"" + outputNuGet + "\"" })

    projects |> Seq.iter (runSingleProject)
)

Target "PublishNuget" (fun _ ->
    let shouldPushNugetPackages = hasBuildParam "nugetkey"
    if not shouldPushNugetPackages then ()
    else
        let apiKey = getBuildParam "nugetkey"
        let sourceUrl = getBuildParamOrDefault "nugetpublishurl" "https://api.nuget.org/v3/index.json"
        
        let rec publishPackage retryLeft packageFile =
            tracefn "Pushing %s Attempts left: %d" (FullName packageFile) retryLeft
            let tracing = ProcessHelper.enableProcessTracing
            try
                try
                    ProcessHelper.enableProcessTracing <- false
                    DotNetCli.RunCommand
                        (fun p ->
                            { p with
                                TimeOut = TimeSpan.FromMinutes 10. })
                        (sprintf "nuget push %s --api-key %s --source %s --no-service-endpoint" packageFile apiKey sourceUrl)
                with exn ->
                    if (retryLeft > 0) then (publishPackage (retryLeft-1) packageFile)
            finally
                ProcessHelper.enableProcessTracing <- tracing
                
        printfn "Pushing nuget packages"
        let normalPackages = !! (outputNuGet @@ "*.nupkg") |> Seq.sortBy(fun x -> x.ToLower())
        for package in normalPackages do
            publishPackage 3 package
)

//--------------------------------------------------------------------------------
// Cleanup
//--------------------------------------------------------------------------------

FinalTarget "KillCreatedProcesses" (fun _ ->
    log "Shutting down dotnet build-server"
    let result = ExecProcess(fun info -> 
            info.FileName <- "dotnet"
            info.WorkingDirectory <- __SOURCE_DIRECTORY__
            info.Arguments <- "build-server shutdown") (System.TimeSpan.FromMinutes 2.0)
    if result <> 0 then failwithf "dotnet build-server shutdown failed"
)

//--------------------------------------------------------------------------------
// Help 
//--------------------------------------------------------------------------------

Target "Help" <| fun _ ->
    List.iter printfn [
      "usage:"
      "./build.ps1 [target]"
      ""
      " Targets for building:"
      " * Build           Builds"
      " * Nuget           Create and optionally publish nugets packages"
      " * SignPackages    Signs all NuGet packages, provided that the following arguments are passed into the script: SignClientSecret={secret} and SignClientUser={username}"
      " * RunTests        Runs Net Framework tests"
      " * RunTestsNetCore Runs Net Core tests"
      " * RunTestsNet     Runs Net 5 tests"
      " * All             Builds, run tests, creates and optionally publish nuget packages"
      " * DocFx           Creates a DocFx-based website for this solution"
      ""
      " Other Targets"
      " * Help            Display this help" 
      ""]

//--------------------------------------------------------------------------------
//  Target dependencies
//--------------------------------------------------------------------------------

Target "BuildRelease" DoNothing
Target "All" DoNothing
Target "Nuget" DoNothing
Target "RunTestsFull" DoNothing
Target "RunTestsNetFull" DoNothing

// build dependencies
"Clean" ==> "RestorePackages" ==> "AssemblyInfo" ==> "Build" ==> "BuildRelease"

// tests dependencies
"Build" ==> "RunTests"
"Build" ==> "RunTestsNet"

// nuget dependencies
"Clean" ==> "Build" ==> "CreateNuget" 
"CreateNuget" ==> "SignPackages" ==> "PublishNuget" ==> "Nuget"

// all
"BuildRelease" ==> "All"
"RunTests" ==> "All"
"RunTestsNet" ==> "All"
"NBench" ==> "All"
"Nuget" ==> "All"

RunTargetOrDefault "Help"