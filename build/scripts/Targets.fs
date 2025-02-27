// SPDX-License-Identifier: Apache-2.0
//
// The OpenSearch Contributors require contributions made to
// this file be licensed under the Apache-2.0 license or a
// compatible open source license.
//
// Modifications Copyright OpenSearch Contributors. See
// GitHub history for details.
//
//  Licensed to Elasticsearch B.V. under one or more contributor
//  license agreements. See the NOTICE file distributed with
//  this work for additional information regarding copyright
//  ownership. Elasticsearch B.V. licenses this file to you under
//  the Apache License, Version 2.0 (the "License"); you may
//  not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
// 	http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing,
//  software distributed under the License is distributed on an
//  "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
//  KIND, either express or implied.  See the License for the
//  specific language governing permissions and limitations
//  under the License.
//

namespace Scripts

open System
open System.IO

open Bullseye
open ProcNet
open Fake.Core
open Fake.IO.Globbing.Operators

module Main =

    let private target name action = Targets.Target(name, Action(action)) 
    let private skip name = printfn "SKIPPED target '%s' evaluated not to run" name |> ignore
    let private conditional name optional action = target name (if optional then action else (fun _ -> skip name)) 
    let private command name dependencies action = Targets.Target(name, dependencies, Action(action))
    let private conditionalCommand name dependencies optional action = command name dependencies (if optional then action else (fun _ -> skip name)) 
    
    /// <summary>Sets command line environments indicating we are building from the command line</summary>
    let setCommandLineEnvVars () =
        Environment.setEnvironVar"OSC_COMMAND_LINE_BUILD" "1"
        
        let sourceDir = Paths.TestsSource("Tests.Configuration");
        let defaultYaml = Path.Combine(sourceDir, "tests.default.yaml");
        let userYaml = Path.Combine(sourceDir, "tests.yaml");
        let e f = File.Exists f;
        match ((e userYaml), (e defaultYaml)) with
        | (true, _) -> Environment.setEnvironVar "OSC_YAML_FILE" (Path.GetFullPath(userYaml))
        | (_, true) -> Environment.setEnvironVar "OSC_YAML_FILE" (Path.GetFullPath(defaultYaml))
        | _ -> failwithf "Expected to find a tests.default.yaml or tests.yaml in %s" sourceDir
        
          
    let [<EntryPoint>] main args = 
        
        setCommandLineEnvVars ()
        
        let parsed = Commandline.parse (args |> Array.toList)
        
        let buildVersions = Versioning.BuildVersioning parsed
        let artifactsVersion = Versioning.ArtifactsVersion buildVersions
        Versioning.Validate parsed.Target buildVersions
        
        let isCanary = parsed.Target = "canary";
        
        Tests.SetTestEnvironmentVariables parsed
        
        let testChain = ["clean"; "version"; "restore"; "full-build"; ]
        let buildChain = ["test"; "inherit-doc" ]
        let releaseChain =
            [ 
                "build";
                "nuget-pack";
                "nuget-pack-versioned";
                "validate-artifacts";
                "generate-release-notes"
            ]
        let canaryChain = [ "version"; "release"; "test-nuget-package";]
        
        // the following are expected to be called as targets directly        
        conditional "clean" (parsed.ReleaseBuild || parsed.Target = "clean") <| fun _ -> Build.Clean parsed 
        target "version" <| fun _ -> printfn "Artifacts Version: %O" artifactsVersion
        
        target "restore" Build.Restore 
        target "full-build" <| fun _ -> Build.Compile parsed artifactsVersion

        //TEST
        conditionalCommand "test" testChain (not parsed.SkipTests && not isCanary) <| fun _ -> Tests.RunUnitTests parsed

        target "inherit-doc" <| InheritDoc.PatchInheritDocs
        
        //BUILD
        command "build" buildChain <| fun _ -> printfn "STARTING BUILD"

        target "nuget-pack" <| fun _ -> Build.Pack artifactsVersion

        conditional "nuget-pack-versioned" (isCanary) <| fun _ -> Build.VersionedPack artifactsVersion

        conditional "generate-release-notes" (not isCanary)  <| fun _ -> ReleaseNotes.GenerateNotes buildVersions
        
        target "validate-artifacts" <| fun _ -> Versioning.ValidateArtifacts artifactsVersion
        
        //RELEASE
        command "release" releaseChain <| fun _ ->
            let outputPath = match parsed.CommandArguments with | Commandline.SetVersion c -> c.OutputLocation | _ -> None
            match outputPath with
            | None ->
                printfn "Finished Release Build %O, artifacts available at: %s" artifactsVersion Paths.BuildOutput
            | Some path ->
                Fake.IO.Shell.cp_r Paths.BuildOutput path
                let zipName = sprintf "opencsearch-net-%O.zip" artifactsVersion.Full
                let outputZip = Path.Combine(path, zipName)
                let files = !! (sprintf "%s/*.*" path) -- outputZip
                Fake.IO.Zip.createZip "." outputZip "opensearch-project/opensearch-net artifact" 9 true files
                printfn "Finished Release Build %O, output copied to: %s" artifactsVersion path

        conditional "test-nuget-package" (not parsed.SkipTests) <| fun _ -> Tests.RunReleaseUnitTests artifactsVersion parsed
        
        //CANARY
        command "canary" canaryChain  <| fun _ -> printfn "Finished Release Build %O" artifactsVersion

        // ADDITIONAL COMMANDS
        
        command "cluster" [ "restore"; "full-build" ] <| fun _ ->
            ReposTooling.LaunchCluster parsed
        
        command "codegen" [ ] <| fun _ ->
            ReposTooling.GenerateApi parsed.RemainingArguments
        
        command "rest-spec-tests" [ ] <| fun _ ->
            ReposTooling.RestSpecTests parsed.RemainingArguments

        command "benchmark" [ "clean"; "full-build"; ] <| fun _ -> Benchmarker.Run parsed

        command "integrate" [ "clean"; "restore"; "full-build";] <| fun _ -> Tests.RunIntegrationTests parsed

        Targets.RunTargetsAndExit([parsed.Target], (fun e -> e.GetType() = typeof<ProcExecException>), ":")

        0

