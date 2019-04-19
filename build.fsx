// $begin{copyright}
//
// This file is part of Bolero
//
// Copyright (c) 2018 IntelliFactory and contributors
//
// Licensed under the Apache License, Version 2.0 (the "License"); you
// may not use this file except in compliance with the License.  You may
// obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
// implied.  See the License for the specific language governing
// permissions and limitations under the License.
//
// $end{copyright}

#r "paket: groupref fake //"
#load "tools/Utility.fsx"

open System.IO
open System.Net
open System.Text
open System.Text.RegularExpressions
open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO.FileSystemOperators
open Utility

let config = getArg "-c" "Debug"
let version = getArg "-v" "0.1.0"
let testUploadUrl = getArgOpt "--push-tests"
let verbosity = getFlag "--verbose" >> function
    | true -> "n"
    | false -> "m"
let buildArgs o =
    sprintf "-c:%s -v:%s" (config o) (verbosity o)

Target.description "Run the compilation phase proper"
Target.create "corebuild" (fun o ->
    dotnet "build" "Bolero.sln %s" (buildArgs o)
)

let [<Literal>] tagsFile = slnDir + "/src/Bolero/tags.csv"
type Tags = FSharp.Data.CsvProvider<tagsFile>
let [<Literal>] attrsFile = slnDir + "/src/Bolero/attrs.csv"
type Attrs = FSharp.Data.CsvProvider<attrsFile>
let [<Literal>] eventsFile = slnDir + "/src/Bolero/events.csv"
type Events = FSharp.Data.CsvProvider<eventsFile>

let escapeDashes s =
    Regex("-(.)").Replace(s, fun (m: Match) ->
        m.Groups.[1].Value.ToUpperInvariant())

let replace rows marker writeItem input =
    Regex(sprintf """(?<=// BEGIN %s\r?\n)(?:\w|\W)*(?=// END %s)""" marker marker,
        RegexOptions.Multiline)
        .Replace(input, fun _ ->
            let s = new StringBuilder()
            for tag in rows do
                writeItem s tag |> ignore
            s.ToString()
        )

let runTags filename apply =
    let input = File.ReadAllText(filename)
    let output = apply input
    if input <> output then
        File.WriteAllText(filename, output)

Target.description "Generate HTML tags and attributes from CSV"
Target.create "tags" (fun _ ->
    runTags "src/Bolero/Html.fs" (
        replace (Tags.GetSample().Rows) "TAGS" (fun s tag ->
            let esc = escapeDashes tag.Name
            let ident = if tag.NeedsEscape then "``" + esc + "``" else esc
            let childrenArg = if tag.CanHaveChildren then " (children: list<Node>)" else ""
            let childrenVal = if tag.CanHaveChildren then "children" else "[]"
            s.AppendLine(sprintf """/// Create an HTML `<%s>` element.""" tag.Name)
             .AppendLine(sprintf """let %s (attrs: list<Attr>)%s : Node =""" ident childrenArg)
             .AppendLine(sprintf """    elt "%s" attrs %s""" tag.Name childrenVal)
             .AppendLine()
        )
        >> replace (Attrs.GetSample().Rows) "ATTRS" (fun s attr ->
            let esc = escapeDashes attr.Name
            let ident = if attr.NeedsEscape then "``" + esc + "``" else esc
            s.AppendLine(sprintf """    /// Create an HTML `%s` attribute.""" attr.Name)
             .AppendLine(sprintf """    let %s (v: obj) : Attr = "%s" => v""" ident attr.Name)
             .AppendLine()
        )
        >> replace (Events.GetSample().Rows) "EVENTS" (fun s event ->
            let esc = escapeDashes event.Name
            s.AppendLine(sprintf """    /// Create a handler for HTML event `%s`.""" event.Name)
             .AppendLine(sprintf """    let %s (callback: UI%sEventArgs -> unit) : Attr =""" esc event.Type)
             .AppendLine(sprintf """        "on%s" => BindMethods.GetEventHandlerValue callback""" esc)
             .AppendLine()
        )
    )
    runTags "src/Bolero.Templating/Parsing.fs" (
        replace (Events.GetSample().Rows) "EVENTS" (fun s event ->
            if event.Type <> "" then
                s.AppendLine(sprintf """        | "on%s" -> typeof<UI%sEventArgs>""" event.Name event.Type)
            else
                s
        )
    )
)

Target.description "Run a full compilation"
Target.create "build" (fun _ ->
    dotnet "build-server" "shutdown" // Using this to avoid locking of the output dlls
)

Target.description "Create the NuGet packages"
Target.create "pack" (fun o ->
    Fake.DotNet.Paket.pack (fun p ->
        { p with
            OutputPath = "build"
            Version = version o
            ToolPath = ".paket/paket"
        }
    )
)

Target.description "Run the Client test project"
Target.create "run-client" (fun o ->
    dotnet' "tests/Client" [] "run" "%s" (buildArgs o)
)

Target.description "Run the Server test project"
Target.create "run-server" (fun o ->
    dotnet' "tests/Server" [] "run" "%s" (buildArgs o)
)

Target.description "Run the Remoting test project"
Target.create "run-remoting" (fun o ->
    dotnet' "tests/Remoting.Server" [] "run" "%s" (buildArgs o)
)

let uploadTests (url: string) =
    let results =
        DirectoryInfo(slnDir </> "tests" </> "Unit" </> "TestResults")
            .EnumerateFiles("*.trx")
        |> Seq.maxBy (fun f -> f.CreationTime)
    use c = new WebClient()
    c.UploadFile(url, results.FullName) |> ignore

Target.description "Run the unit tests"
Target.create "test" (fun o ->
    try dotnet' "tests/Unit" [] "test" "--logger:trx %s" (buildArgs o)
    finally Option.iter uploadTests (testUploadUrl o)
)

Target.description "Run the unit tests waiting for a debugger to connect"
Target.create "test-debug" (fun o ->
    dotnet' "tests/Unit" ["VSTEST_HOST_DEBUG", "1"] "test" "%s" (buildArgs o)
)

"corebuild"
    ==> "build"
    ==> "pack"

"build" ==> "run-client"
"build" ==> "run-server"
"build" ==> "run-remoting"

"build" ?=> "test"
"build" ?=> "test-debug"

Target.runOrDefaultWithArguments "build"
