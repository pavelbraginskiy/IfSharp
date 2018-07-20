namespace IfSharp.Kernel

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Threading

open Newtonsoft.Json


module App = 

    let mutable Kernel : Option<IfSharpKernel> = None

    /// Installs the ifsharp files if they do not exist
    let Install forceInstall = 

        let thisExecutable = Assembly.GetEntryAssembly().Location
        let kernelDir = Config.KernelDir
        let staticDir = Config.StaticDir
        let tempDir = Config.TempDir
        let customDir = Path.Combine(staticDir, "custom")
            
        let createDir(str) =
            if Directory.Exists(str) = false then
                Directory.CreateDirectory(str) |> ignore

        createDir kernelDir
        createDir staticDir
        createDir tempDir
        createDir customDir

        let allFiles = new System.Collections.Generic.List<string>()
        let addFile fn = allFiles.Add(fn); fn
        let configFile = Path.Combine(kernelDir, "ipython_config.py") |> addFile
        let configqtFile = Path.Combine(kernelDir, "ipython_qtconsole_config.py") |> addFile
        let kernelFile = Path.Combine(kernelDir, "kernel.json") |> addFile
        let kjsFile = Path.Combine(kernelDir, "kernel.js") |> addFile
        let wjsFile = Path.Combine(customDir, "webintellisense.js") |> addFile
        let wcjsFile = Path.Combine(customDir, "webintellisense-codemirror.js") |> addFile
        let versionFile = Path.Combine(kernelDir, "version.txt") |> addFile
        let missingFiles = Seq.exists (fun fn -> File.Exists(fn) = false) allFiles
        
        let differentVersion = File.Exists(versionFile) && File.ReadAllText(versionFile) <> Config.Version

        if forceInstall then printfn "Force install required, performing install..."
        else if missingFiles then printfn "One or more files are missing, performing install..."
        else if differentVersion then printfn "Different version found, performing install..."

        if forceInstall || missingFiles || differentVersion then
            
            // write the version file
            File.WriteAllText(versionFile, Config.Version);

            // write the startup script
            let codeTemplate = IfSharpResources.ipython_config()
            let code = 
              match Environment.OSVersion.Platform with
                | PlatformID.Win32Windows | PlatformID.Win32NT -> codeTemplate.Replace("\"mono\",", "")
                | _ -> codeTemplate
            let code = code.Replace("%kexe", thisExecutable)
            let code = code.Replace("%kstatic", staticDir)
            printfn "Saving custom config file [%s]" configFile
            File.WriteAllText(configFile, code)

            let codeqt = IfSharpResources.ipython_qt_config()
            printfn "Saving custom qt config file [%s]" codeqt
            File.WriteAllText(configqtFile, codeqt)


            // write fsharp css file
            let cssFile = Path.Combine(customDir, "fsharp.css")
            printfn "Saving fsharp css [%s]" cssFile
            File.WriteAllText(cssFile, IfSharpResources.fsharp_css())

            // write kernel js file
            printfn "Saving kernel js [%s]" kjsFile
            File.WriteAllText(kjsFile, IfSharpResources.kernel_js())

            // write webintellisense js file
            printfn "Saving webintellisense js [%s]" wjsFile
            File.WriteAllText(wjsFile, IfSharpResources.webintellisense_js())

            // write webintellisense-codemirror js file
            printfn "Saving webintellisense-codemirror js [%s]" wcjsFile
            File.WriteAllText(wcjsFile, IfSharpResources.webintellisense_codemirror_js())

            // Make the Kernel info folder 
            let jsonTemplate = IfSharpResources.ifsharp_kernel_json()
            let code = 
              match Environment.OSVersion.Platform with
                | PlatformID.Win32Windows -> jsonTemplate.Replace("\"mono\",", "")
                | PlatformID.Win32NT -> jsonTemplate.Replace("\"mono\",", "")
                | _ -> jsonTemplate
            let code = code.Replace("%s", thisExecutable.Replace("\\","\/"))
            printfn "Saving custom kernel.json file [%s]" kernelFile
            File.WriteAllText(kernelFile, code)

            printfn "Installing dependencies via Paket"
            let dependencies = Paket.Dependencies.Locate(System.IO.Path.GetDirectoryName(thisExecutable))
            dependencies.Install(false)

    /// Starts jupyter in the user's home directory
    let StartJupyter () =

        let userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        printfn "Starting Jupyter..."
        let p = new Process()
        p.StartInfo.FileName <- "jupyter"
        p.StartInfo.Arguments <- "notebook"
        p.StartInfo.WorkingDirectory <- userDir

        // tell the user something bad happened

        try
            if p.Start() = false then failwith "Unable to start Jupyter, please install Jupyter and ensure it is on the path"
        with _ -> failwith "Unable to start Jupyter, please install Jupyter and ensure it is on the path"

    /// First argument must be an Jupyter connection file, blocks forever
    let Start (args : array<string>) = 

        match args with
        | [||] ->
            Install true
            StartJupyter() //Eventually Jupyter will call back in with the connection file

        | [|"--install"|] ->
            Install true

        | _ ->
            // Verify kernel installation status
            Install false

            // Clear the temporary folder
            try
              if Directory.Exists(Config.TempDir) then Directory.Delete(Config.TempDir, true)
            with exc -> Console.Out.Write(exc.ToString())


            // get connection information
            let fileName = args.[0]
            let json = File.ReadAllText(fileName)
            let connectionInformation = JsonConvert.DeserializeObject<ConnectionInformation>(json)

            // start the kernel
            Kernel <- Some (IfSharpKernel(connectionInformation))
            Kernel.Value.StartAsync()

            // block forever
            Thread.Sleep(Timeout.Infinite)
