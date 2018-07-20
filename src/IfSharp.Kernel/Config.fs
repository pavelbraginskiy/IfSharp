﻿module Config

open System
open System.Configuration
open System.IO

/// Convenience method for getting a setting with a default value
let defaultConfig (name : string, defaultValue) =
    let value = ConfigurationManager.AppSettings.[name]
    if value = null then defaultValue else value

// the configuration properties
let DefaultNuGetSource = defaultConfig("DefaultNuGetSource", "")

let ActualPlatform =
    match Environment.OSVersion.Platform with
    | PlatformID.Unix ->
        //Mono pretends MacOSX is Unix, undo this by heuristic
        if (Directory.Exists("/Applications")
           && Directory.Exists("/System")
           && Directory.Exists("/Users")
           && Directory.Exists("/Volumes"))
        then
            PlatformID.MacOSX
        else
            PlatformID.Unix
    | p -> p

//http://jupyter-client.readthedocs.io/en/latest/kernels.html#kernel-specs
let KernelDir = 
  let thisExecutable = System.Reflection.Assembly.GetEntryAssembly().Location
  let userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
  let appData =  
    match ActualPlatform with
      | PlatformID.Win32Windows | PlatformID.Win32NT -> Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
      | PlatformID.MacOSX -> Path.Combine(userDir, "Library")
      | PlatformID.Unix -> Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) // PlatformID.Unix
      | p -> failwithf "Unknown platform: %A" p
  let jupyterDir = 
    match Environment.OSVersion.Platform with 
      | PlatformID.Unix -> Path.Combine(appData, "jupyter")
      | _ -> Path.Combine(appData, "Jupyter")
  let kernelsDir = Path.Combine(jupyterDir, "kernels")
  let kernelDir = Path.Combine(kernelsDir, "iqsharp")
  kernelDir
let StaticDir = Path.Combine(KernelDir, "static")
let TempDir = Path.Combine(StaticDir, "temp")

let Version = "1"