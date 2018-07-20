﻿namespace IfSharp.Kernel

open System
open System.Text
open System.Reflection
open System.Resources

module IfSharpResources = 
    let resources = new ResourceManager("IfSharpResources", Assembly.GetExecutingAssembly())
    
    let getString(name) =
         let array = resources.GetObject(name) :?> array<byte>
         Encoding.UTF8.GetString(array)

    let fsharp_css() = resources.GetString("fsharp_css")
    let kernel_js() = resources.GetString("kernel_js")
    let webintellisense_js() = resources.GetString("webintellisense")
    let webintellisense_codemirror_js() = resources.GetString("webintellisense-codemirror")
    let ipython_config() = getString("ipython_config")
    let ipython_qt_config() = getString("qtconsole_config")
    let ifsharp_kernel_json() = getString("kernel_json")